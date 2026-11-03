using System;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>Indices into a coarse model's output (and the coarse map, which adds a blend weight after the last).</summary>
public static class CoarseChannel
{
    /// <summary>Elevation, signed square-root metres.</summary>
    public const int Elevation = 0;

    /// <summary>Fifth percentile of elevation, signed square-root metres (the map stores elevation minus this).</summary>
    public const int ElevationP5 = 1;

    /// <summary>Mean annual temperature at the cell's elevation, °C (BIO1).</summary>
    public const int Temperature = 2;

    /// <summary>Standard deviation of monthly mean temperatures × 100 (BIO4).</summary>
    public const int TemperatureSeasonality = 3;

    /// <summary>Annual precipitation, mm (BIO12).</summary>
    public const int Precipitation = 4;

    /// <summary>Coefficient of variation of monthly precipitation, % (BIO15).</summary>
    public const int PrecipitationCv = 5;

    /// <summary>Second harmonic of the temperature cycle, cosine and sine, °C.</summary>
    public const int TemperatureShapeCos = 6, TemperatureShapeSin = 7;

    /// <summary>First harmonic of precipitation relative to the monthly mean: when the wet season falls.</summary>
    public const int WetSeasonCos = 8, WetSeasonSin = 9;

    /// <summary>Second harmonic of precipitation relative to the monthly mean: a second rainy season, sharp monsoons.</summary>
    public const int SecondRainyCos = 10, SecondRainySin = 11;

    /// <summary>Mean diurnal temperature range, °C (BIO2).</summary>
    public const int DiurnalRange = 12;

    /// <summary>The annual statistics every coarse model has: elevation, its fifth percentile and four climate channels.</summary>
    public const int AnnualCount = 6;

    /// <summary>The annual channels plus the six seasonal-cycle channels and the diurnal range.</summary>
    public const int SeasonalCount = 13;
}

/// <summary>
/// A coarse model a world can be generated with: which file it is, how many channels it produces and
/// how they are normalised. The base model and decoder are shared; only the first
/// <see cref="CoarseChannel.AnnualCount"/> channels reach them, so a coarse model with more channels
/// adds climate without changing what the later stages read.
/// </summary>
public sealed class CoarseModelSpec
{
    /// <summary>
    /// The released 30 m coarse model: elevation and four annual climate statistics. Its statistics
    /// come from the downloaded <c>world_pipeline_config.json</c>.
    /// </summary>
    public static readonly CoarseModelSpec Annual = new("annual", CoarseChannel.AnnualCount, null, null);

    /// <summary>
    /// <c>td30m_seasonal_ft</c>: also the seasonal cycle and the diurnal range, enough for Köppen
    /// classes. Statistics from that pipeline's <c>config.json</c>; the first six equal
    /// <see cref="Annual"/>'s.
    /// </summary>
    public static readonly CoarseModelSpec Seasonal = new("seasonal", CoarseChannel.SeasonalCount,
        new[]
        {
            -37.70000792952155f, 1.1403065255556186f, 18.102486588653505f, 332.8342598198455f,
            1332.2078969994602f, 52.66008820698148f, -0.08900170125298282f, 0.13602878716589945f,
            0.11280570029374362f, 0.18431031413298046f, 0.0356677303586799f, 0.01711737849047245f,
            8.15579054694333f
        },
        new[]
        {
            39.741999742263f, 1.7681844104569366f, 8.92146918789914f, 321.7660336396052f,
            842.9293648885134f, 31.079985318715828f, 0.5107442071642879f, 0.43318537119655826f,
            0.4573095023774716f, 0.27943438641214907f, 0.1765421282491852f, 0.16334862253159116f,
            3.0757797509575635f
        });

    public static readonly string[] AllCodes = { Annual.Code, Seasonal.Code };

    /// <summary>The world config value naming this model.</summary>
    public readonly string Code;

    /// <summary>Channels the model produces; the coarse map has one more, the blend weight.</summary>
    public readonly int Channels;

    private readonly float[] _means;
    private readonly float[] _stds;

    private CoarseModelSpec(string code, int channels, float[] means, float[] stds)
    {
        Code = code;
        Channels = channels;
        _means = means;
        _stds = stds;
    }

    /// <summary>Whether the model has the seasonal-cycle channels and the diurnal range.</summary>
    public bool HasSeasonalCycle => Channels >= CoarseChannel.SeasonalCount;

    /// <summary>Index of the blend weight in the coarse map.</summary>
    public int WeightChannel => Channels;

    public float[] Means(WorldPipelineModelConfig config) => _means ?? config.CoarseMeans;

    public float[] Stds(WorldPipelineModelConfig config) => _stds ?? config.CoarseStds;

    public static CoarseModelSpec ByCode(string code) =>
        Array.Find(new[] { Annual, Seasonal }, spec => spec.Code == code)
        ?? throw new ArgumentException($"\"{code}\" is not a coarse model; expected one of {string.Join(", ", AllCodes)}");
}

/// <summary>
/// Planes of <see cref="WorldPipeline.Sample.Climate"/>, in the reference pipeline's native climate
/// order: <c>[T, Tseas, P, Pcv, β, t2c, t2s, p1c, p1s, p2c, p2s, DTR]</c>.
/// </summary>
public static class ClimatePlane
{
    public const int Temperature = 0, TemperatureSeasonality = 1, Precipitation = 2, PrecipitationCv = 3, LapseRate = 4;
    public const int TemperatureShapeCos = 5, TemperatureShapeSin = 6, WetSeasonCos = 7, WetSeasonSin = 8;
    public const int SecondRainyCos = 9, SecondRainySin = 10, DiurnalRange = 11;

    /// <summary>Five for the annual model, twelve with the seasonal cycle.</summary>
    public static int CountFor(CoarseModelSpec spec) => spec.HasSeasonalCycle ? 12 : 5;
}
