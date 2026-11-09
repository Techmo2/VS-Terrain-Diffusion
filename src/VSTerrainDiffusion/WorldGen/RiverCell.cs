using System;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// A point on a river's smoothed centreline, one per river column, in world blocks (a column's
/// centre is at its integer coordinates). Its channel is the capsule of radius <see cref="Reach"/>
/// around the segment to the next point downstream, deepest along the line and shallowing to the banks.
/// </summary>
/// <param name="X">The smoothed centreline point's X.</param>
/// <param name="Z">The smoothed centreline point's Z.</param>
/// <param name="NextX">The next point downstream's X; equal to <paramref name="X"/> where there is none.</param>
/// <param name="NextZ">The next point downstream's Z; equal to <paramref name="Z"/> where there is none.</param>
/// <param name="TopY">The water surface block: <c>ElevationToBlockY(Level)</c> of its own ground.</param>
/// <param name="WidthBlocks">Channel width, at least 1.</param>
/// <param name="DepthBlocks">Depth on the centreline below <paramref name="TopY"/>, 1 to <see cref="RiverGeometry.MaxDepthBlocks"/>.</param>
/// <param name="Speed">Mean velocity, m/s.</param>
/// <param name="Heading">Direction of flow in radians, atan2(dz, dx): 0 is east, π/2 south.</param>
/// <param name="Dry">Too little runoff for water: a dry bed.</param>
public readonly record struct RiverCell(
    float X,
    float Z,
    float NextX,
    float NextZ,
    short TopY,
    float WidthBlocks,
    byte DepthBlocks,
    float Speed,
    float Heading,
    bool Dry)
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
