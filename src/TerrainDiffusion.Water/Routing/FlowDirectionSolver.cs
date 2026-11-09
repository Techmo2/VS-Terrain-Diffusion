using System;
using System.Collections.Generic;
using TerrainDiffusion.Water.Grid;
using TerrainDiffusion.Water.Numerics;

namespace TerrainDiffusion.Water.Routing;

/// <summary>
/// The direction code of every cell of a tile: which neighbour its water steps to on the filled surface.
/// </summary>
/// <remarks>
/// <para>Every land cell steps to a strictly lower neighbour on the filled surface. Without a heading it takes
/// the lowest one (steepest descent). With a heading (<see cref="Meander.MeanderField"/>) it chooses between the
/// two neighbours either side of the heading, the farther one with the probability that keeps paths on the
/// heading on average (Rho8-like dither); if neither is lower it takes the lower neighbour closest to the
/// heading.</para>
/// <para>A flat (cells of one level with no lower neighbour: a lake surface, often only a few centimetres over
/// the ground) drains toward its cells that do have one, its outlets, over the least-cost distance across the
/// flat. A step costs its length times the mean of its two cells' costs, and a cell costs 1 over the flat's lowest
/// ground, rising to 1 + <c>groundWeight</c> where the ground reaches the flat's level, so a river crossing the
/// flat follows the low ground under the water, as a channel would, instead of a straight line to the outlet.
/// Each cell steps to a neighbour strictly nearer an outlet: where it may meander (a finite swing), it aims down
/// the distance (the mean of its descents) turned by its swing and dithered, as a cell on a slope aims at its
/// heading; elsewhere it takes the steepest descent per unit length. Every step is to an adjacent cell and
/// strictly downhill on (fill level, distance across the flat), so the graph has no cycles, both sides of a tile
/// seam agree where water crosses, and flow into a tile can only come from its 8 neighbours. A flat with no
/// outlet (a terminal lake) drains the same way toward its lowest ground cell (ties: the first in row-major
/// order), which is <see cref="D8.Sink"/>, as is the sea: so a river carries on across a lake's shallow margin
/// into the lake instead of ending at its shore. A flat is only routed when the window holds it whole, so every
/// tile computes the same distances. A flat that reaches the window edge or unknown cells is
/// <see cref="D8.Escape"/> (the caller retries with a bigger window).</para>
/// <para>An instance reuses its buffers between calls and is not thread safe: use one per thread.</para>
/// </remarks>
internal sealed class FlowDirectionSolver
{
    /// <summary>π/4: one octant.</summary>
    private const double QuarterPi = Math.PI / 4.0;
    /// <summary>2π: a full turn.</summary>
    private const double TwoPi = 2.0 * Math.PI;
    /// <summary>√2: a diagonal step's length.</summary>
    private static readonly double Sqrt2 = Math.Sqrt(2.0);
    /// <summary>√½: a diagonal step's unit vector components.</summary>
    private static readonly double Diagonal = Math.Sqrt(0.5);

    /// <summary>Flat id per window cell, −1 for none (reset after each call).</summary>
    private int[] component = Array.Empty<int>();
    /// <summary>Least-cost distance from each flat cell to its flat's outlets, −1 for unreached (reset after
    /// each call).</summary>
    private double[] distance = Array.Empty<double>();
    /// <summary>Cost of crossing each flat cell.</summary>
    private double[] cost = Array.Empty<double>();
    /// <summary>Every flat's cells, one flat after another.</summary>
    private int[] flatCells = Array.Empty<int>();
    /// <summary>Outlets of the flat being routed.</summary>
    private int[] outlets = Array.Empty<int>();
    /// <summary>Dijkstra's queue over one flat.</summary>
    private readonly PriorityQueue<int, double> frontier = new();

    /// <summary>
    /// Directions of the tile at (<paramref name="tileRow"/>, <paramref name="tileColumn"/>) of a fill window.
    /// </summary>
    /// <param name="level">Window of water levels, row-major <c>side²</c>; NaN where not known.</param>
    /// <param name="elevation">Window of ground heights, row-major <c>side²</c> (read only on flats, whose
    /// cells are all known).</param>
    /// <param name="side">Window side in cells.</param>
    /// <param name="tileRow">Window row of the tile's first cell.</param>
    /// <param name="tileColumn">Window column of the tile's first cell.</param>
    /// <param name="tileSize">Tile side in cells.</param>
    /// <param name="heading">Per tile cell, the heading to aim for (NaN for none); empty for none at all.</param>
    /// <param name="dither">Per tile cell, the dither in [0, 1) (used with a heading or swing).</param>
    /// <param name="swing">Per tile cell, the turn off downhill a flat cell may take (NaN for none); empty for
    /// none at all.</param>
    /// <param name="groundWeight">How much more a flat costs to cross at its own level than over its lowest
    /// ground (≥ 0).</param>
    /// <param name="directions">Receives the tile's direction codes, row-major <c>tileSize²</c>.</param>
    /// <returns>Number of terminal flats (lakes without an outlet) found.</returns>
    public int Solve(ReadOnlySpan<float> level, ReadOnlySpan<float> elevation, int side, int tileRow, int tileColumn,
                     int tileSize, ReadOnlySpan<double> heading, ReadOnlySpan<double> dither,
                     ReadOnlySpan<double> swing, double groundWeight, Span<sbyte> directions)
    {
        int tileCells = tileSize * tileSize;
        bool hasHeading = !heading.IsEmpty, hasSwing = !swing.IsEmpty;
        if (level.Length != side * side || elevation.Length != side * side || directions.Length != tileCells
            || (hasHeading && heading.Length != tileCells) || (hasSwing && swing.Length != tileCells)
            || ((hasHeading || hasSwing) && dither.Length != tileCells)
            || tileRow < 0 || tileColumn < 0 || tileRow + tileSize > side || tileColumn + tileSize > side)
        {
            throw new ArgumentException("Inconsistent window, tile, heading or swing sizes.");
        }
        EnsureCapacity(side * side);

        ChooseDownhillSteps(level, side, tileRow, tileColumn, tileSize, heading, dither, directions);
        return RouteFlats(level, elevation, side, tileRow, tileColumn, tileSize, dither, swing, groundWeight,
                          directions);
    }

    /// <summary>First pass: every cell with a strictly lower known neighbour gets its step; the sea gets
    /// <see cref="D8.Sink"/>; the rest (flats) stay <see cref="D8.Undecided"/>.</summary>
    private static void ChooseDownhillSteps(ReadOnlySpan<float> level, int side, int tileRow, int tileColumn,
                                            int tileSize, ReadOnlySpan<double> heading, ReadOnlySpan<double> dither,
                                            Span<sbyte> directions)
    {
        ReadOnlySpan<int> rowStep = D8.RowStep, columnStep = D8.ColumnStep;
        Span<bool> allowed = stackalloc bool[D8.NeighbourCount];
        for (int ty = 0; ty < tileSize; ty++)
        {
            for (int tx = 0; tx < tileSize; tx++)
            {
                int y = tileRow + ty, x = tileColumn + tx;
                int t = ty * tileSize + tx;
                float here = level[y * side + x];
                if (IsSea(here))
                {
                    directions[t] = D8.Sink;
                    continue;
                }
                float lowest = here;
                int best = D8.Undecided;
                for (int d = 0; d < D8.NeighbourCount; d++)
                {
                    allowed[d] = IsLowerKnown(level, side, y + rowStep[d], x + columnStep[d], here);
                    if (allowed[d] && level[(y + rowStep[d]) * side + x + columnStep[d]] < lowest)
                    {
                        lowest = level[(y + rowStep[d]) * side + x + columnStep[d]];
                        best = d;
                    }
                }
                if (best >= 0 && heading.Length > 0 && !double.IsNaN(heading[t]))
                {
                    best = Aim(heading[t], dither[t], allowed);
                }
                directions[t] = (sbyte)best;
            }
        }
    }

    /// <summary>
    /// The step toward <paramref name="heading"/> among the <paramref name="allowed"/> neighbours: one of the two
    /// either side of the heading, picked by the dither so paths keep the heading on average, if allowed; else
    /// the allowed neighbour closest to the heading; <see cref="D8.Undecided"/> if none is allowed.
    /// </summary>
    private static int Aim(double heading, double dither, ReadOnlySpan<bool> allowed)
    {
        double angle = PythonMath.Mod(heading, TwoPi);
        int octant = (int)PythonMath.FloorDivide(angle, QuarterPi) % 8;
        double withinOctant = angle - octant * QuarterPi;
        // Probability of the neighbour past the heading (counter-clockwise), from the tangent of the angle into
        // the octant: a straight line at that angle steps diagonally that often.
        double pastProbability = octant % 2 == 0 ? Math.Tan(withinOctant) : 1.0 - Math.Tan(QuarterPi - withinOctant);
        int before = D8.CodeByOctant[octant], past = D8.CodeByOctant[(octant + 1) % 8];
        (int first, int second) = dither < pastProbability ? (past, before) : (before, past);
        if (allowed[first])
        {
            return first;
        }
        if (allowed[second])
        {
            return second;
        }

        int pick = D8.Undecided;
        double smallestGap = 10.0;
        ReadOnlySpan<double> headings = D8.Heading;
        for (int d = 0; d < D8.NeighbourCount; d++)
        {
            if (allowed[d])
            {
                double gap = Math.Abs(PythonMath.Mod(headings[d] - angle + Math.PI, TwoPi) - Math.PI);
                if (gap < smallestGap)
                {
                    smallestGap = gap;
                    pick = d;
                }
            }
        }
        return pick;
    }

    /// <summary>
    /// Second pass: each flat of undecided tile cells, found breadth first through equal known non-sea levels
    /// (into the rest of the window too), drains toward its cells that have a lower neighbour; a flat with none is
    /// a terminal lake, which drains toward its lowest ground instead; a flat that reaches the window edge or
    /// unknown cells escapes.
    /// </summary>
    /// <returns>Number of terminal flats.</returns>
    private int RouteFlats(ReadOnlySpan<float> level, ReadOnlySpan<float> elevation, int side, int tileRow,
                          int tileColumn, int tileSize, ReadOnlySpan<double> dither, ReadOnlySpan<double> swing,
                          double groundWeight, Span<sbyte> directions)
    {
        ReadOnlySpan<int> rowStep = D8.RowStep, columnStep = D8.ColumnStep;
        int flatCount = 0, terminal = 0, used = 0;
        for (int ty = 0; ty < tileSize; ty++)
        {
            for (int tx = 0; tx < tileSize; tx++)
            {
                int t = ty * tileSize + tx;
                int seed = (tileRow + ty) * side + tileColumn + tx;
                if (directions[t] != D8.Undecided || component[seed] >= 0)
                {
                    continue;
                }

                // The flat: every cell reachable through the same level
                float flatLevel = level[seed];
                int id = flatCount++;
                int start = used;
                component[seed] = id;
                flatCells[used++] = seed;
                bool escapes = false;
                for (int next = start; next < used; next++)
                {
                    int cell = flatCells[next];
                    int cy = cell / side, cx = cell - cy * side;
                    if (cy == 0 || cx == 0 || cy == side - 1 || cx == side - 1)
                    {
                        escapes = true;
                    }
                    for (int d = 0; d < D8.NeighbourCount; d++)
                    {
                        int ny = cy + rowStep[d], nx = cx + columnStep[d];
                        if ((uint)ny >= (uint)side || (uint)nx >= (uint)side)
                        {
                            continue;
                        }
                        int m = ny * side + nx;
                        float v = level[m];
                        if (float.IsNaN(v))
                        {
                            escapes = true;
                            continue;
                        }
                        if (component[m] == -1 && !IsSea(v) && v == flatLevel)
                        {
                            component[m] = id;
                            flatCells[used++] = m;
                        }
                    }
                }
                int end = used;

                // Its outlet cells: those with a lower known neighbour
                int outletCount = 0;
                for (int k = start; k < end; k++)
                {
                    int cell = flatCells[k];
                    int cy = cell / side, cx = cell - cy * side;
                    for (int d = 0; d < D8.NeighbourCount; d++)
                    {
                        if (IsLowerKnown(level, side, cy + rowStep[d], cx + columnStep[d], flatLevel))
                        {
                            distance[cell] = 0.0;
                            outlets[outletCount++] = cell;
                            break;
                        }
                    }
                }

                if (escapes)
                {
                    for (int k = start; k < end; k++)
                    {
                        SetIfUndecided(flatCells[k], D8.Escape, side, tileRow, tileColumn, tileSize, directions);
                    }
                    continue;
                }
                if (outletCount == 0)
                {
                    // A terminal lake: everything drains to its lowest ground, the lake's one sink
                    terminal++;
                    int lowest = LowestCell(elevation, start, end);
                    distance[lowest] = 0.0;
                    outlets[outletCount++] = lowest;
                    SetIfUndecided(lowest, D8.Sink, side, tileRow, tileColumn, tileSize, directions);
                }

                SetCosts(elevation, flatLevel, start, end, groundWeight);
                ComputeDistances(side, id, outletCount);
                for (int k = start; k < end; k++)
                {
                    int cell = flatCells[k];
                    int cy = cell / side, cx = cell - cy * side;
                    int ty2 = cy - tileRow, tx2 = cx - tileColumn;
                    if ((uint)ty2 >= (uint)tileSize || (uint)tx2 >= (uint)tileSize
                        || directions[ty2 * tileSize + tx2] != D8.Undecided)
                    {
                        continue;
                    }
                    int t2 = ty2 * tileSize + tx2;
                    double turn = swing.IsEmpty ? double.NaN : swing[t2];
                    directions[t2] = (sbyte)StepAcrossFlat(side, id, cy, cx, turn, double.IsNaN(turn) ? 0.0 : dither[t2]);
                }
            }
        }

        // Leave the buffers clean for the next call (touching only what this one used)
        for (int k = 0; k < used; k++)
        {
            component[flatCells[k]] = -1;
            distance[flatCells[k]] = -1.0;
        }
        return terminal;
    }

    /// <summary>The cost of crossing each cell of <c>flatCells[start..end)</c>: 1 over the flat's lowest ground,
    /// rising linearly to 1 + <paramref name="groundWeight"/> where the ground reaches the flat's level.</summary>
    private void SetCosts(ReadOnlySpan<float> elevation, float flatLevel, int start, int end, double groundWeight)
    {
        double lowest = double.PositiveInfinity;
        for (int k = start; k < end; k++)
        {
            lowest = Math.Min(lowest, (double)elevation[flatCells[k]]);
        }
        double depth = flatLevel - lowest;
        for (int k = start; k < end; k++)
        {
            int cell = flatCells[k];
            double c = 1.0;
            if (depth > 0.0)
            {
                c += groundWeight * (Math.Min((double)elevation[cell], flatLevel) - lowest) / depth;
            }
            cost[cell] = c;
        }
    }

    /// <summary>Least-cost distances over flat <paramref name="id"/> from its first <paramref name="count"/>
    /// outlets (at distance 0) by Dijkstra; a step costs its length times its two cells' mean cost. The
    /// distances do not depend on the order equal-priority cells are taken in.</summary>
    private void ComputeDistances(int side, int id, int count)
    {
        ReadOnlySpan<int> rowStep = D8.RowStep, columnStep = D8.ColumnStep;
        frontier.Clear();
        for (int k = 0; k < count; k++)
        {
            frontier.Enqueue(outlets[k], 0.0);
        }
        while (frontier.TryDequeue(out int cell, out double here))
        {
            if (here > distance[cell])
            {
                continue;                       // a stale entry
            }
            int cy = cell / side, cx = cell - cy * side;
            for (int d = 0; d < D8.NeighbourCount; d++)
            {
                int ny = cy + rowStep[d], nx = cx + columnStep[d];
                if ((uint)ny >= (uint)side || (uint)nx >= (uint)side)
                {
                    continue;
                }
                int m = ny * side + nx;
                if (component[m] != id)
                {
                    continue;
                }
                double step = 0.5 * (cost[cell] + cost[m]);
                if (rowStep[d] != 0 && columnStep[d] != 0)
                {
                    step *= Sqrt2;
                }
                double reached = here + step;
                if (distance[m] < 0.0 || reached < distance[m])
                {
                    distance[m] = reached;
                    frontier.Enqueue(m, reached);
                }
            }
        }
    }

    /// <summary>
    /// The step of flat cell (<paramref name="cy"/>, <paramref name="cx"/>) to a neighbour of its flat strictly
    /// nearer an outlet: with a finite <paramref name="turn"/>, toward the mean of its descents (each neighbour's
    /// descent per unit length along its unit step) turned by it and dithered; else the steepest descent per unit
    /// length.
    /// </summary>
    private int StepAcrossFlat(int side, int id, int cy, int cx, double turn, double dither)
    {
        ReadOnlySpan<int> rowStep = D8.RowStep, columnStep = D8.ColumnStep;
        Span<bool> allowed = stackalloc bool[D8.NeighbourCount];
        double here = distance[cy * side + cx];
        int best = D8.Undecided;
        double steepest = 0.0, towardRow = 0.0, towardColumn = 0.0;
        for (int d = 0; d < D8.NeighbourCount; d++)
        {
            allowed[d] = false;
            int ny = cy + rowStep[d], nx = cx + columnStep[d];
            if ((uint)ny >= (uint)side || (uint)nx >= (uint)side)
            {
                continue;
            }
            int m = ny * side + nx;
            if (component[m] != id || !(distance[m] < here))
            {
                continue;
            }
            allowed[d] = true;
            double descent = here - distance[m];
            double rate = descent;
            if (rowStep[d] != 0 && columnStep[d] != 0)
            {
                descent *= 0.5;
                rate *= Diagonal;
            }
            towardRow += rowStep[d] * descent;
            towardColumn += columnStep[d] * descent;
            if (rate > steepest)
            {
                steepest = rate;
                best = d;
            }
        }
        if (!double.IsNaN(turn))
        {
            best = Aim(Math.Atan2(towardRow, towardColumn) + turn, dither, allowed);
        }
        return best;
    }

    /// <summary>The cell of <c>flatCells[start..end)</c> with the lowest ground, the first in row-major order
    /// (smallest window index) among equals: the same cell in any window that holds the flat whole.</summary>
    private int LowestCell(ReadOnlySpan<float> elevation, int start, int end)
    {
        int lowest = flatCells[start];
        float lowestHeight = elevation[lowest];
        for (int k = start + 1; k < end; k++)
        {
            int cell = flatCells[k];
            float height = elevation[cell];
            if (height < lowestHeight || (height == lowestHeight && cell < lowest))
            {
                lowest = cell;
                lowestHeight = height;
            }
        }
        return lowest;
    }

    /// <summary>Sea: known and at or below 0.</summary>
    private static bool IsSea(float level) => level <= 0.0f;     // false for NaN

    /// <summary>Whether window cell (y, x) exists, is known and is strictly lower than
    /// <paramref name="than"/>.</summary>
    private static bool IsLowerKnown(ReadOnlySpan<float> level, int side, int y, int x, float than)
    {
        if ((uint)y >= (uint)side || (uint)x >= (uint)side)
        {
            return false;
        }
        float v = level[y * side + x];
        return v < than;                                         // false for NaN
    }

    /// <summary>Gives a window cell <paramref name="code"/> if it lies in the tile and is still undecided.</summary>
    private static void SetIfUndecided(int cell, sbyte code, int side, int tileRow, int tileColumn, int tileSize,
                                       Span<sbyte> directions)
    {
        int cy = cell / side - tileRow, cx = cell % side - tileColumn;
        if ((uint)cy < (uint)tileSize && (uint)cx < (uint)tileSize && directions[cy * tileSize + cx] == D8.Undecided)
        {
            directions[cy * tileSize + cx] = code;
        }
    }

    /// <summary>Frees the buffers if they hold more than <paramref name="maxCells"/> cells (they grow again on
    /// the next call that needs them).</summary>
    /// <param name="maxCells">Largest window, in cells, worth keeping buffers for.</param>
    public void Trim(int maxCells)
    {
        if (component.Length <= maxCells)
        {
            return;
        }
        component = flatCells = outlets = Array.Empty<int>();
        distance = cost = Array.Empty<double>();
    }

    /// <summary>Grows the buffers for windows of <paramref name="cells"/> cells (new ones start reset).</summary>
    private void EnsureCapacity(int cells)
    {
        if (component.Length >= cells)
        {
            return;
        }
        component = new int[cells];
        distance = new double[cells];
        cost = new double[cells];
        flatCells = new int[cells];
        outlets = new int[cells];
        Array.Fill(component, -1);
        Array.Fill(distance, -1.0);
    }
}
