using System;
using System.Collections.Concurrent;
using System.Numerics;
using TerrainDiffusion.Water.Grid;
using TerrainDiffusion.Water.Numerics;

namespace TerrainDiffusion.Water.Meander;

/// <summary>
/// The heading each land cell aims its water at: downhill, swung side to side by a smooth field, by as much as
/// the terrain lets a river wander.
/// </summary>
/// <remarks>
/// <para>A river whose heading swings as <c>θ = ω sin(2π s / L)</c> along its length (the sine-generated curve of
/// Langbein and Leopold, which real meanders follow closely) has sinuosity <c>1 / J₀(ω)</c>. Here
/// <c>θ = ω · n(row, column)</c>, with n a smooth field of unit spread (a sum of plane waves, in world
/// coordinates, see <see cref="MeanderTables"/>), and ω is set per cell so that the expected sinuosity
/// <c>1 / E[cos(ω n)]</c> equals a target that depends on the terrain:</para>
/// <code>
///   S = 1 + (MaxSinuosity − 1) / (1 + (slope / SlopeScale)²) / (1 + (relief / ReliefScaleMetres)²)
/// </code>
/// <para>slope is the local gradient of the filled surface (Horn's 3 × 3 operator) and relief the r.m.s. height
/// of the ground about its smoothed self within about <see cref="MeanderSettings.ConfinementMetres"/> (valley
/// walls, ridges, hills: what holds a river in). Steep or confined ground gets S near 1; a wide gentle plain
/// gets MaxSinuosity.</para>
/// <para>Where the relief is over <c>FreeReliefRatio × ReliefScaleMetres</c>, or the ground is flat, a cell
/// has no heading and keeps plain steepest descent. Elsewhere the heading also removes the 0°/45° bias of
/// steepest descent on an even grade (the dither keeps any angle on average). The swing is bounded by the
/// strictly-downhill rule of <see cref="Routing.FlowDirectionSolver"/>, so a river never turns back uphill, and
/// a valley narrower than its meanders keeps it straight on its own.</para>
/// <para>A cell's heading depends only on its world position and the fill within <see cref="PaddingFor"/>
/// cells of its tile, so it is the same whatever order the world's tiles arrive in.</para>
/// <para>Thread safety: an instance may be used from several threads at once.</para>
/// </remarks>
public sealed class MeanderField
{
    /// <summary>Gradient (metres per metre) at or below which a cell counts as flat: no heading.</summary>
    private const double FlatGradient = 1e-9;

    /// <summary>The relief filters are cut off at this many standard deviations.</summary>
    private const double FilterTruncate = 2.0;

    /// <summary>The settings.</summary>
    private readonly MeanderSettings settings;
    /// <summary>Cell size in metres.</summary>
    private readonly double cellSizeMetres;
    /// <summary>The relief smoothing scale in cells (at least 1).</summary>
    private readonly double sigmaCells;
    /// <summary>The relief filter for each padding seen (its sigma is capped by the padding).</summary>
    private readonly ConcurrentDictionary<int, GaussianFilter> filtersByPadding = new();

    /// <summary>A field laid out from the seed (see <see cref="MeanderTables.Generate"/>).</summary>
    /// <param name="settings">Meander settings.</param>
    /// <param name="cellSizeMetres">Cell size in metres.</param>
    /// <param name="seed">World seed.</param>
    public MeanderField(MeanderSettings settings, double cellSizeMetres, uint seed)
        : this(settings, cellSizeMetres, seed, MeanderTables.Generate(settings, cellSizeMetres, seed))
    {
    }

    /// <summary>A field with explicit tables.</summary>
    /// <param name="settings">Meander settings.</param>
    /// <param name="cellSizeMetres">Cell size in metres.</param>
    /// <param name="seed">World seed (for the dither).</param>
    /// <param name="tables">The waves and the sinuosity table.</param>
    public MeanderField(MeanderSettings settings, double cellSizeMetres, uint seed, MeanderTables tables)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tables);
        settings.Validate();
        this.settings = settings;
        this.cellSizeMetres = cellSizeMetres;
        Seed = seed;
        Tables = tables;
        sigmaCells = Math.Max(1.0, settings.ConfinementMetres / cellSizeMetres);
    }

    /// <summary>The world seed.</summary>
    public uint Seed { get; }

    /// <summary>The waves and the sinuosity table.</summary>
    public MeanderTables Tables { get; }

    /// <summary>Cells of fill needed around a tile of <paramref name="tileSize"/> to work out its headings: four
    /// relief scales plus two, at most one tile.</summary>
    /// <param name="tileSize">Tile side in cells.</param>
    /// <returns>Padding in cells.</returns>
    public int PaddingFor(int tileSize) => Math.Min(tileSize, (int)Math.Ceiling(4.0 * sigmaCells) + 2);

    /// <summary>
    /// Headings, dither, swing and target sinuosity of the <paramref name="tileSize"/>² cells of a tile, from the fill
    /// around it.
    /// </summary>
    /// <param name="fill">Water level (fill) of the tile and <paramref name="padding"/> cells around it,
    /// row-major, <c>(tileSize + 2 · padding)²</c> values, all finite.</param>
    /// <param name="padding">Padding of <paramref name="fill"/>; normally <see cref="PaddingFor"/>.</param>
    /// <param name="tileSize">Tile side in cells.</param>
    /// <param name="worldRow">World row of the tile's first cell.</param>
    /// <param name="worldColumn">World column of the tile's first cell.</param>
    /// <param name="output">Receives the results (sized for <paramref name="tileSize"/>).</param>
    /// <param name="workspace">Scratch memory; not shared between threads.</param>
    public void ComputeHeadings(ReadOnlySpan<double> fill, int padding, int tileSize, long worldRow, long worldColumn,
                                MeanderHeadings output, MeanderWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(workspace);
        int side = tileSize + 2 * padding;
        if (padding < 1 || fill.Length != side * side || output.TileSize != tileSize)
        {
            throw new ArgumentException("The fill must be (tileSize + 2 · padding)² values, padding ≥ 1, and the output sized for the tile.");
        }
        workspace.EnsureCapacity(side, tileSize);

        // Ground height: the fill, with the sea at 0
        Span<double> ground = workspace.Ground.AsSpan(0, side * side);
        for (int i = 0; i < ground.Length; i++)
        {
            ground[i] = Math.Max(fill[i], 0.0);
        }

        ComputeGradient(ground, side, padding, tileSize, workspace.GradientRow, workspace.GradientColumn);
        ComputeRelief(ground, side, padding, tileSize, workspace);
        ComputeField(worldRow, worldColumn, tileSize, workspace);

        double slopeScale = settings.SlopeScale, reliefScale = settings.ReliefScaleMetres;
        double freeRelief = settings.FreeReliefRatio * reliefScale;
        double excess = settings.MaxSinuosity - 1.0;
        for (int row = 0; row < tileSize; row++)
        {
            long wr = worldRow + row;
            for (int column = 0; column < tileSize; column++)
            {
                int i = row * tileSize + column;
                long wc = worldColumn + column;
                double gy = workspace.GradientRow[i], gx = workspace.GradientColumn[i];
                double relief = workspace.Relief[i];
                double slope = double.Hypot(gx, gy);
                double slopeTerm = slope / slopeScale;
                double reliefTerm = relief / reliefScale;
                double target = 1.0 + excess / (1.0 + slopeTerm * slopeTerm) / (1.0 + reliefTerm * reliefTerm);
                output.Sinuosity[i] = target;
                output.Dither[i] = CellHash.UnitInterval(wr, wc, Seed);
                if (relief > freeRelief)
                {
                    output.Heading[i] = double.NaN;           // held in: steepest descent
                    output.Swing[i] = double.NaN;
                    continue;
                }
                double swing = Tables.SwingFor(target) * workspace.Field[i];
                output.Swing[i] = swing;
                // flat: steepest descent here; FlowDirectionSolver turns its way across the flat by the swing
                output.Heading[i] = slope <= FlatGradient ? double.NaN : Math.Atan2(-gy, -gx) + swing;
            }
        }
    }

    /// <summary>
    /// The swing field at the tile's cells (<see cref="MeanderTables.FieldAt"/>), with each wave split by angle
    /// addition, <c>sin(a + b) = sin a cos b + cos a sin b</c>, into a row part and a column part computed once per
    /// row and column: two multiplications per wave and cell instead of a sine. Agrees with the direct sum to
    /// rounding (about 1e-15 relative near the origin, growing with the angle far out).
    /// </summary>
    private void ComputeField(long worldRow, long worldColumn, int tileSize, MeanderWorkspace workspace)
    {
        ReadOnlySpan<PlaneWave> waves = Tables.Waves;
        int count = waves.Length;
        workspace.EnsureFieldCapacity(count, tileSize);
        Span<double> rowSin = workspace.RowSin, rowCos = workspace.RowCos;
        Span<double> columnSin = workspace.ColumnSin, columnCos = workspace.ColumnCos;
        for (int w = 0; w < count; w++)
        {
            PlaneWave wave = waves[w];
            for (int k = 0; k < tileSize; k++)
            {
                (rowSin[w * tileSize + k], rowCos[w * tileSize + k]) = Math.SinCos(wave.RowWavenumber * (worldRow + k) + wave.Phase);
                (columnSin[w * tileSize + k], columnCos[w * tileSize + k]) = Math.SinCos(wave.ColumnWavenumber * (worldColumn + k));
            }
        }

        double scale = Tables.Normalisation;
        for (int row = 0; row < tileSize; row++)
        {
            Span<double> field = workspace.Field.AsSpan(row * tileSize, tileSize);
            field.Clear();
            for (int w = 0; w < count; w++)
            {
                double s = rowSin[w * tileSize + row], c = rowCos[w * tileSize + row];
                ReadOnlySpan<double> cosB = columnCos.Slice(w * tileSize, tileSize);
                ReadOnlySpan<double> sinB = columnSin.Slice(w * tileSize, tileSize);
                int x = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    var vs = new Vector<double>(s);
                    var vc = new Vector<double>(c);
                    for (; x <= tileSize - Vector<double>.Count; x += Vector<double>.Count)
                    {
                        (new Vector<double>(field[x..]) + (vs * new Vector<double>(cosB[x..]) + vc * new Vector<double>(sinB[x..])))
                            .CopyTo(field[x..]);
                    }
                }
                for (; x < tileSize; x++)
                {
                    field[x] += s * cosB[x] + c * sinB[x];
                }
            }
            for (int x = 0; x < tileSize; x++)
            {
                field[x] = Math.Clamp(field[x] * scale, -MeanderTables.FieldClip, MeanderTables.FieldClip);
            }
        }
    }

    /// <summary>Horn's 3 × 3 gradient (metres per metre) of the ground at the tile's cells.</summary>
    private void ComputeGradient(ReadOnlySpan<double> ground, int side, int padding, int tileSize,
                                 Span<double> gradientRow, Span<double> gradientColumn)
    {
        double denominator = 8.0 * cellSizeMetres;
        for (int row = 0; row < tileSize; row++)
        {
            // g(a, b): ground at tile cell (row + a − 1, column + b − 1)
            int above = (padding + row - 1) * side + padding - 1;
            int middle = above + side;
            int below = middle + side;
            for (int column = 0; column < tileSize; column++)
            {
                double nw = ground[above + column], n = ground[above + column + 1], ne = ground[above + column + 2];
                double w = ground[middle + column], e = ground[middle + column + 2];
                double sw = ground[below + column], s = ground[below + column + 1], se = ground[below + column + 2];
                int i = row * tileSize + column;
                gradientColumn[i] = ((ne + 2 * e + se) - (nw + 2 * w + sw)) / denominator;
                gradientRow[i] = ((sw + 2 * s + se) - (nw + 2 * n + ne)) / denominator;
            }
        }
    }

    /// <summary>R.m.s. relief at the tile's cells: <c>sqrt(blur((g − blur(g))²))</c>.</summary>
    private void ComputeRelief(ReadOnlySpan<double> ground, int side, int padding, int tileSize,
                               MeanderWorkspace workspace)
    {
        GaussianFilter filter = filtersByPadding.GetOrAdd(padding, p =>
        {
            double sigma = p > 2 ? Math.Min(sigmaCells, (p - 2) / 4.0) : 0.5;
            return new GaussianFilter(sigma, FilterTruncate);
        });
        int cells = side * side;
        Span<double> smooth = workspace.Smooth.AsSpan(0, cells);
        Span<double> squared = workspace.Squared.AsSpan(0, cells);
        Span<double> scratch = workspace.Scratch.AsSpan(0, cells);

        GridRegion tile = GridRegion.Square(padding, tileSize);
        GridRegion highPass = filter.InputRegion(tile, side, side);        // where the second blur reads
        filter.Apply(ground, side, side, highPass, smooth, scratch);
        squared.Clear();
        for (int row = highPass.RowStart; row < highPass.RowEnd; row++)
        {
            for (int column = highPass.ColumnStart; column < highPass.ColumnEnd; column++)
            {
                int i = row * side + column;
                double detail = ground[i] - smooth[i];
                squared[i] = detail * detail;
            }
        }
        filter.Apply(squared, side, side, tile, smooth, scratch);
        for (int row = 0; row < tileSize; row++)
        {
            int source = (padding + row) * side + padding;
            for (int column = 0; column < tileSize; column++)
            {
                workspace.Relief[row * tileSize + column] = Math.Sqrt(smooth[source + column]);
            }
        }
    }
}

/// <summary>Per-cell results of <see cref="MeanderField.ComputeHeadings"/> for one tile, row-major.</summary>
public sealed class MeanderHeadings
{
    /// <summary>Arrays for tiles of <paramref name="tileSize"/> cells.</summary>
    /// <param name="tileSize">Tile side in cells.</param>
    public MeanderHeadings(int tileSize)
    {
        TileSize = tileSize;
        Heading = new double[tileSize * tileSize];
        Dither = new double[tileSize * tileSize];
        Sinuosity = new double[tileSize * tileSize];
        Swing = new double[tileSize * tileSize];
    }

    /// <summary>Tile side in cells.</summary>
    public int TileSize { get; }

    /// <summary>Heading in radians (<c>atan2(rowStep, columnStep)</c>); NaN where the cell keeps steepest
    /// descent.</summary>
    public double[] Heading { get; }

    /// <summary>The cell's dither in [0, 1) (<see cref="CellHash"/>).</summary>
    public double[] Dither { get; }

    /// <summary>Target sinuosity (≥ 1).</summary>
    public double[] Sinuosity { get; }

    /// <summary>The turn off downhill in radians (the swing amplitude for the target sinuosity times the field);
    /// NaN where the relief holds the river in. A cell on a slope already has it in <see cref="Heading"/>; a cell
    /// of a flat adds it to its way across the flat.</summary>
    public double[] Swing { get; }
}

/// <summary>Scratch memory for <see cref="MeanderField.ComputeHeadings"/>, reused between tiles. Not thread
/// safe: one per thread.</summary>
public sealed class MeanderWorkspace
{
    /// <summary>Ground height (fill, sea at 0) over the padded tile.</summary>
    internal double[] Ground = Array.Empty<double>();
    /// <summary>The first blur of the ground, then the blurred squared detail.</summary>
    internal double[] Smooth = Array.Empty<double>();
    /// <summary>Squared detail (ground minus its blur), where the second blur reads it.</summary>
    internal double[] Squared = Array.Empty<double>();
    /// <summary>Intermediate of the separable blur.</summary>
    internal double[] Scratch = Array.Empty<double>();
    /// <summary>Horn gradient along rows (Z) per tile cell, metres per metre.</summary>
    internal double[] GradientRow = Array.Empty<double>();
    /// <summary>Horn gradient along columns (X) per tile cell, metres per metre.</summary>
    internal double[] GradientColumn = Array.Empty<double>();
    /// <summary>R.m.s. relief per tile cell, metres.</summary>
    internal double[] Relief = Array.Empty<double>();
    /// <summary>Swing field per tile cell.</summary>
    internal double[] Field = Array.Empty<double>();
    /// <summary>Per wave and tile row, sin of the row part of its phase.</summary>
    internal double[] RowSin = Array.Empty<double>();
    /// <summary>Per wave and tile row, cos of the row part of its phase.</summary>
    internal double[] RowCos = Array.Empty<double>();
    /// <summary>Per wave and tile column, sin of the column part of its phase.</summary>
    internal double[] ColumnSin = Array.Empty<double>();
    /// <summary>Per wave and tile column, cos of the column part of its phase.</summary>
    internal double[] ColumnCos = Array.Empty<double>();

    /// <summary>Grows the field buffers for <paramref name="waves"/> waves on a tile of
    /// <paramref name="tileSize"/>.</summary>
    internal void EnsureFieldCapacity(int waves, int tileSize)
    {
        if (RowSin.Length < waves * tileSize)
        {
            RowSin = new double[waves * tileSize];
            RowCos = new double[waves * tileSize];
            ColumnSin = new double[waves * tileSize];
            ColumnCos = new double[waves * tileSize];
        }
    }

    /// <summary>Grows the buffers for a padded side of <paramref name="side"/> and a tile of
    /// <paramref name="tileSize"/>.</summary>
    internal void EnsureCapacity(int side, int tileSize)
    {
        int cells = side * side, tileCells = tileSize * tileSize;
        if (Ground.Length < cells)
        {
            Ground = new double[cells];
            Smooth = new double[cells];
            Squared = new double[cells];
            Scratch = new double[cells];
        }
        if (Relief.Length < tileCells)
        {
            GradientRow = new double[tileCells];
            GradientColumn = new double[tileCells];
            Relief = new double[tileCells];
            Field = new double[tileCells];
        }
    }
}
