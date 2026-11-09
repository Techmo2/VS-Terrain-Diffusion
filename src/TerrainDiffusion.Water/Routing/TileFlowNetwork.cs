using System;
using System.Collections.Generic;
using TerrainDiffusion.Water.Grid;
using TerrainDiffusion.Water.Numerics;

namespace TerrainDiffusion.Water.Routing;

/// <summary>
/// The cells of one tile whose water crosses into one neighbouring tile, and where it arrives there.
/// </summary>
/// <param name="Target">The neighbouring tile.</param>
/// <param name="ExitIndices">Indices into <see cref="TileFlowNetwork.ExitCells"/> of the exits that lead
/// there, ascending.</param>
/// <param name="TargetCells">For each of those exits, the cell of <paramref name="Target"/> it steps into.</param>
internal sealed record ExitLink(TileCoord Target, int[] ExitIndices, int[] TargetCells);

/// <summary>
/// One tile's part of the river graph: each cell's next cell, a downstream order of the tile's cells, and the
/// links across its edges, plus the discharge state the tile last passed on to its neighbours.
/// </summary>
/// <remarks>
/// Built once, when the tile gets its directions; the directions never change after. The discharge state
/// (<see cref="Unsettled"/>, <see cref="ExitDischarge"/>, <see cref="ExitUnsettled"/>) is updated by every
/// flush of the tile (<see cref="DischargeSolver"/>).
/// </remarks>
internal sealed class TileFlowNetwork
{
    /// <summary>A network from its parts; the discharge state starts unsettled with nothing passed on.</summary>
    private TileFlowNetwork(sbyte[] directions, int[] next, int[] order, int[] perimeterRoots, int[] exitCells,
                            ExitLink[] links, int cellCount)
    {
        Directions = directions;
        Next = next;
        DownstreamOrder = order;
        PerimeterRoots = perimeterRoots;
        ExitCells = exitCells;
        Links = links;
        ExitDischarge = new double[exitCells.Length];
        ExitUnsettled = new bool[exitCells.Length];
        Array.Fill(ExitUnsettled, true);
        Unsettled = new bool[cellCount];
        Array.Fill(Unsettled, true);
    }

    /// <summary>Direction code of each cell (<see cref="D8"/>), row-major.</summary>
    public sbyte[] Directions { get; }

    /// <summary>The cell each cell's water steps to inside the tile; −1 where it leaves the tile or ends.</summary>
    public int[] Next { get; }

    /// <summary>Every cell of the tile, each before the cell it steps to (a topological order of the tile's
    /// part of the graph).</summary>
    public int[] DownstreamOrder { get; }

    /// <summary>For each cell of <see cref="TileGeometry.Perimeter"/>, the last cell inside the tile on its
    /// path (where it leaves, or ends).</summary>
    public int[] PerimeterRoots { get; }

    /// <summary>The border cells whose step leaves the tile, in perimeter order.</summary>
    public int[] ExitCells { get; }

    /// <summary>The exits grouped by the neighbour they lead into.</summary>
    public ExitLink[] Links { get; }

    /// <summary>Per cell: whether its discharge may still grow (water may still come from tiles that are not
    /// routed yet). All true until the first flush.</summary>
    public bool[] Unsettled { get; set; }

    /// <summary>Discharge leaving through each exit at the last flush.</summary>
    public double[] ExitDischarge { get; set; }

    /// <summary>Whether the discharge leaving through each exit was unsettled at the last flush.</summary>
    public bool[] ExitUnsettled { get; set; }

    /// <summary>The link from this tile into <paramref name="target"/>, if any of its water goes there.</summary>
    /// <param name="target">A neighbouring tile.</param>
    /// <returns>The link, or null.</returns>
    public ExitLink? LinkTo(TileCoord target)
    {
        foreach (ExitLink link in Links)
        {
            if (link.Target == target)
            {
                return link;
            }
        }
        return null;
    }

    /// <summary>The network of tile <paramref name="tile"/> from its direction codes.</summary>
    /// <param name="tile">The tile.</param>
    /// <param name="directions">Its direction codes (kept, not copied).</param>
    /// <param name="geometry">Tile tables.</param>
    /// <returns>The network.</returns>
    public static TileFlowNetwork Build(TileCoord tile, sbyte[] directions, TileGeometry geometry)
    {
        int size = geometry.Size, n = geometry.CellCount;
        ReadOnlySpan<int> rowStep = D8.RowStep, columnStep = D8.ColumnStep;

        // Next cell inside the tile, and how many cells step into each
        var next = new int[n];
        var inflowCount = new int[n];
        for (int i = 0; i < n; i++)
        {
            next[i] = -1;
            int d = directions[i];
            if (!D8.IsStep(d))
            {
                continue;
            }
            int row = i / size + rowStep[d], column = i % size + columnStep[d];
            if ((uint)row < (uint)size && (uint)column < (uint)size)
            {
                int j = row * size + column;
                next[i] = j;
                inflowCount[j]++;
            }
        }

        // Downstream order: sources first (in index order), then each cell once all its inflows are placed
        var order = new int[n];
        int tail = 0;
        for (int i = 0; i < n; i++)
        {
            if (inflowCount[i] == 0)
            {
                order[tail++] = i;
            }
        }
        for (int head = 0; head < tail; head++)
        {
            int j = next[order[head]];
            if (j >= 0 && --inflowCount[j] == 0)
            {
                order[tail++] = j;
            }
        }
        if (tail != n)
        {
            throw new InvalidOperationException($"Tile {tile} has a cycle in its directions.");
        }

        // The last cell inside the tile on each path, walking the order backwards
        var root = new int[n];
        for (int i = 0; i < n; i++)
        {
            root[i] = i;
        }
        for (int k = n - 1; k >= 0; k--)
        {
            int i = order[k];
            if (next[i] >= 0)
            {
                root[i] = root[next[i]];
            }
        }
        int[] perimeter = geometry.Perimeter;
        var perimeterRoots = new int[perimeter.Length];
        for (int p = 0; p < perimeter.Length; p++)
        {
            perimeterRoots[p] = root[perimeter[p]];
        }

        // Border cells whose step leaves the tile, grouped by the neighbour they enter
        var exitCells = new List<int>();
        var byTarget = new Dictionary<TileCoord, (List<int> Exits, List<int> Cells)>();
        var targets = new List<TileCoord>();
        foreach (int cell in perimeter)
        {
            int d = directions[cell];
            if (!D8.IsStep(d))
            {
                continue;
            }
            int row = cell / size + rowStep[d], column = cell % size + columnStep[d];
            if ((uint)row < (uint)size && (uint)column < (uint)size)
            {
                continue;
            }
            var target = new TileCoord(tile.Row + (int)PythonMath.FloorDivide(row, size),
                                       tile.Column + (int)PythonMath.FloorDivide(column, size));
            int targetCell = (int)(PythonMath.Mod(row, size) * size + PythonMath.Mod(column, size));
            if (!byTarget.TryGetValue(target, out var group))
            {
                group = (new List<int>(), new List<int>());
                byTarget[target] = group;
                targets.Add(target);
            }
            group.Exits.Add(exitCells.Count);
            group.Cells.Add(targetCell);
            exitCells.Add(cell);
        }
        var links = new ExitLink[targets.Count];
        for (int i = 0; i < targets.Count; i++)
        {
            var group = byTarget[targets[i]];
            links[i] = new ExitLink(targets[i], group.Exits.ToArray(), group.Cells.ToArray());
        }
        return new TileFlowNetwork(directions, next, order, perimeterRoots, exitCells.ToArray(), links, n);
    }

    /// <summary>Approximate memory held, in bytes.</summary>
    public long ByteSize =>
        Directions.Length + 4L * Next.Length + 4L * DownstreamOrder.Length + Unsettled.Length
        + 4L * PerimeterRoots.Length + 13L * ExitCells.Length;
}
