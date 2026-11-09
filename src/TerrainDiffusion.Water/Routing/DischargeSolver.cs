using System;

namespace TerrainDiffusion.Water.Routing;

/// <summary>
/// Discharge of one tile's cells: every cell counts itself plus everything that flows into it, with the inflow
/// across the tile's edges added.
/// </summary>
internal static class DischargeSolver
{
    /// <summary>
    /// One downstream sweep of a tile.
    /// </summary>
    /// <param name="network">The tile's network.</param>
    /// <param name="inflow">Per cell, the discharge arriving from neighbouring tiles.</param>
    /// <param name="unsettledInflow">Per cell, whether discharge may still arrive there from a neighbour (a
    /// tile not routed yet, or an exit of a neighbour that is itself unsettled).</param>
    /// <param name="discharge">Receives each cell's discharge (upstream cells, the cell included).</param>
    /// <param name="unsettled">Receives whether each cell's discharge may still grow.</param>
    /// <param name="final">Per cell, whether its discharge is final (empty: none is). A final cell passes on
    /// <paramref name="drawn"/>, settled, whatever reaches it now: after a neighbouring tile is forgotten its
    /// inflow is missing, and that must not reach the cells downstream. Its own entries in
    /// <paramref name="discharge"/> and <paramref name="unsettled"/> still hold what this sweep found.</param>
    /// <param name="drawn">Per cell, its final discharge (read where <paramref name="final"/>).</param>
    public static void Sweep(TileFlowNetwork network, ReadOnlySpan<double> inflow, ReadOnlySpan<bool> unsettledInflow,
                             Span<double> discharge, Span<bool> unsettled, ReadOnlySpan<bool> final = default,
                             ReadOnlySpan<float> drawn = default)
    {
        int n = network.Next.Length;
        bool pinned = !final.IsEmpty;
        if (inflow.Length != n || unsettledInflow.Length != n || discharge.Length != n || unsettled.Length != n
            || (pinned && (final.Length != n || drawn.Length != n)))
        {
            throw new ArgumentException("Every array must hold one value per cell of the tile.");
        }
        for (int i = 0; i < n; i++)
        {
            discharge[i] = 1.0 + inflow[i];
        }
        unsettledInflow.CopyTo(unsettled);
        int[] next = network.Next;
        foreach (int i in network.DownstreamOrder)
        {
            int j = next[i];
            if (j < 0)
            {
                continue;
            }
            if (pinned && final[i])
            {
                discharge[j] += drawn[i];
                continue;
            }
            discharge[j] += discharge[i];
            if (unsettled[i])
            {
                unsettled[j] = true;
            }
        }
    }
}
