using System;
using System.Diagnostics;
using TerrainDiffusion.Water.Grid;
using TerrainDiffusion.Water.Meander;
using TerrainDiffusion.Water.Routing;

namespace TerrainDiffusion.Water.World;

/// <summary>What routing one tile did (merged into the statistics by the caller).</summary>
/// <param name="Routed">Whether the tile got its directions.</param>
/// <param name="TerminalFlats">Lakes without an outlet found.</param>
/// <param name="EscapedCells">Cells of flats wider than 5 × 5 tiles, turned into sinks.</param>
/// <param name="SinuositySum">Target sinuosity summed over the tile's land cells.</param>
/// <param name="LandCells">Land cells of the tile.</param>
/// <param name="MeanderTicks">Stopwatch ticks spent on headings.</param>
internal readonly record struct RouteOutcome(bool Routed, int TerminalFlats, int EscapedCells, double SinuositySum,
                                             int LandCells, long MeanderTicks)
{
    /// <summary>Nothing done: the fill the tile needs is not all there.</summary>
    public static RouteOutcome NotReady => default;
}

/// <summary>
/// Stage 2 of a tile: its direction codes (see <see cref="FlowDirectionSolver"/>), aimed by the meander
/// headings, and its <see cref="TileFlowNetwork"/>. Needs the fill of the 3 × 3 tiles around it, or of the
/// 5 × 5 when one of its flats does not fit in 3 × 3.
/// </summary>
/// <remarks>Thread safe: several tiles may be routed at once (each writes only its own state).</remarks>
internal sealed class RoutingStage
{
    /// <summary>The tiles.</summary>
    private readonly TileStore store;
    /// <summary>Tile tables.</summary>
    private readonly TileGeometry geometry;
    /// <summary>Headings; null for steepest descent.</summary>
    private readonly MeanderField? meander;
    /// <summary>How much more a flat costs to cross at its own level than over its lowest ground.</summary>
    private readonly double flatGroundWeight;
    /// <summary>Scratch per concurrent route.</summary>
    private readonly ObjectPool<Workspace> workspaces;
    /// <summary>Cells of a 3 × 3-tile window: scratch for a 5 × 5 one is freed after use.</summary>
    private readonly int baseWindowCells;

    /// <summary>A routing stage reading from <paramref name="store"/>.</summary>
    /// <param name="store">The tiles.</param>
    /// <param name="geometry">Tile tables.</param>
    /// <param name="meander">Headings; null for steepest descent.</param>
    /// <param name="flatGroundWeight">See <see cref="RiverGraphSettings.FlatGroundWeight"/>.</param>
    /// <param name="maxRetained">Workspaces kept between routes (the most routes that run at once).</param>
    public RoutingStage(TileStore store, TileGeometry geometry, MeanderField? meander, double flatGroundWeight,
                        int maxRetained)
    {
        this.store = store;
        this.geometry = geometry;
        this.meander = meander;
        this.flatGroundWeight = flatGroundWeight;
        workspaces = new ObjectPool<Workspace>(() => new Workspace(geometry.Size), maxRetained);
        baseWindowCells = 9 * geometry.CellCount;
    }

    /// <summary>Routes <paramref name="state"/> if the fill it needs is there (the caller has checked it is
    /// filled and not routed).</summary>
    /// <param name="state">The tile.</param>
    /// <returns>What was done.</returns>
    public RouteOutcome TryRoute(TileState state)
    {
        Workspace workspace = workspaces.Rent();
        try
        {
            return TryRoute(state, workspace);
        }
        finally
        {
            workspace.Trim(baseWindowCells);         // a 5 × 5 window's scratch is about three times the usual
            workspaces.Return(workspace);
        }
    }

    /// <summary>Routes in the 3 × 3 tiles, or the 5 × 5 when a flat does not fit, computing the headings once.</summary>
    private RouteOutcome TryRoute(TileState state, Workspace workspace)
    {
        int t = geometry.Size;
        bool headingsDone = false;
        double sinuositySum = 0.0;
        int landCells = 0;
        long meanderTicks = 0;
        while (true)
        {
            int radius = state.IsWide ? 2 : 1;
            if (!store.AllWithin(state.Tile, radius, s => s.IsFilled))
            {
                return RouteOutcome.NotReady with { SinuositySum = sinuositySum, LandCells = landCells, MeanderTicks = meanderTicks };
            }
            int side = (2 * radius + 1) * t;
            Span<float> window = workspace.WindowFor(side);
            store.CopyWindow(state.Tile, radius * t, s => s.IsFilled ? s.Level : null, window, float.NaN);
            Span<float> ground = workspace.GroundFor(side);
            store.CopyWindow(state.Tile, radius * t, s => s.Elevation, ground, float.NaN);

            // The headings depend only on the fill within the meander padding: the same in either window
            if (meander is not null && !headingsDone)
            {
                long start = Stopwatch.GetTimestamp();
                LandSinuosity land = ComputeHeadings(state.Tile, window, side, radius * t, workspace);
                (sinuositySum, landCells) = (land.Sum, land.Cells);
                meanderTicks = Stopwatch.GetTimestamp() - start;
                headingsDone = true;
            }

            var directions = new sbyte[t * t];
            ReadOnlySpan<double> heading = meander is null ? ReadOnlySpan<double>.Empty : workspace.Headings.Heading;
            ReadOnlySpan<double> dither = meander is null ? ReadOnlySpan<double>.Empty : workspace.Headings.Dither;
            ReadOnlySpan<double> swing = meander is null ? ReadOnlySpan<double>.Empty : workspace.Headings.Swing;
            int terminal = workspace.Solver.Solve(window, ground, side, radius * t, radius * t, t, heading, dither,
                                                  swing, flatGroundWeight, directions);

            int escaped = 0;
            for (int i = 0; i < directions.Length; i++)
            {
                if (directions[i] == D8.Escape)
                {
                    escaped++;
                }
            }
            if (escaped > 0 && radius == 1)
            {
                state.IsWide = true;          // a flat leaves the 3 × 3 tiles: try again with 5 × 5
                continue;
            }
            if (escaped > 0)
            {
                // A flat wider than 5 × 5 tiles: its cells here end (no cycles)
                for (int i = 0; i < directions.Length; i++)
                {
                    if (directions[i] == D8.Escape)
                    {
                        directions[i] = D8.Sink;
                    }
                }
            }
            state.Network = TileFlowNetwork.Build(state.Tile, directions, geometry);
            return new RouteOutcome(true, terminal, escaped, sinuositySum, landCells, meanderTicks);
        }
    }

    /// <summary>The tile's headings into <c>workspace.Headings</c>; returns the target sinuosity summed over
    /// its land cells, and their count.</summary>
    private LandSinuosity ComputeHeadings(TileCoord tile, ReadOnlySpan<float> window, int side, int tileStart,
                                          Workspace workspace)
    {
        MeanderField field = meander!;
        int t = geometry.Size;
        int padding = field.PaddingFor(t);
        int cropSide = t + 2 * padding;
        Span<double> crop = workspace.CropFor(cropSide);
        int origin = tileStart - padding;
        for (int row = 0; row < cropSide; row++)
        {
            ReadOnlySpan<float> source = window.Slice((origin + row) * side + origin, cropSide);
            Span<double> target = crop.Slice(row * cropSide, cropSide);
            for (int column = 0; column < cropSide; column++)
            {
                target[column] = source[column];
            }
        }
        field.ComputeHeadings(crop, padding, t, (long)tile.Row * t, (long)tile.Column * t, workspace.Headings,
                              workspace.Meander);

        double sum = 0.0;
        int land = 0;
        for (int row = 0; row < t; row++)
        {
            for (int column = 0; column < t; column++)
            {
                if (crop[(padding + row) * cropSide + padding + column] > 0.0)
                {
                    sum += workspace.Headings.Sinuosity[row * t + column];
                    land++;
                }
            }
        }
        return new LandSinuosity(sum, land);
    }

    /// <summary>Target sinuosity summed over a tile's land cells, and their count.</summary>
    /// <param name="Sum">The sum.</param>
    /// <param name="Cells">Land cells.</param>
    private readonly record struct LandSinuosity(double Sum, int Cells);

    /// <summary>Scratch for routing one tile.</summary>
    private sealed class Workspace
    {
        /// <summary>Fill window, grown as needed.</summary>
        private float[] window = Array.Empty<float>();
        /// <summary>Ground height window, grown as needed.</summary>
        private float[] ground = Array.Empty<float>();
        /// <summary>Meander crop, grown as needed.</summary>
        private double[] crop = Array.Empty<double>();

        /// <summary>Scratch for tiles of <paramref name="tileSize"/>.</summary>
        public Workspace(int tileSize)
        {
            Headings = new MeanderHeadings(tileSize);
        }

        /// <summary>The direction solver, with its own buffers.</summary>
        public FlowDirectionSolver Solver { get; } = new();

        /// <summary>The tile's headings.</summary>
        public MeanderHeadings Headings { get; }

        /// <summary>Scratch of the meander field.</summary>
        public MeanderWorkspace Meander { get; } = new();

        /// <summary>Frees the buffers sized for windows of more than <paramref name="maxCells"/> cells.</summary>
        /// <param name="maxCells">Largest window worth keeping buffers for.</param>
        public void Trim(int maxCells)
        {
            if (window.Length > maxCells)
            {
                window = Array.Empty<float>();
                ground = Array.Empty<float>();
            }
            Solver.Trim(maxCells);
        }

        /// <summary>The fill window for a side of <paramref name="side"/> cells.</summary>
        public Span<float> WindowFor(int side)
        {
            if (window.Length < side * side)
            {
                window = new float[side * side];
            }
            return window.AsSpan(0, side * side);
        }

        /// <summary>The ground height window for a side of <paramref name="side"/> cells.</summary>
        public Span<float> GroundFor(int side)
        {
            if (ground.Length < side * side)
            {
                ground = new float[side * side];
            }
            return ground.AsSpan(0, side * side);
        }

        /// <summary>The meander crop for a side of <paramref name="side"/> cells.</summary>
        public Span<double> CropFor(int side)
        {
            if (crop.Length < side * side)
            {
                crop = new double[side * side];
            }
            return crop.AsSpan(0, side * side);
        }
    }
}
