namespace VSTerrainDiffusion.WorldGen;

/// <summary>Where <see cref="TerrainDiffusionProvider.SampleSurfaceY"/> takes a column's height from.</summary>
public enum SurfaceSampleMode
{
    /// <summary>The terrain tile, generated if need be: exactly what the world gets.</summary>
    Full,

    /// <summary>The coarse model alone: blurred, but no tile is generated.</summary>
    Coarse,

    /// <summary>The terrain tile if it is already in memory or the tile database, else the coarse model.</summary>
    Stored
}
