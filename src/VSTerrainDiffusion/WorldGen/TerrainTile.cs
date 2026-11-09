using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// One square of generated terrain, aligned to the tile grid. Rows run along Z, columns along X.
/// </summary>
public sealed class TerrainTile
{
    /// <summary>World block X of the tile's first column.</summary>
    public readonly int BlockX;

    /// <summary>World block Z of the tile's first row.</summary>
    public readonly int BlockZ;

    /// <summary>Edge of the tile in blocks.</summary>
    public readonly int Size;

    /// <summary>Elevation in metres, including the small-scale slope noise.</summary>
    public readonly float[] ElevationMeters;

    /// <summary>Surface block Y for each column.</summary>
    public readonly int[] SurfaceY;

    /// <summary>Terrain slope as a rise-over-run ratio.</summary>
    public readonly float[] Slope;

    /// <summary>Annual mean temperature at the surface, degrees Celsius (WorldClim BIO1).</summary>
    public readonly float[] TemperatureC;

    /// <summary>Temperature seasonality: standard deviation of monthly means times 100 (BIO4).</summary>
    public readonly float[] TemperatureSeasonality;

    /// <summary>Annual precipitation in millimetres (BIO12).</summary>
    public readonly float[] PrecipitationMm;

    /// <summary>Precipitation seasonality: coefficient of variation, percent (BIO15).</summary>
    public readonly float[] PrecipitationCv;

    /// <summary>Number of per-column arrays held by a tile, used to size the cache.</summary>
    private const int ArraysPerTile = 7;

    /// <summary>Allocates an empty tile.</summary>
    /// <param name="blockX">World block X of the first column.</param>
    /// <param name="blockZ">World block Z of the first row.</param>
    /// <param name="size">Edge length in blocks.</param>
    public TerrainTile(int blockX, int blockZ, int size)
    {
        BlockX = blockX;
        BlockZ = blockZ;
        Size = size;
        int n = size * size;
        ElevationMeters = new float[n];
        SurfaceY = new int[n];
        Slope = new float[n];
        TemperatureC = new float[n];
        TemperatureSeasonality = new float[n];
        PrecipitationMm = new float[n];
        PrecipitationCv = new float[n];
    }

    /// <summary>Index into the tile's arrays of a column, from its offset in the tile.</summary>
    public int Index(int localX, int localZ) => localZ * Size + localX;

    /// <summary>The derived ecological climate at one column.</summary>
    public Bioclim ClimateAt(int index) => new(
        TemperatureC[index], TemperatureSeasonality[index], PrecipitationMm[index], PrecipitationCv[index]);

    /// <summary>Approximate heap cost of one tile of the given edge length, in bytes.</summary>
    public static long EstimateBytes(int size) => (long)size * size * ArraysPerTile * sizeof(float);
}
