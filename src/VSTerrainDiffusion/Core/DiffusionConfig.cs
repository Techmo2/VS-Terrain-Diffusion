using Vintagestory.API.Common;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Machine-level settings, stored in <c>ModConfig/vsterraindiffusion.json</c>. Everything except
/// <see cref="WorldGen"/> describes the hardware the server is running on, not the world itself.
/// </summary>
public class DiffusionConfig
{
    /// <summary>
    /// "auto", "cpu", "openvino", "cuda", "tensorrt-rtx", "directml" or "coreml". OpenVINO and
    /// TensorRT RTX are selected only when requested explicitly.
    /// </summary>
    public string InferenceDevice { get; set; } = "auto";

    /// <summary>"auto", "memory" or "file". Controls where ONNX sessions load model graphs from.</summary>
    public string ModelLoadMode { get; set; } = "auto";

    /// <summary>
    /// Keep only one model resident on the GPU at a time, rebuilding a session whenever another
    /// stage needs the device. That holds peak VRAM near 1.5 GB instead of about 2.5 GB, and it
    /// costs a great deal: generating one terrain tile runs the latent model and the decoder, so
    /// every tile pays for at least one session rebuild of a graph that is most of a gigabyte.
    /// Measured on a 6 GB laptop card, turning this on triples the average tile time (66 ms to
    /// 197 ms). Off by default; turn it on only if the models will not fit alongside everything
    /// else on the card.
    /// </summary>
    public bool OffloadModels { get; set; }

    /// <summary>
    /// Share of the time, as a percentage, that world generation may keep the inference device
    /// busy. 100 is unlimited and is the default.
    ///
    /// This exists for frame stuttering in single player, where the model runs on the same GPU the
    /// game renders with. A graph that is already running cannot be interrupted, so the only lever
    /// is how often one is started: after each one the generator idles for long enough to hold the
    /// device to this share, which leaves the renderer regular windows to get a frame out. It
    /// cannot make an individual model run shorter, so it reduces stutter rather than removing it.
    /// World generation slows down by the reciprocal - at 50% a terrain tile takes about twice as
    /// long - so lower this only as far as the stutter actually requires.
    /// </summary>
    public int GpuUtilizationPercent { get; set; } = 100;

    /// <summary>Verify SHA-256 of pre-existing model files on startup.</summary>
    public bool ValidateModelHashes { get; set; } = true;

    /// <summary>
    /// Download the matching ONNX Runtime and optional OpenVINO native libraries automatically.
    /// Turn off to supply them yourself under <c>TerrainDiffusionModels/onnxruntime/</c>.
    /// </summary>
    public bool DownloadRuntime { get; set; } = true;

    /// <summary>
    /// "fp32", "fp16" or "int8". Both are opt-in because changing decoder precision can alter
    /// newly generated terrain slightly. FP16 needs a GPU provider; INT8 is for CPU and OpenVINO.
    /// </summary>
    public string DecoderPrecision { get; set; } = "fp32";

    /// <summary>
    /// "fp32" or "fp16" for the base (latent) model, which is most of the work in a tile. FP16
    /// needs a GPU provider and is worth the most on TensorRT RTX.
    /// </summary>
    public string BasePrecision { get; set; } = "fp32";

    /// <summary>
    /// "fp32" or "fp16" for the coarse model. Measured on an RTX 3060: FP16 here saves about a
    /// tenth of a tile's time and moves elevation roughly 2 m, because the coarse sampler runs
    /// twenty steps and compounds the difference. FP32 unless you have measured otherwise.
    /// </summary>
    public string CoarsePrecision { get; set; } = "fp32";

    /// <summary>Total megabytes of decoded tensor windows kept across all pipeline stages.</summary>
    public int TileCacheMegabytes { get; set; } = 256;

    /// <summary>Latent windows per base-model call. Zero selects one on CPU and four on GPU.</summary>
    public int LatentBatchSize { get; set; }

    /// <summary>
    /// Megabytes of finished terrain tiles to keep. This has to cover everything world generation
    /// touches at once — a spawn area alone can span a hundred tiles — or tiles get evicted while
    /// still in use and are rebuilt from scratch.
    /// </summary>
    public int TerrainTileCacheMegabytes { get; set; } = 256;

    /// <summary>
    /// Number of chunk columns' worth of terrain generated per model query, in blocks. Larger
    /// values amortise model latency over more chunks at the cost of a longer first-visit stall.
    /// Zero selects 128 on CPU and 256 on GPU. Explicit values must be a multiple of 32.
    /// </summary>
    public int TerrainTileSizeBlocks { get; set; }

    /// <summary>
    /// Log a line at notification level for every terrain tile generated. Very noisy; useful when
    /// profiling. With this off the same lines are still written to the debug log, and only a tile
    /// far out of step with the rest of the session reaches the main one.
    /// </summary>
    public bool VerboseInference { get; set; }

    /// <summary>
    /// Port for the debug map, a small read-only web page showing the model's heightmap and
    /// climate maps as tiles are generated. Zero, the default, does not open a port at all.
    /// </summary>
    public int DebugMapPort { get; set; }

    /// <summary>
    /// Address the debug map listens on. Loopback by default, so only this machine can reach it;
    /// set to <c>0.0.0.0</c> to expose it to the network, which publishes the world's terrain and
    /// climate to anything that can reach the port.
    /// </summary>
    public string DebugMapBindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// How many generated tiles the debug map remembers. Each costs about eight kilobytes, and the
    /// oldest are dropped past this. The provider's own tile cache is no use here: it is sized for
    /// world generation and drops a tile as soon as the generator has moved on.
    /// </summary>
    public int DebugMapHistoryTiles { get; set; } = 2048;

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
        DiffusionConfig config;
        try
        {
            config = api.LoadModConfig<DiffusionConfig>(DiffusionPaths.ModId + ".json");
        }
        catch (System.Exception e)
        {
            // Carrying on with defaults would generate terrain to settings the player never chose,
            // and would not match whatever this world was generated with before.
            throw DiffusionFailure.Fatal(api.Logger,
                $"The mod config ({DiffusionPaths.ModId}.json) could not be read. Fix or delete it.", e);
        }

        config ??= new DiffusionConfig();
        config.Sanitize();
        api.StoreModConfig(config, DiffusionPaths.ModId + ".json");
        _instance = config;
        return config;
    }

    /// <summary>
    /// Records an inference device the mod had to choose for the player, in the file they chose the
    /// original one in.
    ///
    /// The runtime is resolved once per process, so a provider that turns out to be unusable cannot
    /// always be replaced in the session that found out. Writing the working one down means the next
    /// start comes up on it without the player having to read the log and edit the file, and it
    /// keeps the effective provider stable for the world afterwards, which matters because changing
    /// provider moves newly generated terrain slightly.
    /// </summary>
    public static void PersistInferenceDevice(string device, ILogger logger)
    {
        if (string.Equals(Instance.InferenceDevice, device, System.StringComparison.Ordinal)) return;
        Instance.InferenceDevice = device;

        try
        {
            string path = DiffusionPaths.ConfigFile;
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(
                path, Newtonsoft.Json.JsonConvert.SerializeObject(Instance, Newtonsoft.Json.Formatting.Indented));
        }
        catch (System.Exception e)
        {
            // The device in memory is still the corrected one; only the record of it is lost, and
            // the next start will work the same failure out again.
            logger?.Warning("[{0}] Could not write the corrected inference device to {1}: {2}",
                DiffusionPaths.ModId, DiffusionPaths.ConfigFile, e.Message);
        }
    }

    private void Sanitize()
    {
        (WorldGen ??= new WorldGenConfig()).Sanitize();

        // Below about a twentieth the idle windows are longer than the pauses they are meant to
        // prevent, and world generation stops keeping up with a walking player.
        if (GpuUtilizationPercent < 5) GpuUtilizationPercent = 5;
        if (GpuUtilizationPercent > 100) GpuUtilizationPercent = 100;

        if (DebugMapPort != 0 && (DebugMapPort < 1024 || DebugMapPort > 65535)) DebugMapPort = 0;
        DebugMapBindAddress = (DebugMapBindAddress ?? "127.0.0.1").Trim();
        if (DebugMapBindAddress.Length == 0) DebugMapBindAddress = "127.0.0.1";
        if (DebugMapHistoryTiles < 64) DebugMapHistoryTiles = 64;
        if (DebugMapHistoryTiles > 65536) DebugMapHistoryTiles = 65536;

        if (TileCacheMegabytes < 32) TileCacheMegabytes = 32;
        if (TileCacheMegabytes > 4096) TileCacheMegabytes = 4096;

        if (LatentBatchSize < 0) LatentBatchSize = 0;
        if (LatentBatchSize > 16) LatentBatchSize = 16;

        if (TerrainTileCacheMegabytes < 32) TerrainTileCacheMegabytes = 32;
        if (TerrainTileCacheMegabytes > 4096) TerrainTileCacheMegabytes = 4096;

        if (TerrainTileSizeBlocks != 0)
        {
            if (TerrainTileSizeBlocks < 64) TerrainTileSizeBlocks = 64;
            if (TerrainTileSizeBlocks > 1024) TerrainTileSizeBlocks = 1024;
            TerrainTileSizeBlocks -= TerrainTileSizeBlocks % 32;
        }

        InferenceDevice = (InferenceDevice ?? "auto").Trim().ToLowerInvariant();
        switch (InferenceDevice)
        {
            case "auto":
            case "cpu":
            case "cuda":
            case "directml":
            case "dml":
            case "coreml":
            case "openvino":
            case "tensorrt-rtx":
            case "gpu":
                break;
            case "trt-rtx":
            case "tensorrtrtx":
            case "rtx":
                InferenceDevice = "tensorrt-rtx";
                break;
            default:
                InferenceDevice = "auto";
                break;
        }

        ModelLoadMode = (ModelLoadMode ?? "auto").Trim().ToLowerInvariant();
        switch (ModelLoadMode)
        {
            case "auto":
            case "memory":
            case "file":
                break;
            default:
                ModelLoadMode = "auto";
                break;
        }

        DecoderPrecision = (DecoderPrecision ?? "fp32").Trim().ToLowerInvariant();
        // Earlier development builds wrote "auto", which coupled OpenVINO to INT8. Treat it as
        // the safe FP32 default when those configs are upgraded.
        if (DecoderPrecision == "auto") DecoderPrecision = "fp32";
        if (DecoderPrecision is not ("fp32" or "fp16" or "int8")) DecoderPrecision = "fp32";

        BasePrecision = (BasePrecision ?? "fp32").Trim().ToLowerInvariant();
        if (BasePrecision is not ("fp32" or "fp16")) BasePrecision = "fp32";

        CoarsePrecision = (CoarsePrecision ?? "fp32").Trim().ToLowerInvariant();
        if (CoarsePrecision is not ("fp32" or "fp16")) CoarsePrecision = "fp32";
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
    /// Added to the final rainfall as a fraction of full scale, so the model's own answer stands
    /// unless this says otherwise.
    ///
    /// The climate map cancels Vintage Story's own "higher ground is wetter" bonus, because the
    /// model already models orography properly, and this used to default to 0.05 to put that
    /// bonus's average back - vanilla's thresholds were tuned with it present. In practice it read
    /// as a world that was too lush everywhere, so the compensation is now opt-in: raise it for a
    /// wetter world, lower it for a drier one.
    ///
    /// It is an *additive* trim, which is what makes it the right knob for "a bit too wet": it
    /// moves every column by the same amount. <see cref="MoistureMedian"/> shifts a log-normal and
    /// so bites hardest in places that are already dry, which turns arid regions into desert.
    /// </summary>
    public float RainfallBias { get; set; }

    /// <summary>Degrees Celsius added to every model temperature, for a warmer or colder world.</summary>
    public float TemperatureOffsetC { get; set; }

    /// <summary>
    /// Scales the forest cover the model's moisture implies. Vintage Story's own forest map is
    /// noise with no climate signal at all, so this replaces it outright; raise for denser woods.
    ///
    /// Trees on the ground go as the <em>square</em> of this. What the mod writes is a 0-255 forest
    /// byte; vanilla draws candidate tree positions from the climate and accepts each one with
    /// probability <c>(byte / 255)^2</c>, so 1.4 is about twice the trees rather than four tenths
    /// more. The byte also saturates, which is why much above 1.2 only flattens the wet end, and
    /// why the setting bites hardest where cover was low to begin with.
    /// </summary>
    public float ForestDensityMultiplier { get; set; } = 1f;

    /// <summary>Scales shrub cover the same way, with the same squaring.</summary>
    public float ShrubDensityMultiplier { get; set; } = 1f;

    /// <summary>
    /// Swing temperature through the year using the model's temperature seasonality (BIO4) rather
    /// than from latitude alone, which is all vanilla has to go on. Continental interiors then get
    /// hard winters and hot summers while maritime and tropical climates at the same latitude stay
    /// even. Latitude still shows through, because a polar climate is a strongly seasonal one.
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
    /// Least share of a place's annual rainfall the dry season is allowed to fall to.
    ///
    /// A safety rail rather than a tuning knob: at the default strength it only binds above a
    /// coefficient of variation of roughly 100%. There the driest weeks would otherwise see no rain
    /// at all, and Vintage Story's farmland integrates precipitation into a moisture level that
    /// stalls crops below 10%, so an absolute drought parks every unirrigated field for months.
    /// Zero restores the undamped swing.
    /// </summary>
    public float SeasonalPrecipitationFloor { get; set; } = 0.4f;

    /// <summary>
    /// How long a translocator may wait for a chunk peek that never came back before starting its
    /// search again, in seconds. 0 disables the watchdog. Held to at least
    /// <see cref="TranslocatorPeekPauseSeconds"/> plus a minute, so it cannot fire while a peek is
    /// still waiting its turn, and each restart waits longer than the last.
    ///
    /// The server drops a queued peek if it cannot pause every worldgen thread within 3.6 seconds,
    /// and a worldgen thread waiting on the model can take longer than that. The translocator has
    /// already cleared the flag that would make it try again, so without this it stays on "Warping
    /// spacetime..." until its chunk is unloaded and read back from disk.
    /// </summary>
    public int TranslocatorSearchTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// How far the land is lowered where sneeze's Rivers runs a river, from 0 (leave the model's
    /// own landscape alone) to 1. Does nothing without that mod.
    ///
    /// The rivers are routed before the terrain is generated - they come from the world's ocean
    /// map, not from its height - so the model can be told where they will be and put a valley
    /// there rather than a ridge for one to be cut through afterwards.
    ///
    /// Measured at the default conditioning strength, a corridor against ~505 m either side:
    /// 0.3 brings it to 334 m (a 28% drop), 0.5 to 251 m (45%), and 1.0 to 28 m (94%) but drowns a
    /// fifth of the corridor, which turns rivers into sea inlets.
    ///
    /// The default is 0.3 rather than the 0.5 first shipped because the basin reaches a full coarse
    /// cell either side of a river. At Rivers' own default density that sweeps roughly a third of
    /// the world, and pressing all of it halfway to sea level reads as a plain rather than as river
    /// valleys. Raise it for deeper valleys, or lower <c>riverSpawnChance</c> in Rivers' own config
    /// for fewer of them.
    /// </summary>
    public float RiverBasinDepth { get; set; } = 0.3f;

    /// <summary>
    /// How long the server may wait for world generation to pause before a chunk peek, in seconds.
    /// 0 keeps Vintage Story's own 3.6.
    ///
    /// Worldgen threads only park between chunk columns, and a column here waits on the model
    /// behind a single inference gate, so several queued threads routinely take longer than 3.6 s
    /// to come to rest. The server then throws the peek away - after the terrain was generated -
    /// and the translocator has to start over. Nothing can cut a column short, so the only
    /// remaining option is to wait for it.
    /// </summary>
    public int TranslocatorPeekPauseSeconds { get; set; } = 30;

    /// <summary>
    /// Caps how far a translocator looks for its partner, in blocks. 0 keeps Vintage Story's own
    /// 8000.
    ///
    /// Each attempt generates nine chunk columns and throws them away, four times a second, until
    /// it finds a ruin. A smaller ring keeps those attempts inside terrain that is already
    /// generated and still cached. It also makes translocator hops shorter, and it changes which
    /// translocator links to which, which is why it is off by default. Links already made are kept.
    /// </summary>
    public int TranslocatorMaxRangeBlocks { get; set; }

    /// <summary>
    /// Swing the year the opposite way south of the equator.
    ///
    /// On by default, and not really optional: Vintage Story already does this. Its calendar takes
    /// the hemisphere from the sign of the same latitude this mod reads, and
    /// <c>GetSeasonRel</c> shifts the year half a turn for the southern one - which is what decides
    /// foliage, crop growth and everything else that asks the calendar what season it is. Leaving
    /// this off does not give the world one hemisphere; it gives it a southern hemisphere whose
    /// leaves fall in the spring, because only the temperature curve stayed northern.
    ///
    /// There is no world it is right to turn off. The game's own hemisphere does not depend on this
    /// mod's latitude bands, so even at <see cref="LatitudeStrength"/> 0 the calendar still flips
    /// and this should follow it; on a "Patchy" world the game reports one hemisphere everywhere
    /// and the setting does nothing either way. It stays a switch only because someone may prefer
    /// one long season to a world that is half out of step with their own.
    /// </summary>
    public bool SeasonHemispheres { get; set; } = true;

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
    /// Which way the world's ocean map and the model's terrain are made to agree.
    ///
    /// "input" conditions the model on the ocean map, so the world's "Land cover" and "Ocean scale"
    /// settings - and any ocean map another mod installs in their place - decide where the sea is,
    /// and the map itself is left untouched for everything else that reads it.
    ///
    /// "output" is the reverse: the model invents its own continents and the ocean map is rewritten
    /// to match them. That gives coastlines drawn entirely from real-world terrain, at the price of
    /// ignoring the world's settings and overwriting any other mod's ocean map.
    /// </summary>
    public string OceanMap { get; set; } = "input";

    /// <summary>
    /// input: how completely the ocean map overrides the model's own sense of where land belongs,
    /// from 0 (ignored) to 1. Below 1 the map biases the coastline rather than setting it, which
    /// keeps more of the model's structure at the cost of honouring the world settings less exactly.
    /// </summary>
    public float LandmaskStrength { get; set; } = 1f;

    /// <summary>
    /// input: how much noise the model is told the landmask carries, which is how it decides
    /// whether the mask is a hint or an instruction. <em>Lower binds it more tightly</em> — the
    /// value is mixed as <c>cos(atan(n))</c> conditioning against <c>sin(atan(n))</c> noise, so it
    /// runs the opposite way to its name in the model's own config, where it is called
    /// <c>cond_snr</c>.
    ///
    /// The model ships 0.5, at which it reproduces the ocean map over about 88% of the world;
    /// 0.1 gets that to 95% and is the default here. Below that the gain is under 2% and the
    /// conditioning starts flattening the land it does keep. Zero uses the model's own value.
    /// </summary>
    public float LandmaskNoiseLevel { get; set; } = 0.1f;

    /// <summary>
    /// How much of the world's "Global temperature" and "Global precipitation" settings is built
    /// into the climate the model is conditioned on, from 0 to 1. The rest is applied to the
    /// model's output afterwards, so the world reads the same either way; what changes is whether
    /// the model knew. At 1 an arid world is drawn as an arid world, with the drainage, vegetation
    /// and soils to match; at 0 it is a temperate world with its rainfall scaled down on the way
    /// out, which is what this mod used to do and what vanilla does.
    /// </summary>
    public float GlobalClimateStrength { get; set; } = 1f;

    /// <summary>
    /// How much noise the model is told the shifted climate carries, on the same inverted scale as
    /// <see cref="LandmaskNoiseLevel"/>: <em>lower binds it more tightly</em>. Only consulted when
    /// one of the two settings is off its default, so an ordinary world keeps the model's own
    /// climate character. Zero uses the model's value for every world.
    /// </summary>
    public float ClimateNoiseLevel { get; set; }

    /// <summary>
    /// How much of a north-south climate gradient the world gets, from 0 to 1.
    ///
    /// The model's climate is a real climatology with continents, maritime coasts, rain shadows and
    /// altitude in it, but nothing in it knows which way is north: left alone it puts the cold
    /// places wherever its noise put them, and the world's <c>polarEquatorDistance</c> means
    /// nothing. At 1 the equator, the subtropical deserts, the mid-latitude storm track and the ice
    /// caps are conditioned into the model at the latitudes the game says they belong, and
    /// everything the model knows about coasts and mountains happens <em>within</em> those bands.
    /// At 0 the world is unrooted, which is what the mod did before 0.5.
    ///
    /// Which block is at which latitude is the game's answer, not this mod's, so day length,
    /// midnight sun and the hemispheres all agree with the snow line for free.
    /// </summary>
    public float LatitudeStrength { get; set; } = 1f;

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
        RainfallBias = Clamp(RainfallBias, -1f, 1f, 0f);
        TemperatureOffsetC = Clamp(TemperatureOffsetC, -40f, 40f, 0f);

        ForestDensityMultiplier = Clamp(ForestDensityMultiplier, 0f, 4f, 1f);
        ShrubDensityMultiplier = Clamp(ShrubDensityMultiplier, 0f, 4f, 1f);

        SeasonalTemperatureStrength = Clamp(SeasonalTemperatureStrength, 0f, 4f, 1f);
        SeasonalPrecipitationStrength = Clamp(SeasonalPrecipitationStrength, 0f, 4f, 1f);
        SeasonalPrecipitationFloor = Clamp(SeasonalPrecipitationFloor, 0f, 1f, 0.4f);
        TranslocatorSearchTimeoutSeconds = (int)Clamp(TranslocatorSearchTimeoutSeconds, 0f, 1800f, 120f);
        TranslocatorPeekPauseSeconds = (int)Clamp(TranslocatorPeekPauseSeconds, 0f, 120f, 30f);
        RiverBasinDepth = Clamp(RiverBasinDepth, 0f, 1f, 0.3f);
        TranslocatorMaxRangeBlocks = (int)Clamp(TranslocatorMaxRangeBlocks, 0f, 8000f, 0f);

        OceanMap = (OceanMap ?? "input").Trim().ToLowerInvariant();
        if (OceanMap != "output") OceanMap = "input";
        LandmaskStrength = Clamp(LandmaskStrength, 0f, 1f, 1f);
        if (LandmaskNoiseLevel != 0f) LandmaskNoiseLevel = Clamp(LandmaskNoiseLevel, 0.01f, 8f, 0.1f);
        GlobalClimateStrength = Clamp(GlobalClimateStrength, 0f, 1f, 1f);
        LatitudeStrength = Clamp(LatitudeStrength, 0f, 1f, 1f);
        if (ClimateNoiseLevel != 0f) ClimateNoiseLevel = Clamp(ClimateNoiseLevel, 0.01f, 8f, 0f);

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
