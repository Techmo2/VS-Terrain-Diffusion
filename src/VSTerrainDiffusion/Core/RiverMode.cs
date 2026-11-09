namespace VSTerrainDiffusion.Core;

/// <summary>
/// Which rivers are cut into the land: a column is river once this many cells drain through it
/// (the conditioning lab's thresholds at the default discharge cap; see
/// <see cref="WorldGen.RiverGeometry.ThresholdCells"/>). Off leaves only lakes.
/// </summary>
public enum RiverMode
{
    /// <summary>No rivers; lakes only.</summary>
    Off,

    /// <summary>Every river down to creeks, the smallest threshold.</summary>
    Creek,

    /// <summary>Rivers of middling drainage and up.</summary>
    River,

    /// <summary>Only the largest trunk rivers; the default.</summary>
    Trunk
}
