using System;
using System.Collections.Generic;
using TerrainDiffusion.Water.Grid;
using TerrainDiffusion.Water.Routing;

namespace TerrainDiffusion.Water.World;

/// <summary>
/// Stage 3: discharge. Each routed tile sweeps its own cells with the inflow across its edges added; what leaves
/// through its exits is the inflow of the neighbour it enters, so a change travels downstream tile by tile until
/// nothing changes. Also releases the chunks whose cells are all final.
/// </summary>
/// <remarks>
/// <para>A cell's discharge counts every cell upstream of it, wherever they are, capped at the discharge cap.
/// It is final once nothing upstream can still change (no unsettled cell upstream borders a tile without
/// directions) or once the known part reaches the cap: the drawn value cannot change then, whatever arrives
/// later. A final cell is never written again, and passes its drawn value on as settled, so a forgotten tile's
/// missing inflow never reaches the cells downstream. If a sweep with everything upstream known would change
/// a final cell, that is counted as a violation (never expected).</para>
/// <para>Not thread safe: one sweep at a time.</para>
/// </remarks>
internal sealed class DischargeStage
{
    /// <summary>The tiles.</summary>
    private readonly TileStore store;
    /// <summary>Tile tables.</summary>
    private readonly TileGeometry geometry;
    /// <summary>Discharge cap.</summary>
    private readonly double cap;
    /// <summary>Scratch: discharge arriving at each cell from the neighbours.</summary>
    private readonly double[] inflow;
    /// <summary>Scratch: whether more may still arrive at each cell.</summary>
    private readonly bool[] unsettledInflow;
    /// <summary>Scratch: each cell's discharge in the current sweep.</summary>
    private readonly double[] discharge;

    /// <summary>A discharge stage over <paramref name="store"/>.</summary>
    /// <param name="store">The tiles.</param>
    /// <param name="geometry">Tile tables.</param>
    /// <param name="cap">Discharge cap.</param>
    public DischargeStage(TileStore store, TileGeometry geometry, double cap)
    {
        this.store = store;
        this.geometry = geometry;
        this.cap = cap;
        inflow = new double[geometry.CellCount];
        unsettledInflow = new bool[geometry.CellCount];
        discharge = new double[geometry.CellCount];
    }

    /// <summary>Sweeps made so far.</summary>
    public long Sweeps { get; private set; }

    /// <summary>Sweeps that would have changed a final cell.</summary>
    public long Violations { get; private set; }

    /// <summary>
    /// Sweeps tiles until nothing changes: each tile of <paramref name="start"/>, and every routed tile
    /// downstream of a tile whose outflow changed.
    /// </summary>
    /// <param name="start">Tiles to sweep first.</param>
    /// <param name="step">Release step for chunks that become final.</param>
    /// <param name="changed">Receives the tiles whose water changed.</param>
    /// <param name="released">Receives the chunks released.</param>
    /// <param name="throwIfCancelled">Called between sweeps; throws to abandon the work.</param>
    public void Propagate(IEnumerable<TileCoord> start, int step, ISet<TileCoord> changed, List<ReleasedChunk> released,
                          Action throwIfCancelled)
    {
        var queue = new Stack<TileCoord>();
        var queued = new HashSet<TileCoord>();
        int routedTiles = 0;
        foreach (TileState state in store.All)
        {
            routedTiles += state.IsRouted ? 1 : 0;
        }
        foreach (TileCoord tile in start)
        {
            if (queued.Add(tile))
            {
                queue.Push(tile);
            }
        }
        long guard = 50L * (routedTiles + 1);       // a fixed point is always reached long before this
        while (queue.Count > 0 && guard-- > 0)
        {
            throwIfCancelled();
            TileCoord tile = queue.Pop();
            queued.Remove(tile);
            TileState state = store.Get(tile)!;
            if (!Sweep(state, step, changed, released))
            {
                continue;
            }
            foreach (ExitLink link in state.Network!.Links)
            {
                TileState? target = store.Get(link.Target);
                if (target is not null && target.IsRouted && queued.Add(link.Target))
                {
                    queue.Push(link.Target);
                }
            }
        }
    }

    /// <summary>One sweep of a routed tile. Returns whether what leaves it changed.</summary>
    private bool Sweep(TileState state, int step, ISet<TileCoord> changed, List<ReleasedChunk> released)
    {
        TileFlowNetwork network = state.Network!;
        GatherInflow(state.Tile);

        var unsettled = new bool[geometry.CellCount];
        float[] drawn = state.Discharge;
        bool[] final = state.IsFinal;
        DischargeSolver.Sweep(network, inflow, unsettledInflow, discharge, unsettled, final, drawn);

        bool violated = false, anyChange = false;
        for (int i = 0; i < discharge.Length; i++)
        {
            double q = Math.Min(discharge[i], cap);
            discharge[i] = q;
            float value = (float)q;
            bool nowFinal = !unsettled[i] || q >= cap;
            if (final[i])
            {
                // A final cell keeps its value, and passes it on settled (as the solver did inside the tile).
                // Only a change with everything upstream known is a violation: after a neighbouring tile is
                // forgotten, its inflow is simply missing.
                violated |= !unsettled[i] && Math.Abs(drawn[i] - value) > 0.5f;
                discharge[i] = drawn[i];
                unsettled[i] = false;
                continue;
            }
            if (drawn[i] != value || nowFinal)
            {
                anyChange = true;
            }
            drawn[i] = value;
            final[i] = nowFinal;
        }
        if (violated)
        {
            Violations++;
        }
        if (anyChange)
        {
            changed.Add(state.Tile);
        }
        network.Unsettled = unsettled;

        bool moved = false;
        int[] exits = network.ExitCells;
        for (int e = 0; e < exits.Length; e++)
        {
            double q = discharge[exits[e]];
            bool u = unsettled[exits[e]];
            if (q != network.ExitDischarge[e] || u != network.ExitUnsettled[e])
            {
                moved = true;
                network.ExitDischarge[e] = q;
                network.ExitUnsettled[e] = u;
            }
        }
        Release(state, step, released);
        Sweeps++;
        return moved;
    }

    /// <summary>Fills <see cref="inflow"/> and <see cref="unsettledInflow"/> for <paramref name="tile"/> from
    /// its neighbours: the discharge of their exits into it, and its edge cells beside tiles not routed yet.</summary>
    private void GatherInflow(TileCoord tile)
    {
        Array.Clear(inflow);
        Array.Clear(unsettledInflow);
        for (int slot = 0; slot < D8.NeighbourCount; slot++)
        {
            TileState? neighbour = store.Get(tile.Offset(D8.RowStep[slot], D8.ColumnStep[slot]));
            if (neighbour?.Network is not { } network)
            {
                foreach (int cell in geometry.EdgeCellsFacing(slot))
                {
                    unsettledInflow[cell] = true;       // water may still come in from there
                }
                continue;
            }
            ExitLink? link = network.LinkTo(tile);
            if (link is null)
            {
                continue;
            }
            for (int k = 0; k < link.ExitIndices.Length; k++)
            {
                int exit = link.ExitIndices[k], cell = link.TargetCells[k];
                inflow[cell] += network.ExitDischarge[exit];
                if (network.ExitUnsettled[exit])
                {
                    unsettledInflow[cell] = true;
                }
            }
        }
    }

    /// <summary>
    /// Releases the chunks of <paramref name="state"/> whose cells are all final.
    /// </summary>
    /// <param name="state">The tile.</param>
    /// <param name="step">Release step.</param>
    /// <param name="released">Receives the chunks released.</param>
    public void Release(TileState state, int step, List<ReleasedChunk> released)
    {
        if (state.IsFullyReleased)
        {
            return;
        }
        int t = geometry.Size, perSide = geometry.ChunksPerSide, chunk = geometry.ChunkSize;
        bool all = true;
        for (int ci = 0; ci < perSide; ci++)
        {
            for (int cj = 0; cj < perSide; cj++)
            {
                int index = ci * perSide + cj;
                if (state.ChunkReleaseStep[index] >= 0)
                {
                    continue;
                }
                if (!ChunkIsFinal(state.IsFinal, t, ci * chunk, cj * chunk, chunk))
                {
                    all = false;
                    continue;
                }
                state.ChunkReleaseStep[index] = step;
                released.Add(new ReleasedChunk(
                    new ChunkCoord(state.Tile.Row * perSide + ci, state.Tile.Column * perSide + cj), step));
            }
        }
        state.IsFullyReleased = all;
    }

    /// <summary>Whether every cell of the chunk at (<paramref name="row0"/>, <paramref name="column0"/>) is final.</summary>
    private static bool ChunkIsFinal(bool[] final, int tileSize, int row0, int column0, int chunk)
    {
        for (int row = row0; row < row0 + chunk; row++)
        {
            if (Array.IndexOf(final, false, row * tileSize + column0, chunk) >= 0)
            {
                return false;
            }
        }
        return true;
    }
}
