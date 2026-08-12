using System;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// One rectangle of model output. Rows run along the pipeline's i axis (world Z), columns along
/// j (world X), both in native model pixels unless the caller upsampled first.
/// </summary>
public sealed class TerrainSample
{
    /// <summary>Number of climate planes; see <see cref="Climate"/>.</summary>
    public const int ClimateChannels = 4;

    public readonly int Height;
    public readonly int Width;

    /// <summary>Elevation in metres, row-major, <see cref="Height"/> * <see cref="Width"/> long.</summary>
    public readonly float[] Elevation;

    /// <summary>
    /// Four planes of WorldClim bioclimatic variables, each <see cref="Height"/> *
    /// <see cref="Width"/> long and stored one after another:
    /// <list type="number">
    /// <item>BIO1  - annual mean temperature at the surface, degrees Celsius</item>
    /// <item>BIO4  - temperature seasonality, the standard deviation of monthly means times 100</item>
    /// <item>BIO12 - annual precipitation, millimetres</item>
    /// <item>BIO15 - precipitation seasonality, coefficient of variation as a percentage</item>
    /// </list>
    /// Null when the caller asked for elevation only.
    /// </summary>
    public readonly float[] Climate;

    public TerrainSample(int height, int width, float[] elevation, float[] climate)
    {
        Height = height;
        Width = width;
        Elevation = elevation;
        Climate = climate;
    }
}

/// <summary>
/// Where terrain comes from. Two implementations exist: the Terrain Diffusion HTTP API, and the
/// ONNX pipeline embedded in this mod. Both produce the same units, so everything above this
/// interface is written once.
/// </summary>
public interface ITerrainSource : IDisposable
{
    /// <summary>Metres of real-world ground covered by one native model pixel.</summary>
    float NativeResolutionMeters { get; }

    /// <summary>
    /// Samples the half-open window [i1, i2) x [j1, j2) in native model pixels. Implementations
    /// are not required to be thread safe; callers serialise access.
    /// </summary>
    TerrainSample Fetch(int i1, int j1, int i2, int j2, bool withClimate);

    /// <summary>One line naming the source, for the startup log and the status command.</summary>
    string Describe();
}
