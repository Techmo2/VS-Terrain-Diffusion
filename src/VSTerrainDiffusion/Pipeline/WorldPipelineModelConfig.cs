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
    private const string ConfigFileName = "world_pipeline_config.json";

    private static WorldPipelineModelConfig _instance;
    private static readonly object Gate = new();

    [JsonProperty("coarse_means")] public float[] CoarseMeans;
    [JsonProperty("coarse_stds")] public float[] CoarseStds;
    [JsonProperty("coarse_pooling")] public int CoarsePooling = 1;
    [JsonProperty("cond_snr")] public float[] CondSnr;
    [JsonProperty("frequency_mult")] public float[] FrequencyMult;
    [JsonProperty("histogram_raw")] public float[] HistogramRaw;
    [JsonProperty("latent_compression")] public int LatentCompression = 8;
    [JsonProperty("native_resolution")] public float NativeResolution = 30f;
    [JsonProperty("residual_mean")] public float ResidualMean;
    [JsonProperty("residual_std")] public float ResidualStd = 0.7f;
    [JsonProperty("drop_water_pct")] public float DropWaterPct;
    [JsonProperty("elev_coarse_pool_mode")] public string ElevCoarsePoolMode;
    [JsonProperty("p5_coarse_pool_mode")] public string P5CoarsePoolMode;

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

    private static void RequireLength(float[] values, int expected, string field)
    {
        if (values == null || values.Length != expected)
            throw new ArgumentException($"{field} must contain exactly {expected} values");
    }
}
