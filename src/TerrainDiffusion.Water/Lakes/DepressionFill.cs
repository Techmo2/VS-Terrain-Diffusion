using System;
using TerrainDiffusion.Water.Grid;
using TerrainDiffusion.Water.Numerics;

namespace TerrainDiffusion.Water.Lakes;

/// <summary>
/// The water level of every cell of a window of terrain: lakes fill their depressions up to the spill, except
/// closed basins large enough to keep a terminal lake (endorheic basins).
/// </summary>
/// <remarks>
/// <para>The water level of a cell is the lowest level <c>w ≥ z</c> at which the connected low ground
/// <c>{z ≤ w}</c> around it is <em>open</em>: it holds sea (z ≤ 0), or at least <c>lakeCells</c> cells, or it
/// reaches the edge of the window and is assumed to drain beyond it. So a depression smaller than
/// <c>lakeCells</c> fills to its spill like any fill; a bigger closed basin keeps a lake of about
/// <c>lakeCells</c> cells instead.</para>
/// <para>One pass over the cells in height order (ties by index), joining each to its already-visited
/// neighbours with union-find. A closed component keeps a linked list of its cells; the moment it opens, every
/// cell on the list gets the current height as its level. Cells decided only by reaching the window edge are
/// counted as <em>uncertain</em>: a bigger window could see that they are part of a closed basin. Every other
/// level is the one a fill of the whole world would give, whatever the window: a component that has not reached
/// the window edge is the same component in the whole world.</para>
/// <para>Cost is O(n α(n)) after an O(n) radix sort. An instance reuses its buffers between calls and is not
/// thread safe: use one per thread.</para>
/// </remarks>
internal sealed class DepressionFill
{
    /// <summary>What is known about a component of visited cells.</summary>
    private enum BasinState : byte
    {
        /// <summary>No outlet yet: its cells wait for a level.</summary>
        Closed = 0,

        /// <summary>Holds sea or enough cells: water spills from it.</summary>
        Open = 1,

        /// <summary>Open only because it reaches the window edge.</summary>
        OpenAtEdge = 2,
    }

    /// <summary>Per component root: its size, its list of cells waiting for a level, and its state. Kept
    /// together so a join touches one cache line per root.</summary>
    private struct Basin
    {
        /// <summary>Cells in the component.</summary>
        public int Size;
        /// <summary>First cell on its waiting list (cells with no level yet, while closed).</summary>
        public int ListHead;
        /// <summary>Last cell on its waiting list.</summary>
        public int ListTail;
        /// <summary>Whether it is closed, open, or open only through the window edge.</summary>
        public BasinState State;
    }

    /// <summary>The window's cells as sort keys (height, then index); in height order after sorting.</summary>
    private ulong[] keys = Array.Empty<ulong>();
    /// <summary>Scratch for the radix sort.</summary>
    private ulong[] sortBuffer = Array.Empty<ulong>();
    /// <summary>Union-find forest: each visited cell's parent; −1 for a cell not visited yet.</summary>
    private int[] parent = Array.Empty<int>();
    /// <summary>Per component root, its <see cref="Basin"/>.</summary>
    private Basin[] basins = Array.Empty<Basin>();
    /// <summary>Next cell on its component's waiting list, −1 at the end.</summary>
    private int[] listNext = Array.Empty<int>();
    /// <summary>Water level per cell, once its component has opened.</summary>
    private float[] level = Array.Empty<float>();
    /// <summary>Whether a cell's level was decided only by the window edge.</summary>
    private bool[] uncertain = Array.Empty<bool>();

    /// <summary>
    /// Fills a square window and writes the levels of its central square.
    /// </summary>
    /// <param name="elevation">Window heights in metres, row-major, <c>side²</c> finite values. Cells at or
    /// below 0 are sea.</param>
    /// <param name="side">Window side in cells.</param>
    /// <param name="lakeCells">Closed basins of at least this many cells stay terminal lakes.</param>
    /// <param name="coreStart">Row and column of the central square's first cell.</param>
    /// <param name="coreSize">Side of the central square.</param>
    /// <param name="coreLevel">Receives the central square's levels, row-major, <c>coreSize²</c> values.</param>
    /// <returns>Cells of the central square whose level was decided only by the window edge.</returns>
    public int Fill(ReadOnlySpan<float> elevation, int side, int lakeCells, int coreStart, int coreSize,
                    Span<float> coreLevel)
    {
        int n = side * side;
        if (side < 1 || elevation.Length != n || coreLevel.Length != coreSize * coreSize
            || coreStart < 0 || coreStart + coreSize > side)
        {
            throw new ArgumentException("The window must be side² values and the core must lie inside it.");
        }
        EnsureCapacity(n);
        SortByHeight(elevation, n);
        Array.Fill(parent, -1, 0, n);

        ReadOnlySpan<int> rowStep = D8.RowStep, columnStep = D8.ColumnStep;
        Span<int> offset = stackalloc int[D8.NeighbourCount];
        for (int d = 0; d < D8.NeighbourCount; d++)
        {
            offset[d] = rowStep[d] * side + columnStep[d];
        }
        ulong indexMask = (1UL << RadixSort.IndexBits(n)) - 1;
        for (int k = 0; k < n; k++)
        {
            int cell = (int)(keys[k] & indexMask);
            float height = elevation[cell];
            int row = cell / side, column = cell - row * side;
            bool onEdge = row == 0 || column == 0 || row == side - 1 || column == side - 1;
            Visit(cell, height, onEdge, lakeCells);

            int root = cell;
            for (int d = 0; d < D8.NeighbourCount; d++)
            {
                int neighbour;
                if (!onEdge)
                {
                    neighbour = cell + offset[d];
                }
                else
                {
                    int r = row + rowStep[d], c = column + columnStep[d];
                    if ((uint)r >= (uint)side || (uint)c >= (uint)side)
                    {
                        continue;
                    }
                    neighbour = r * side + c;
                }
                if (parent[neighbour] >= 0)
                {
                    root = Join(root, Find(neighbour), height, lakeCells);
                }
            }
        }

        // A component never opened (only possible without any window edge): the top of the window
        float top = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            top = Math.Max(top, elevation[i]);
        }
        int uncertainCore = 0;
        for (int row = 0; row < coreSize; row++)
        {
            for (int column = 0; column < coreSize; column++)
            {
                int cell = (coreStart + row) * side + coreStart + column;
                bool open = basins[Find(cell)].State != BasinState.Closed;
                coreLevel[row * coreSize + column] = open ? level[cell] : top;
                if (open && uncertain[cell])
                {
                    uncertainCore++;
                }
            }
        }
        return uncertainCore;
    }

    /// <summary>Adds a cell as a component of its own: open (with its own height as level) if it is sea or on
    /// the window edge, else closed and waiting.</summary>
    private void Visit(int cell, float height, bool onEdge, int lakeCells)
    {
        parent[cell] = cell;
        listNext[cell] = -1;
        uncertain[cell] = false;
        BasinState state = BasinState.Closed;
        if (height <= 0.0f || lakeCells <= 1)
        {
            state = BasinState.Open;
            level[cell] = height;
        }
        else if (onEdge)
        {
            state = BasinState.OpenAtEdge;
            level[cell] = height;
            uncertain[cell] = true;
        }
        basins[cell] = new Basin { Size = 1, ListHead = cell, ListTail = cell, State = state };
    }

    /// <summary>Merges two components (given by their roots) while flooding to <paramref name="height"/>, and
    /// returns the root of the merged one.</summary>
    private int Join(int a, int b, float height, int lakeCells)
    {
        if (a == b)
        {
            return a;
        }
        if (basins[a].Size < basins[b].Size)
        {
            (a, b) = (b, a);
        }
        ref Basin big = ref basins[a];
        ref Basin small = ref basins[b];
        BasinState stateA = big.State, stateB = small.State;
        long total = (long)big.Size + small.Size;
        parent[b] = a;
        big.Size = (int)total;
        bool bigEnough = total >= lakeCells;

        if (stateA == BasinState.Closed && stateB == BasinState.Closed)
        {
            if (bigEnough)
            {
                AssignLevel(big.ListHead, height, false);
                AssignLevel(small.ListHead, height, false);
                big.State = BasinState.Open;
            }
            else
            {
                listNext[big.ListTail] = small.ListHead;      // still closed: one list
                big.ListTail = small.ListTail;
            }
        }
        else if (stateA == BasinState.Closed || stateB == BasinState.Closed)
        {
            BasinState open = stateA != BasinState.Closed ? stateA : stateB;
            int closedHead = stateA == BasinState.Closed ? big.ListHead : small.ListHead;
            AssignLevel(closedHead, height, open == BasinState.OpenAtEdge && !bigEnough);
            big.State = open == BasinState.Open || bigEnough ? BasinState.Open : BasinState.OpenAtEdge;
        }
        else
        {
            big.State = stateA == BasinState.Open || stateB == BasinState.Open || bigEnough
                ? BasinState.Open : BasinState.OpenAtEdge;
        }
        return a;
    }

    /// <summary>Gives every cell on the list from <paramref name="first"/> the level
    /// <paramref name="height"/>.</summary>
    private void AssignLevel(int first, float height, bool decidedByEdge)
    {
        for (int cell = first; cell >= 0; cell = listNext[cell])
        {
            level[cell] = height;
            uncertain[cell] = decidedByEdge;
        }
    }

    /// <summary>The root of a visited cell's component (with path halving).</summary>
    private int Find(int cell)
    {
        int[] p = parent;
        while (p[cell] != cell)
        {
            p[cell] = p[p[cell]];
            cell = p[cell];
        }
        return cell;
    }

    /// <summary>Fills <see cref="keys"/> with the cells in height order (ties by index).</summary>
    private void SortByHeight(ReadOnlySpan<float> elevation, int n)
    {
        int indexBits = RadixSort.IndexBits(n);
        for (int i = 0; i < n; i++)
        {
            keys[i] = ((ulong)RadixSort.OrderedKey(elevation[i]) << indexBits) | (uint)i;
        }
        RadixSort.Sort(keys.AsSpan(0, n), sortBuffer.AsSpan(0, n), 32 + indexBits);
    }

    /// <summary>Frees the buffers if they hold more than <paramref name="maxCells"/> cells (they grow again on
    /// the next call that needs them).</summary>
    /// <param name="maxCells">Largest window, in cells, worth keeping buffers for.</param>
    public void Trim(int maxCells)
    {
        if (parent.Length <= maxCells)
        {
            return;
        }
        keys = sortBuffer = Array.Empty<ulong>();
        parent = listNext = Array.Empty<int>();
        basins = Array.Empty<Basin>();
        level = Array.Empty<float>();
        uncertain = Array.Empty<bool>();
    }

    /// <summary>Grows the buffers for windows of <paramref name="n"/> cells.</summary>
    private void EnsureCapacity(int n)
    {
        if (parent.Length >= n)
        {
            return;
        }
        keys = new ulong[n];
        sortBuffer = new ulong[n];
        parent = new int[n];
        basins = new Basin[n];
        listNext = new int[n];
        level = new float[n];
        uncertain = new bool[n];
    }
}
