using Vintagestory.API.Common;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Machine-level settings, stored in <c>ModConfig/vsterraindiffusion.json</c>. Everything except
/// <see cref="WorldGen"/> describes the hardware the server is running on, not the world itself.
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
    public int TerrainTileCacheMegabytes { get; set; } = 256;

    /// <summary>
    /// Number of chunk columns' worth of terrain generated per model query, in blocks. Larger
    /// values amortise model latency over more chunks at the cost of a longer first-visit stall.
    /// Must be a multiple of 32.
    /// </summary>
    public int TerrainTileSizeBlocks { get; set; } = 256;

    /// <summary>Log a line for every window the model computes. Very noisy; useful when profiling.</summary>
    public bool VerboseInference { get; set; }

    /// <summary>
    /// World shaping and climate. Unlike the rest of this file these change what the world looks
    /// like, so editing them after a world has been explored will make new chunks disagree with old
    /// ones.
    /// </summary>
    public WorldGenConfig WorldGen { get; set; } = new();

    private static DiffusionConfig _instance;

    public static DiffusionConfig Instance => _instance ??= new DiffusionConfig();

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
/// How the model's metres, degrees and millimetres become a Vintage Story world.
///
/// The defaults reproduce the Terrain Diffusion Minecraft mod's geometry: a block is exactly as
/// tall as it is wide, so the landscape is at true scale in every direction and a 2 000 m massif
/// really is 2 000 m of climbing. That only works in a world with the height to hold it, which is
/// why the mod asks for a tall world rather than stretching the terrain to fit a short one.
/// </summary>
public class WorldGenConfig
{
    /// <summary>
    /// "isotropic" makes vertical scale match horizontal, the way the Minecraft mod does it.
    /// "manual" uses <see cref="MetersPerBlockVertical"/>. "auto" measures the region's peaks once
    /// per world and stretches terrain to fill the world height, which suits short worlds at the
    /// cost of exaggerated relief.
    /// </summary>
    public string HeightMode { get; set; } = "isotropic";

    /// <summary>
    /// manual: metres of elevation per block of height. Zero falls back to isotropic.
    /// </summary>
    public float MetersPerBlockVertical { get; set; }

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
    public int CalibrationProbes { get; set; } = 8;

    /// <summary>
    /// auto: assumed ratio of true peak height to the coarse survey's value, used when probing is
    /// disabled or fails.
    /// </summary>
    public float ReliefFactor { get; set; } = 1.6f;

    /// <summary>auto: bounds on the vertical exaggeration calibration is allowed to choose.</summary>
    public float MinAutoExaggeration { get; set; } = 1f;

    public float MaxAutoExaggeration { get; set; } = 20f;

    /// <summary>
    /// Fraction of the available height that is mapped perfectly linearly. Above the knee the
    /// curve bends over so that arbitrarily tall model peaks still fit under the ceiling; the
    /// closer this is to 1 the more faithful the summits and the harder they clip.
    /// </summary>
    public float LinearKneeFraction { get; set; } = 0.85f;

    /// <summary>Fraction of the space below sea level that the deepest ocean reaches.</summary>
    public float OceanDepthFraction { get; set; } = 0.9f;

    /// <summary>
    /// Multiplies the Perlin detail added to sloped ground. The model resolves features down to one
    /// native pixel, so hillsides need roughness of their own; raise for craggier slopes.
    /// </summary>
    public float SlopeDetailStrength { get; set; } = 1f;

    /// <summary>
    /// What the game's 0-255 rainfall byte is built from. "moisture" uses the model's aridity —
    /// precipitation measured against how much the climate can evaporate, discounted for a dry
    /// season — which is what actually decides whether ground is bare, and stops warm-but-rainy
    /// and cold-but-dry places from being read as the same. "precipitation" uses annual millimetres
    /// alone.
    /// </summary>
    public string RainfallBasis { get; set; } = "moisture";

    /// <summary>
    /// moisture: the tree-moisture value that maps to the middle of the rainfall scale.
    /// </summary>
    public float MoistureMedian { get; set; } = 0.62f;

    /// <summary>moisture: spread of log tree-moisture across the model's land.</summary>
    public float MoistureSpread { get; set; } = 1.0f;

    /// <summary>
    /// precipitation: annual millimetres that map to the middle of the rainfall scale.
    /// </summary>
    public float RainfallMedianMm { get; set; } = 540f;

    /// <summary>precipitation: spread of the model's log precipitation over land.</summary>
    public float RainfallSpread { get; set; } = 0.8f;

    /// <summary>
    /// Added to the final rainfall as a fraction of full scale. The climate map cancels Vintage
    /// Story's own "higher ground is wetter" bonus, because the model already models orography
    /// properly, and vanilla's thresholds were tuned with that bonus present; this puts its average
    /// back. Raise for a lusher world, drop to zero for the model's unmodified answer.
    /// </summary>
    public float RainfallBias { get; set; } = 0.05f;

    /// <summary>Degrees Celsius added to every model temperature, for a warmer or colder world.</summary>
    public float TemperatureOffsetC { get; set; }

    /// <summary>
    /// Scales the forest cover the model's moisture implies. Vintage Story's own forest map is
    /// noise with no climate signal at all, so this replaces it outright; raise for denser woods.
    /// </summary>
    public float ForestDensityMultiplier { get; set; } = 1f;

    /// <summary>Scales shrub cover the same way.</summary>
    public float ShrubDensityMultiplier { get; set; } = 1f;

    /// <summary>
    /// Swing temperature through the year using the model's temperature seasonality, instead of
    /// Vintage Story's latitude bands. Continental interiors then get hard winters and hot summers
    /// while maritime and tropical climates stay even.
    /// </summary>
    public bool SeasonalTemperature { get; set; } = true;

    /// <summary>Multiplies the modelled seasonal temperature swing. Zero gives a world with no seasons.</summary>
    public float SeasonalTemperatureStrength { get; set; } = 1f;

    /// <summary>
    /// Swing rainfall through the year using the model's precipitation seasonality, so monsoon
    /// climates get a real wet and dry season rather than drizzling evenly all year.
    /// </summary>
    public bool SeasonalPrecipitation { get; set; } = true;

    /// <summary>Multiplies the modelled wet/dry season contrast.</summary>
    public float SeasonalPrecipitationStrength { get; set; } = 1f;

    /// <summary>
    /// Give the world two hemispheres with opposite seasons, split at the middle of the map. Off by
    /// default: without a latitude temperature gradient to go with it, crossing the line just makes
    /// the calendar disagree with itself.
    /// </summary>
    public bool SeasonHemispheres { get; set; }

    /// <summary>
    /// Stretch the altitude bands of vanilla's surface block layers to match the terrain height, so
    /// that hills which are only tall because of vertical exaggeration are not surfaced as bare
    /// alpine gravel. Has no effect at isotropic scale, where the bands already line up.
    /// </summary>
    public bool RescaleBlockLayerAltitudes { get; set; } = true;

    /// <summary>
    /// Leave slopes too steep to hold soil as bare rock. The threshold comes from the model's own
    /// moisture, because roots are what keep a hillside from shedding its soil.
    /// </summary>
    public bool BareSlopeRock { get; set; } = true;

    /// <summary>
    /// Cap ground whose warmest month never rises above freezing with glacier ice, so ice fields
    /// look permanent rather than like a winter that has not melted yet.
    /// </summary>
    public bool GlacierIce { get; set; } = true;

    /// <summary>
    /// Honour the world's "Starting climate" setting by placing the spawn on land whose modelled
    /// temperature falls in the chosen band. Vanilla implements that setting by shifting its own
    /// climate map, which cannot be done to a model that predicts a specific world, so the player
    /// moves instead. Turn off to spawn on the nearest land whatever its climate.
    /// </summary>
    public bool StartingClimateSearch { get; set; } = true;

    /// <summary>
    /// How far from the middle of the map the starting climate search may look, in blocks. The
    /// search stops as soon as it finds matching land, so this is only the point at which it gives
    /// up and takes the closest temperature it saw. Surveying the full radius costs a few seconds
    /// once per world.
    /// </summary>
    public int StartingClimateSearchRadiusBlocks { get; set; } = 65536;

    /// <summary>
    /// How much more reluctant the search is to move the spawn north or south than east or west.
    /// Distance along Z decides day length in Vintage Story, and past the world's polar distance it
    /// buys midnight sun and polar night; distance along X costs nothing at all. At 2 the search
    /// will go twice as far east for the same climate before it heads for a pole.
    /// </summary>
    public float StartingClimateNorthSouthCost { get; set; } = 2f;

    /// <summary>
    /// Overrides how much of the climate the model drives: "full" for model temperature, rainfall
    /// and vegetation with no latitude bands, "off" to leave Vintage Story's climate alone. Empty
    /// uses the world's own setting, which also defaults to full.
    /// </summary>
    public string ClimateMode { get; set; } = "";

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
        HeightMode = (HeightMode ?? "isotropic").Trim().ToLowerInvariant();
        if (HeightMode is not ("auto" or "manual")) HeightMode = "isotropic";

        TargetPeakFillFraction = Clamp(TargetPeakFillFraction, 0.2f, 1f, 0.92f);
        PeakQuantile = Clamp(PeakQuantile, 0.5f, 1f, 0.995f);
        CalibrationRadiusBlocks = (int)Clamp(CalibrationRadiusBlocks, 512f, 4_000_000f, 4096f);
        CalibrationProbes = (int)Clamp(CalibrationProbes, 0f, 64f, 8f);
        ReliefFactor = Clamp(ReliefFactor, 1f, 5f, 1.6f);

        MinAutoExaggeration = Clamp(MinAutoExaggeration, 0.05f, 100f, 1f);
        MaxAutoExaggeration = Clamp(MaxAutoExaggeration, 0.05f, 100f, 20f);
        if (MaxAutoExaggeration < MinAutoExaggeration) MaxAutoExaggeration = MinAutoExaggeration;

        if (MetersPerBlockVertical < 0f) MetersPerBlockVertical = 0f;

        LinearKneeFraction = Clamp(LinearKneeFraction, 0.1f, 0.99f, 0.85f);
        OceanDepthFraction = Clamp(OceanDepthFraction, 0.05f, 1f, 0.9f);
        SlopeDetailStrength = Clamp(SlopeDetailStrength, 0f, 8f, 1f);

        RainfallBasis = (RainfallBasis ?? "moisture").Trim().ToLowerInvariant();
        if (RainfallBasis != "precipitation") RainfallBasis = "moisture";
        MoistureMedian = Clamp(MoistureMedian, 0.01f, 100f, 0.62f);
        MoistureSpread = Clamp(MoistureSpread, 0.1f, 4f, 1f);
        RainfallMedianMm = Clamp(RainfallMedianMm, 10f, 10000f, 540f);
        RainfallSpread = Clamp(RainfallSpread, 0.1f, 4f, 0.8f);
        RainfallBias = Clamp(RainfallBias, -1f, 1f, 0.05f);
        TemperatureOffsetC = Clamp(TemperatureOffsetC, -40f, 40f, 0f);

        ForestDensityMultiplier = Clamp(ForestDensityMultiplier, 0f, 4f, 1f);
        ShrubDensityMultiplier = Clamp(ShrubDensityMultiplier, 0f, 4f, 1f);

        SeasonalTemperatureStrength = Clamp(SeasonalTemperatureStrength, 0f, 4f, 1f);
        SeasonalPrecipitationStrength = Clamp(SeasonalPrecipitationStrength, 0f, 4f, 1f);

        StartingClimateSearchRadiusBlocks = (int)Clamp(StartingClimateSearchRadiusBlocks, 512f, 4_000_000f, 65536f);
        StartingClimateNorthSouthCost = Clamp(StartingClimateNorthSouthCost, 1f, 100f, 2f);

        ClimateMode = (ClimateMode ?? "").Trim().ToLowerInvariant();
        if (ClimateMode is not ("full" or "off")) ClimateMode = "";

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
