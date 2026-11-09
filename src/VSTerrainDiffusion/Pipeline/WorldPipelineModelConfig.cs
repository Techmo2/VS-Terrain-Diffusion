using System;
using System.IO;
using Newtonsoft.Json;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Model-specific WorldPipeline constants, read from <c>world_pipeline_config.json</c> so that a
/// differently trained model can be swapped in by replacing a single file.
/// </summary>
public sealed class WorldPipelineModelConfig
{
    /// <summary>The config's file name among the model assets.</summary>
    private const string ConfigFileName = "world_pipeline_config.json";

    /// <summary>
    /// Pixel size of the model this mod ships against, for the one caller that needs a number
    /// before the model files have been downloaded.
    /// </summary>
    public const float DefaultNativeResolution = 30f;

    /// <summary>The loaded config, once read.</summary>
    private static WorldPipelineModelConfig _instance;

    /// <summary>Serialises the first load.</summary>
    private static readonly object Gate = new();

    /// <summary>Means of the annual coarse model's six channels.</summary>
    [JsonProperty("coarse_means")] public float[] CoarseMeans;

    /// <summary>Standard deviations of the annual coarse model's six channels.</summary>
    [JsonProperty("coarse_stds")] public float[] CoarseStds;

    /// <summary>
    /// Pooling the model was trained with; only 1 is supported (pooling is the world's choice).
    /// </summary>
    [JsonProperty("coarse_pooling")] public int CoarsePooling = 1;

    /// <summary>The coarse model's default conditioning noise level per channel.</summary>
    [JsonProperty("cond_snr")] public float[] CondSnr;

    /// <summary>Frequency multipliers of the synthetic map's five noise channels.</summary>
    [JsonProperty("frequency_mult")] public float[] FrequencyMult;

    /// <summary>The base model's terrain-type histogram input, or null for zeros.</summary>
    [JsonProperty("histogram_raw")] public float[] HistogramRaw;

    /// <summary>Native pixels per latent pixel along each axis.</summary>
    [JsonProperty("latent_compression")] public int LatentCompression = 8;

    /// <summary>Metres per native pixel.</summary>
    [JsonProperty("native_resolution")] public float NativeResolution = DefaultNativeResolution;

    /// <summary>Mean of the decoder's residual.</summary>
    [JsonProperty("residual_mean")] public float ResidualMean;

    /// <summary>Standard deviation of the decoder's residual.</summary>
    [JsonProperty("residual_std")] public float ResidualStd = 0.7f;

    /// <summary>
    /// The reference's share of water dropped from training; read for completeness, unused.
    /// </summary>
    [JsonProperty("drop_water_pct")] public float DropWaterPct;

    /// <summary>The reference's pooling mode for elevation; read for completeness, unused.</summary>
    [JsonProperty("elev_coarse_pool_mode")] public string ElevCoarsePoolMode;

    /// <summary>
    /// The reference's pooling mode for the 5th-percentile elevation; read for completeness, unused.
    /// </summary>
    [JsonProperty("p5_coarse_pool_mode")] public string P5CoarsePoolMode;

    /// <summary>The config, read on first use.</summary>
    /// <exception cref="InvalidOperationException">The file is missing or invalid.</exception>
    public static WorldPipelineModelConfig Instance
    {
        get
        {
            if (_instance != null) return _instance;
            lock (Gate)
            {
                _instance ??= Load();
                return _instance;
            }
        }
    }

    /// <summary>Reads and validates the config.</summary>
    /// <returns>The config.</returns>
    private static WorldPipelineModelConfig Load()
    {
        string path = ModelAssetManager.ResolveAssetPath(ConfigFileName);
        try
        {
            var config = JsonConvert.DeserializeObject<WorldPipelineModelConfig>(File.ReadAllText(path));
            config.Validate();
            return config;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("Failed to load model config from " + path, e);
        }
    }

    /// <summary>Checks array lengths and positive sizes.</summary>
    /// <exception cref="ArgumentException">A value is out of range.</exception>
    private void Validate()
    {
        RequireLength(CoarseMeans, 6, "coarse_means");
        RequireLength(CoarseStds, 6, "coarse_stds");
        RequireLength(CondSnr, 5, "cond_snr");
        RequireLength(FrequencyMult, 5, "frequency_mult");
        if (HistogramRaw != null && HistogramRaw.Length != 5)
            throw new ArgumentException("histogram_raw must contain exactly 5 values when provided");
        if (CoarsePooling <= 0) throw new ArgumentException("coarse_pooling must be > 0");
        if (LatentCompression <= 0) throw new ArgumentException("latent_compression must be > 0");
        if (NativeResolution <= 0f) throw new ArgumentException("native_resolution must be > 0");
    }

    /// <summary>Checks an array's length.</summary>
    /// <param name="values">The array.</param>
    /// <param name="expected">Its required length.</param>
    /// <param name="field">Its JSON name, for the message.</param>
    /// <exception cref="ArgumentException">Missing or the wrong length.</exception>
    private static void RequireLength(float[] values, int expected, string field)
    {
        if (values == null || values.Length != expected)
            throw new ArgumentException($"{field} must contain exactly {expected} values");
    }
}
