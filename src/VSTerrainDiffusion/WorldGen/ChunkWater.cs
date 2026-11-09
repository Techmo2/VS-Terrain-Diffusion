using System;
using TerrainDiffusion.Water;
using TerrainDiffusion.Water.Grid;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// The final water of one 32 x 32 chunk column, copied out of the river graph when the chunk is
/// released: lake tops per column, and the river cells with their channel geometry.
/// </summary>
public sealed class ChunkWater
{
    public static readonly ChunkWater Dry = new(null, null, Array.Empty<RiverCell>());

    /// <summary>Top lake water block Y per column (index lz * 32 + lx), 0 where none; null if the chunk has no lake.</summary>
    public readonly short[] LakeTop;

    /// <summary>Wetness of wetland columns (1 to 255: how much of the lake's dropped water stood there), 0 elsewhere; null if none.</summary>
    public readonly byte[] Wetland;

    /// <summary>River cells of this chunk: points on the smoothed centreline.</summary>
    public readonly RiverCell[] Rivers;

    public ChunkWater(short[] lakeTop, byte[] wetland, RiverCell[] rivers)
    {
        LakeTop = lakeTop;
        Wetland = wetland;
        Rivers = rivers;
    }

    public bool SameAs(ChunkWater other)
    {
        if (other == null) return false;
        if ((LakeTop == null) != (other.LakeTop == null)) return false;
        if (LakeTop != null && !LakeTop.AsSpan().SequenceEqual(other.LakeTop)) return false;
        if ((Wetland == null) != (other.Wetland == null)) return false;
        if (Wetland != null && !Wetland.AsSpan().SequenceEqual(other.Wetland)) return false;
        return Rivers.AsSpan().SequenceEqual(other.Rivers);
    }
}

/// <summary>
/// A point on a river's smoothed centreline, one per river column, in world blocks (a column's
/// centre is at its integer coordinates). Its channel is the capsule of radius <see cref="Reach"/>
/// around the segment to the next point downstream, deepest along the line and shallowing to the banks.
/// </summary>
public readonly record struct RiverCell(
    float X,                // the smoothed centreline point
    float Z,
    float NextX,            // the next point downstream (equal to X, Z where there is none)
    float NextZ,
    short TopY,             // the water surface block: ElevationToBlockY(Level), its own ground
    float WidthBlocks,      // channel width, at least 1
    byte DepthBlocks,       // depth on the centreline below TopY, 1 to RiverGeometry.MaxDepthBlocks
    float Speed,            // mean velocity, m/s
    float Heading,          // radians, atan2(dz, dx): 0 is east, pi/2 south
    bool Dry)               // too little runoff for water: a dry bed
{
    /// <summary>Columns whose centres are this close to the segment are in the channel; 0.75 keeps one-block rivers face-connected on diagonals.</summary>
    public double Reach => Math.Max(0.75, WidthBlocks / 2.0);

    /// <summary>Distance from a column centre to the segment.</summary>
    public double DistanceTo(double x, double z)
    {
        double ax = X, az = Z, bx = NextX - ax, bz = NextZ - az;
        double length = bx * bx + bz * bz;
        double t = length > 0.0 ? Math.Clamp(((x - ax) * bx + (z - az) * bz) / length, 0.0, 1.0) : 0.0;
        double dx = x - (ax + t * bx), dz = z - (az + t * bz);
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Blocks of water below <see cref="TopY"/> at a distance from the centreline: parabolic, at least 1.</summary>
    public int DepthAt(double distance)
    {
        double half = Math.Max(0.5, WidthBlocks / 2.0);
        double across = Math.Min(1.0, distance / half);
        return Math.Max(1, (int)Math.Round(DepthBlocks * (1.0 - across * across)));
    }
}

/// <summary>
/// Channel geometry from a river cell's drainage area and climate: Turc runoff, then downstream
/// hydraulic geometry (Leopold and Maddock): width 5 Q^0.5, depth 0.4 Q^0.4, velocity from
/// continuity. Depth follows discharge alone, so it only grows downstream. The centreline is the
/// D8 flow path smoothed along itself. Uses only what is fixed once a chunk is released: the
/// cell's final discharge, and its own tile's directions, heights and runoff. No fused
/// multiply-adds, so every machine gets the same numbers.
/// </summary>
public static class RiverGeometry
{
    /// <summary>Drainage, in cells, from which a column is a river, by mode (the lab's thresholds at cap 2^19).</summary>
    public static double ThresholdCells(RiverMode mode) => mode switch
    {
        RiverMode.Creek => 10485.76,
        RiverMode.River => 20971.52,
        RiverMode.Trunk => 41943.04,
        _ => 0.0
    };

    /// <summary>
    /// Half of it plus the centreline's reach from its column (up to <see cref="MaxShift"/> plus a
    /// diagonal step) stays within 32 blocks, so a chunk needs only its 8 neighbours' cells.
    /// </summary>
    public const int MaxWidthBlocks = 57;
    public const int MaxDepthBlocks = 8;

    private const double WidthCoefficient = 5.0, WidthExponent = 0.5;
    private const double DepthCoefficient = 0.4, DepthExponent = 0.4;
    private const double DryDischarge = 0.01;     // m^3/s
    private const double SecondsPerYear = 31_557_600.0;
    private const int HeadingSteps = 4;

    /// <summary>Steps either way the centreline is averaged over, Gaussian weights of this spread.</summary>
    private const int SmoothSteps = 6;
    private const double SmoothSigma = 3.0;
    /// <summary>Most a centreline point moves from its column.</summary>
    private const double MaxShift = 2.0;

    private static readonly double[] SmoothWeights = BuildWeights();

    private static double[] BuildWeights()
    {
        var weights = new double[SmoothSteps + 1];
        for (int i = 0; i <= SmoothSteps; i++) weights[i] = Math.Exp(-(i * i) / (2.0 * SmoothSigma * SmoothSigma));
        return weights;
    }

    /// <summary>Mean annual runoff, mm/yr, from precipitation (mm/yr) and temperature (C) (Turc 1954).</summary>
    public static float Runoff(float precipitationMm, float temperatureC)
    {
        double p = precipitationMm, t = temperatureC;
        if (!(p > 0.0)) return 0f;
        double capacity = Math.Max(1.0, 300.0 + 25.0 * t + 0.05 * t * t * t);
        double ratio = p / capacity;
        double evaporation = Math.Min(p, p / Math.Sqrt(0.9 + ratio * ratio));
        return (float)Math.Max(0.0, p - evaporation);
    }

    /// <summary>The river cell at <paramref name="cell"/> of a released tile whose first column is (tileX, tileZ).</summary>
    public static RiverCell Cell(in TileWater water, float[] runoff, int cell, int tileX, int tileZ, int topY,
                                 DiffusionWorldSettings settings, double cap, float widthFactor)
    {
        int size = WorldWater.TileBlocks;
        double dx = settings.MetersPerBlock;

        double area = Math.Min(water.Discharge[cell], cap) * dx * dx;
        double q = runoff[cell] / 1000.0 * area / SecondsPerYear;
        double width = WidthCoefficient * Math.Pow(q, WidthExponent);
        double depth = DepthCoefficient * Math.Pow(q, DepthExponent);
        bool dry = !(q >= DryDischarge);
        double velocity = dry ? 0.0 : q / (width * depth);

        double widthBlocks = Math.Min(MaxWidthBlocks, Math.Max(1.0, widthFactor * width / dx));
        double vertical = settings.MetersPerBlockVertical;
        int depthBlocks = dry || !(vertical > 0.0)
            ? 1
            : (int)Math.Clamp(Math.Round(widthFactor * depth / vertical), 1, MaxDepthBlocks);

        (double row, double column) = Centreline(water, cell, size);
        (double nextRow, double nextColumn) = (row, column);
        int code = water.Directions[cell];
        if (D8.IsStep(code))
        {
            int r = cell / size + D8.RowStep[code], c = cell % size + D8.ColumnStep[code];
            // Beyond the tile, the next point is that column itself: its own tile smooths nothing at its edge.
            (nextRow, nextColumn) = (uint)r < (uint)size && (uint)c < (uint)size
                ? Centreline(water, r * size + c, size)
                : (r, c);
        }

        return new RiverCell((float)(tileX + column), (float)(tileZ + row), (float)(tileX + nextColumn),
            (float)(tileZ + nextRow), (short)topY, (float)widthBlocks, (byte)depthBlocks, (float)velocity,
            (float)Heading(water, cell, size), dry);
    }

    /// <summary>
    /// The cell's position averaged along its flow path, the same number of steps up- and downstream
    /// (fewer near a tile edge or the end of the path, so the line still meets the next tile's and
    /// reaches the lake or sea at its end). Upstream follows the lowest inflow, ties by smaller index.
    /// </summary>
    private static (double Row, double Column) Centreline(in TileWater water, int cell, int size)
    {
        ReadOnlySpan<sbyte> directions = water.Directions;
        ReadOnlySpan<float> elevation = water.Elevation;
        Span<int> down = stackalloc int[SmoothSteps];
        Span<int> up = stackalloc int[SmoothSteps];

        int downCount = 0;
        for (int at = cell; downCount < SmoothSteps; downCount++)
        {
            int code = directions[at];
            if (!D8.IsStep(code)) break;
            int r = at / size + D8.RowStep[code], c = at % size + D8.ColumnStep[code];
            if ((uint)r >= (uint)size || (uint)c >= (uint)size) break;
            at = r * size + c;
            down[downCount] = at;
        }

        int upCount = 0;
        for (int at = cell; upCount < downCount; upCount++)
        {
            int row = at / size, column = at % size, best = -1;
            for (int code = 0; code < D8.NeighbourCount; code++)
            {
                // The neighbour whose water steps into this cell by this code.
                int r = row - D8.RowStep[code], c = column - D8.ColumnStep[code];
                if ((uint)r >= (uint)size || (uint)c >= (uint)size) continue;
                int source = r * size + c;
                if (directions[source] != code) continue;
                if (best < 0 || elevation[source] < elevation[best] || (elevation[source] == elevation[best] && source < best))
                    best = source;
            }
            if (best < 0) break;
            at = best;
            up[upCount] = at;
        }

        int steps = Math.Min(downCount, upCount);
        double row0 = cell / size, column0 = cell % size;
        double sum = SmoothWeights[0], sumRow = row0 * sum, sumColumn = column0 * sum;
        for (int i = 0; i < steps; i++)
        {
            double w = SmoothWeights[i + 1];
            sumRow += w * (down[i] / size + up[i] / size);
            sumColumn += w * (down[i] % size + up[i] % size);
            sum += 2.0 * w;
        }

        double shiftRow = sumRow / sum - row0, shiftColumn = sumColumn / sum - column0;
        double shift = Math.Sqrt(shiftRow * shiftRow + shiftColumn * shiftColumn);
        if (shift > MaxShift)
        {
            shiftRow *= MaxShift / shift;
            shiftColumn *= MaxShift / shift;
        }
        return (row0 + shiftRow, column0 + shiftColumn);
    }

    /// <summary>The channel tangent: towards the cell 4 steps downstream, within the tile.</summary>
    private static double Heading(in TileWater water, int cell, int size)
    {
        ReadOnlySpan<sbyte> directions = water.Directions;
        int row0 = cell / size, column0 = cell % size;
        int row = row0, column = column0;
        for (int i = 0; i < HeadingSteps; i++)
        {
            int code = directions[row * size + column];
            if (!D8.IsStep(code)) break;
            int r = row + D8.RowStep[code], c = column + D8.ColumnStep[code];
            if ((uint)r >= (uint)size || (uint)c >= (uint)size) break;
            row = r;
            column = c;
        }

        if (row == row0 && column == column0)
        {
            int code = directions[cell];
            return D8.IsStep(code) ? D8.Heading[code] : 0.0;
        }
        return Math.Atan2(row - row0, column - column0);
    }
}
