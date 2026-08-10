using Vintagestory.API.Common;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Machine-level settings, stored in <c>ModConfig/vsterraindiffusion.json</c>. These are not part of
/// the save game: they describe the hardware the server is running on, not the world.
/// </summary>
public class DiffusionConfig
{
    /// <summary>"auto", "cpu", "cuda", "directml" or "coreml".</summary>
    public string InferenceDevice { get; set; } = "auto";

    /// <summary>
    /// Keep only one model resident on the GPU at a time. Costs a little time per stage switch but
    /// keeps peak VRAM near 1.5 GB instead of ~2.5 GB.
    /// </summary>
    public bool OffloadModels { get; set; } = true;

    /// <summary>Verify SHA-256 of pre-existing model files on startup.</summary>
    public bool ValidateModelHashes { get; set; } = true;

    /// <summary>
    /// Download the matching ONNX Runtime native library automatically. Turn off to supply your own
    /// in <c>TerrainDiffusionModels/onnxruntime/&lt;rid&gt;/</c>.
    /// </summary>
    public bool DownloadRuntime { get; set; } = true;

    /// <summary>Megabytes of decoded tensor windows kept per pipeline stage.</summary>
    public int TileCacheMegabytes { get; set; } = 256;

    /// <summary>
    /// Megabytes of finished terrain tiles to keep. This has to cover everything world generation
    /// touches at once — a spawn area alone can span a hundred tiles — or tiles get evicted while
    /// still in use and are rebuilt from scratch.
    /// </summary>
    public int TerrainTileCacheMegabytes { get; set; } = 192;

    /// <summary>
    /// Number of chunk columns' worth of terrain generated per model invocation, in blocks. Larger
    /// values amortise model latency over more chunks at the cost of a longer first-visit stall.
    /// Must be a multiple of 32.
    /// </summary>
    public int TerrainTileSizeBlocks { get; set; } = 256;

    /// <summary>Log a line for every window the model computes. Very noisy; useful when profiling.</summary>
    public bool VerboseInference { get; set; }

    /// <summary>
    /// Terrain shaping. Unlike the rest of this file these do change what the world looks like, so
    /// editing them after a world has been explored will make new chunks disagree with old ones.
    /// </summary>
    public WorldGenConfig WorldGen { get; set; } = new();

    private static DiffusionConfig _instance;

    public static DiffusionConfig Instance => _instance ?? (_instance = new DiffusionConfig());

    public static DiffusionConfig Load(ICoreAPI api)
    {
        DiffusionConfig config = null;
        try
        {
            config = api.LoadModConfig<DiffusionConfig>(DiffusionPaths.ModId + ".json");
        }
        catch (System.Exception e)
        {
            api.Logger.Error("[{0}] Could not read config, falling back to defaults: {1}", DiffusionPaths.ModId, e.Message);
        }

        config ??= new DiffusionConfig();
        config.Sanitize();
        api.StoreModConfig(config, DiffusionPaths.ModId + ".json");
        _instance = config;
        return config;
    }

    private void Sanitize()
    {
        (WorldGen ??= new WorldGenConfig()).Sanitize();

        if (TileCacheMegabytes < 32) TileCacheMegabytes = 32;
        if (TileCacheMegabytes > 4096) TileCacheMegabytes = 4096;

        if (TerrainTileCacheMegabytes < 32) TerrainTileCacheMegabytes = 32;
        if (TerrainTileCacheMegabytes > 4096) TerrainTileCacheMegabytes = 4096;

        if (TerrainTileSizeBlocks < 64) TerrainTileSizeBlocks = 64;
        if (TerrainTileSizeBlocks > 1024) TerrainTileSizeBlocks = 1024;
        TerrainTileSizeBlocks -= TerrainTileSizeBlocks % 32;

        InferenceDevice = (InferenceDevice ?? "auto").Trim().ToLowerInvariant();
        switch (InferenceDevice)
        {
            case "auto":
            case "cpu":
            case "cuda":
            case "directml":
            case "dml":
            case "coreml":
            case "gpu":
                break;
            default:
                InferenceDevice = "auto";
                break;
        }
    }
}

/// <summary>
/// Everything that decides how model metres become blocks.
///
/// The defaults aim for terrain that reads as mountainous from ground level rather than terrain
/// that is geometrically true to scale. Real landscapes are far wider than they are tall — a
/// 1 000 m peak spread over 10 km is a 6% grade — so a heightmap reproduced at true scale in a
/// world only a few hundred blocks tall looks like gentle moorland. <see cref="HeightMode"/>
/// "auto" measures how tall the terrain around spawn actually gets and picks the vertical gain
/// that makes those peaks reach near the world ceiling.
/// </summary>
public class WorldGenConfig
{
    /// <summary>
    /// "auto" measures the region's tall peaks once per world and scales terrain to fill the
    /// world height. "manual" uses <see cref="MetersPerBlockVertical"/> (or true scale) instead.
    /// </summary>
    public string HeightMode { get; set; } = "auto";

    /// <summary>
    /// auto: how much of the space between sea level and the world ceiling the region's tall
    /// peaks should occupy. Leave a little room, or the summits flatten against the ceiling.
    /// </summary>
    public float TargetPeakFillFraction { get; set; } = 0.92f;

    /// <summary>
    /// auto: which elevation quantile counts as a "tall peak". 0.995 means the top half percent of
    /// the surveyed area reaches the ceiling; lowering it makes the whole landscape taller and
    /// clips more summits.
    /// </summary>
    public float PeakQuantile { get; set; } = 0.995f;

    /// <summary>
    /// auto: half-width, in blocks, of the area surveyed around spawn. This should cover the part
    /// of the world you expect to explore: surveying a whole continent lets a distant mountain
    /// range decide the scale and leaves your own surroundings flat.
    /// </summary>
    public int CalibrationRadiusBlocks { get; set; } = 4096;

    /// <summary>
    /// auto: how many full-detail probes to run on the tallest surveyed cells. The survey itself
    /// only sees terrain averaged over several kilometres, so peaks need measuring at full
    /// resolution. Each probe costs about as much as one terrain tile, once per world. Zero skips
    /// probing and falls back to <see cref="ReliefFactor"/>.
    /// </summary>
    public int CalibrationProbes { get; set; } = 4;

    /// <summary>
    /// auto: assumed ratio of true peak height to the coarse survey's value, used when probing is
    /// disabled or fails.
    /// </summary>
    public float ReliefFactor { get; set; } = 1.6f;

    /// <summary>auto: bounds on the vertical exaggeration calibration is allowed to choose.</summary>
    public float MinAutoExaggeration { get; set; } = 1f;

    public float MaxAutoExaggeration { get; set; } = 20f;

    /// <summary>
    /// manual: metres of elevation per block of height. Zero means "same as horizontal", which is
    /// true real-world scale. Ignored in auto mode.
    /// </summary>
    public float MetersPerBlockVertical { get; set; }

    /// <summary>
    /// Fraction of the available height that is mapped perfectly linearly. Above the knee the
    /// curve bends over so that arbitrarily tall model peaks still fit under the ceiling; the
    /// closer this is to 1 the more faithful the summits and the harder they clip.
    /// </summary>
    public float LinearKneeFraction { get; set; } = 0.85f;

    /// <summary>Fraction of the space below sea level that the deepest ocean reaches.</summary>
    public float OceanDepthFraction { get; set; } = 0.9f;

    /// <summary>
    /// Multiplies the Perlin detail added to sloped ground. The model resolves features down to
    /// about 30 m, so hillsides need roughness of their own; raise for craggier slopes.
    /// </summary>
    public float SlopeDetailStrength { get; set; } = 1f;

    /// <summary>
    /// How strongly the model's regional climate pulls temperature away from the latitude band it
    /// sits in, in the "regional" climate mode. 0 is pure vanilla latitude, 1 is pure model.
    /// </summary>
    public float RegionalClimateStrength { get; set; } = 0.6f;

    /// <summary>
    /// How model precipitation becomes Vintage Story rainfall; see <see cref="PrecipitationScale"/>.
    /// "distribution" quantile-maps the model's log-normal precipitation onto the uniform 0-255
    /// spread that every vanilla rainfall threshold was tuned against. "absolute" is the older
    /// fixed curve, <c>1 - exp(-mm / rainfallAbsoluteScaleMm)</c>, which reads a good deal drier.
    /// </summary>
    public string RainfallCurve { get; set; } = "distribution";

    /// <summary>
    /// distribution: annual precipitation, in millimetres, that maps to the middle of the rainfall
    /// scale. Measured over the model's own output; lower makes the whole world wetter.
    /// </summary>
    public float RainfallMedianMm { get; set; } = 490f;

    /// <summary>
    /// distribution: spread of the model's log precipitation. Smaller pulls the world towards the
    /// middle of the rainfall scale, larger pushes deserts and rainforests further apart.
    /// </summary>
    public float RainfallSpread { get; set; } = 0.95f;

    /// <summary>absolute: millimetres at which the old saturating curve reaches 63% of full scale.</summary>
    public float RainfallAbsoluteScaleMm { get; set; } = 1200f;

    /// <summary>
    /// Added to the final rainfall as a fraction of full scale. The default nudge exists because
    /// the climate map pre-compensates away Vintage Story's own "higher ground is wetter" bonus —
    /// the model already models orography properly — and vanilla's biome thresholds were tuned
    /// with that bonus present. Raise it for a lusher world, drop it to zero for the model's
    /// unmodified precipitation.
    /// </summary>
    public float RainfallBias { get; set; } = 0.05f;

    /// <summary>
    /// Stretch the altitude bands of vanilla's surface block layers to match the terrain height,
    /// so that hills which are only tall because of vertical exaggeration are not surfaced as bare
    /// alpine gravel. See <see cref="WorldGen.BlockLayerAltitude"/>.
    /// </summary>
    public bool RescaleBlockLayerAltitudes { get; set; } = true;

    /// <summary>
    /// Overrides the world's "Diffusion resolution" setting. Zero uses the world setting. Values
    /// above 6 are only reachable from here.
    /// </summary>
    public int ScaleOverride { get; set; }

    /// <summary>
    /// Overrides the world's "Vertical exaggeration" setting. Zero uses the world setting. In auto
    /// mode this multiplies the calibrated height rather than setting it outright.
    /// </summary>
    public float VerticalExaggerationOverride { get; set; }

    internal void Sanitize()
    {
        HeightMode = (HeightMode ?? "auto").Trim().ToLowerInvariant();
        if (HeightMode != "manual") HeightMode = "auto";

        TargetPeakFillFraction = Clamp(TargetPeakFillFraction, 0.2f, 1f, 0.92f);
        PeakQuantile = Clamp(PeakQuantile, 0.5f, 1f, 0.995f);
        CalibrationRadiusBlocks = (int)Clamp(CalibrationRadiusBlocks, 512f, 4_000_000f, 16384f);
        CalibrationProbes = (int)Clamp(CalibrationProbes, 0f, 64f, 4f);
        ReliefFactor = Clamp(ReliefFactor, 1f, 5f, 1.6f);

        MinAutoExaggeration = Clamp(MinAutoExaggeration, 0.05f, 100f, 1f);
        MaxAutoExaggeration = Clamp(MaxAutoExaggeration, 0.05f, 100f, 20f);
        if (MaxAutoExaggeration < MinAutoExaggeration) MaxAutoExaggeration = MinAutoExaggeration;

        if (MetersPerBlockVertical < 0f) MetersPerBlockVertical = 0f;

        LinearKneeFraction = Clamp(LinearKneeFraction, 0.1f, 0.99f, 0.85f);
        OceanDepthFraction = Clamp(OceanDepthFraction, 0.05f, 1f, 0.9f);
        SlopeDetailStrength = Clamp(SlopeDetailStrength, 0f, 8f, 1f);
        RegionalClimateStrength = Clamp(RegionalClimateStrength, 0f, 1f, 0.6f);

        RainfallCurve = (RainfallCurve ?? "distribution").Trim().ToLowerInvariant();
        if (RainfallCurve != "absolute") RainfallCurve = "distribution";
        RainfallMedianMm = Clamp(RainfallMedianMm, 10f, 10000f, 490f);
        RainfallSpread = Clamp(RainfallSpread, 0.1f, 4f, 0.95f);
        RainfallAbsoluteScaleMm = Clamp(RainfallAbsoluteScaleMm, 50f, 20000f, 1200f);
        RainfallBias = Clamp(RainfallBias, -1f, 1f, 0.05f);

        if (ScaleOverride != 0) ScaleOverride = (int)Clamp(ScaleOverride, 1f, 16f, 0f);
        if (VerticalExaggerationOverride != 0f)
            VerticalExaggerationOverride = Clamp(VerticalExaggerationOverride, 0.05f, 20f, 0f);
    }

    /// <summary>Clamps, substituting <paramref name="fallback"/> for NaN and other nonsense.</summary>
    private static float Clamp(float value, float min, float max, float fallback)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
        return value < min ? min : value > max ? max : value;
    }
}
