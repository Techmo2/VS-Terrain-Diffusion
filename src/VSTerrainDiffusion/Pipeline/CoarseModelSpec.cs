using System;

namespace VSTerrainDiffusion.Pipeline;

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

    /// <summary>Every model's <see cref="Code"/>, for the world config's choices.</summary>
    public static readonly string[] AllCodes = { Annual.Code, Seasonal.Code };

    /// <summary>The world config value naming this model.</summary>
    public readonly string Code;

    /// <summary>Channels the model produces; the coarse map has one more, the blend weight.</summary>
    public readonly int Channels;

    /// <summary>
    /// Per-channel means the model's output is normalised by; null to take them from the pipeline config.
    /// </summary>
    private readonly float[] _means;

    /// <summary>Per-channel standard deviations, as <see cref="_means"/>.</summary>
    private readonly float[] _stds;

    /// <summary>Only the instances above exist.</summary>
    /// <param name="code">See <see cref="Code"/>.</param>
    /// <param name="channels">See <see cref="Channels"/>.</param>
    /// <param name="means">See <see cref="_means"/>.</param>
    /// <param name="stds">See <see cref="_stds"/>.</param>
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

    /// <summary>Per-channel means that undo the model's output normalisation.</summary>
    /// <param name="config">
    /// The downloaded pipeline config, which holds the annual model's statistics.
    /// </param>
    public float[] Means(WorldPipelineModelConfig config) => _means ?? config.CoarseMeans;

    /// <summary>Per-channel standard deviations that undo the model's output normalisation.</summary>
    /// <param name="config">The downloaded pipeline config.</param>
    public float[] Stds(WorldPipelineModelConfig config) => _stds ?? config.CoarseStds;

    /// <summary>The model a world config value names.</summary>
    /// <param name="code">A <see cref="Code"/>.</param>
    /// <exception cref="ArgumentException">No model has that code.</exception>
    public static CoarseModelSpec ByCode(string code) =>
        Array.Find(new[] { Annual, Seasonal }, spec => spec.Code == code)
        ?? throw new ArgumentException($"\"{code}\" is not a coarse model; expected one of {string.Join(", ", AllCodes)}");
}
