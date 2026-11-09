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
    /// <summary>A chunk with no lake, wetland or river.</summary>
    public static readonly ChunkWater Dry = new(null, null, Array.Empty<RiverCell>());

    /// <summary>Top lake water block Y per column (index lz * 32 + lx), 0 where none; null if the chunk has no lake.</summary>
    public readonly short[] LakeTop;

    /// <summary>Wetness of wetland columns (1 to 255: how much of the lake's dropped water stood there), 0 elsewhere; null if none.</summary>
    public readonly byte[] Wetland;

    /// <summary>River cells of this chunk: points on the smoothed centreline.</summary>
    public readonly RiverCell[] Rivers;

    /// <summary>One chunk's water, as copied out of the river graph.</summary>
    /// <param name="lakeTop">See <see cref="LakeTop"/>.</param>
    /// <param name="wetland">See <see cref="Wetland"/>.</param>
    /// <param name="rivers">See <see cref="Rivers"/>.</param>
    public ChunkWater(short[] lakeTop, byte[] wetland, RiverCell[] rivers)
    {
        LakeTop = lakeTop;
        Wetland = wetland;
        Rivers = rivers;
    }

    /// <summary>Whether <paramref name="other"/> holds exactly the same water, value for value.</summary>
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
