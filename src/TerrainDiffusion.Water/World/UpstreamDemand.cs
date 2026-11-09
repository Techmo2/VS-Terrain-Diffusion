using System;
using System.Collections.Generic;
using TerrainDiffusion.Water.Grid;
using TerrainDiffusion.Water.Numerics;
using TerrainDiffusion.Water.Routing;

namespace TerrainDiffusion.Water.World;

/// <summary>
/// The tiles that must be generated before the water near a point can become final: the tiles near it, the
/// tiles a tile needs for its fill (more rings where a lake runs into the window edge) and directions, and,
/// upstream through edge cells, every tile water may still come in from until the known discharge reaches the
/// cap.
/// </summary>
/// <remarks>Read only; not thread safe with respect to tile arrivals.</remarks>
internal sealed class UpstreamDemand
{
    /// <summary>The tiles.</summary>
    private readonly TileStore store;
    /// <summary>Tile tables.</summary>
    private readonly TileGeometry geometry;
    /// <summary>Discharge cap.</summary>
    private readonly double cap;

    /// <summary>Demand over <paramref name="store"/>.</summary>
    /// <param name="store">The tiles.</param>
    /// <param name="geometry">Tile tables.</param>
    /// <param name="cap">Discharge cap.</param>
    public UpstreamDemand(TileStore store, TileGeometry geometry, double cap)
    {
        this.store = store;
        this.geometry = geometry;
        this.cap = cap;
    }

    /// <summary>
    /// The ungenerated tiles that the not yet final cells within <paramref name="radius"/> tiles of
    /// (<paramref name="focusRow"/>, <paramref name="focusColumn"/>) are waiting on.
    /// </summary>
    /// <param name="focusRow">Focus row, in tiles (fractional).</param>
    /// <param name="focusColumn">Focus column, in tiles (fractional).</param>
    /// <param name="radius">Radius in tiles; a tile counts if any part of it is within.</param>
    /// <returns>The tiles, unordered.</returns>
    public HashSet<TileCoord> Find(double focusRow, double focusColumn, double radius)
    {
        var wanted = new HashSet<TileCoord>();
        var stack = new Stack<(TileCoord Tile, int Cell)>();
        var seen = new HashSet<(TileCoord, int)>();
        int[] perimeter = geometry.Perimeter;

        int reach = (int)Math.Ceiling(radius) + 1;
        int rowCentre = (int)Math.Floor(focusRow), columnCentre = (int)Math.Floor(focusColumn);
        for (int row = rowCentre - reach; row <= rowCentre + reach; row++)
        {
            for (int column = columnCentre - reach; column <= columnCentre + reach; column++)
            {
                if (double.Hypot(row + 0.5 - focusRow, column + 0.5 - focusColumn) - 0.5 > radius)
                {
                    continue;
                }
                var tile = new TileCoord(row, column);
                TileState? state = store.Get(tile);
                if (state is null)
                {
                    wanted.Add(tile);
                    continue;
                }
                if (state.Network is not { } network)
                {
                    AddMissingAround(state, wanted);
                    continue;
                }
                for (int p = 0; p < perimeter.Length; p++)
                {
                    int cell = perimeter[p];
                    if (network.Unsettled[cell] && state.Discharge[cell] < cap)
                    {
                        stack.Push((tile, cell));
                    }
                }
            }
        }

        // Upstream through the edges: which neighbouring cells step into each unsettled edge cell
        int t = geometry.Size;
        while (stack.Count > 0)
        {
            (TileCoord tile, int cell) = stack.Pop();
            if (!seen.Add((tile, cell)))
            {
                continue;
            }
            long worldRow = (long)tile.Row * t + cell / t, worldColumn = (long)tile.Column * t + cell % t;
            for (int d = 0; d < D8.NeighbourCount; d++)
            {
                long ny = worldRow + D8.RowStep[d], nx = worldColumn + D8.ColumnStep[d];
                var source = new TileCoord((int)PythonMath.FloorDivide(ny, t), (int)PythonMath.FloorDivide(nx, t));
                if (source == tile)
                {
                    continue;
                }
                TileState? state = store.Get(source);
                if (state?.Network is not { } network)
                {
                    if (state is null)
                    {
                        AddMissingAround(source, isWide: false, wanted);
                    }
                    else
                    {
                        AddMissingAround(state, wanted);
                    }
                    continue;
                }
                int sourceCell = (int)(PythonMath.Mod(ny, t) * t + PythonMath.Mod(nx, t));
                int step = network.Directions[sourceCell];
                if (!D8.IsStep(step) || ny + D8.RowStep[step] != worldRow || nx + D8.ColumnStep[step] != worldColumn)
                {
                    continue;                                   // does not flow into this cell
                }
                if (!network.Unsettled[sourceCell] || state.Discharge[sourceCell] >= cap)
                {
                    continue;
                }
                for (int p = 0; p < perimeter.Length; p++)
                {
                    int edge = perimeter[p];
                    if (network.PerimeterRoots[p] == sourceCell && network.Unsettled[edge] && state.Discharge[edge] < cap)
                    {
                        stack.Push((source, edge));
                    }
                }
            }
        }
        return wanted;
    }

    /// <summary>The ungenerated tiles a tile without directions needs (see the other overload).</summary>
    private void AddMissingAround(TileState state, HashSet<TileCoord> wanted) =>
        AddMissingAround(state.Tile, state.IsWide, wanted);

    /// <summary>The ungenerated tiles that the directions of <paramref name="tile"/> wait on: the fill of the
    /// 3 × 3 tiles around it (5 × 5 if <paramref name="isWide"/>), each of which needs the tiles within its fill
    /// radius (1 for a tile not generated yet, so 5 × 5 or 7 × 7 tiles in all unless a fill had to grow).</summary>
    private void AddMissingAround(TileCoord tile, bool isWide, HashSet<TileCoord> wanted)
    {
        int routeRadius = isWide ? 2 : 1;
        for (int dr = -routeRadius; dr <= routeRadius; dr++)
        {
            for (int dc = -routeRadius; dc <= routeRadius; dc++)
            {
                TileCoord neighbour = tile.Offset(dr, dc);
                TileState? state = store.Get(neighbour);
                if (state is { IsFilled: true })
                {
                    continue;
                }
                int fillRadius = state?.FillRadius ?? 1;
                for (int fr = -fillRadius; fr <= fillRadius; fr++)
                {
                    for (int fc = -fillRadius; fc <= fillRadius; fc++)
                    {
                        TileCoord around = neighbour.Offset(fr, fc);
                        if (!store.Contains(around))
                        {
                            wanted.Add(around);
                        }
                    }
                }
            }
        }
    }
}
