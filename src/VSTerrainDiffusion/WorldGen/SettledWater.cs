using System;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>A chunk's final water: its lake tops, and the river cells within reach of it.</summary>
public sealed class SettledWater
{
    /// <summary>No lakes, wetland or rivers.</summary>
    public static readonly SettledWater Dry = new(null, null, Array.Empty<RiverCell>());

    /// <summary>Top lake water block per column (lz * 32 + lx), 0 where none; null without lakes.</summary>
    public readonly short[] LakeTop;

    /// <summary>Wetness of wetland columns (1 to 255), 0 elsewhere; null without wetland.</summary>
    public readonly byte[] Wetland;

    /// <summary>River cells of the chunk and its neighbours, world coordinates.</summary>
    public readonly RiverCell[] Rivers;

    /// <summary>Creates the water of one chunk.</summary>
    /// <param name="lakeTop">Top lake water block per column, or null.</param>
    /// <param name="wetland">Wetness per column, or null.</param>
    /// <param name="rivers">River cells within reach of the chunk.</param>
    public SettledWater(short[] lakeTop, byte[] wetland, RiverCell[] rivers)
    {
        LakeTop = lakeTop;
        Wetland = wetland;
        Rivers = rivers;
    }
}
