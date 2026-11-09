using System;
using System.Collections.Generic;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;
using VSTerrainDiffusion.Tensors;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Port of terrain_diffusion/inference/world_pipeline.py.
///
/// Three stages feed each other through <see cref="InfiniteTensor"/> caches:
/// a coarse map (20-step DPM-Solver++), a latent map (2 flow-matching steps) and a decoder
/// (1 flow-matching step). All pixel coordinates are in native model resolution
/// (<see cref="WorldPipelineModelConfig.NativeResolution"/> metres per pixel).
/// </summary>
public sealed partial class WorldPipeline
{
    /// <summary>Edge of a coarse window, in coarse pixels.</summary>
    internal const int CoarseTileSize = 64;

    /// <summary>Step between coarse windows; they overlap by a quarter and are blended.</summary>
    private const int CoarseTileStride = 48;

    /// <summary>Edge of a latent window, in latent pixels.</summary>
    internal const int LatentTileSize = 64;

    /// <summary>Step between latent windows; they overlap by half.</summary>
    private const int LatentTileStride = 32;

    /// <summary>Edge of a decoder window, in native pixels.</summary>
    internal const int DecoderTileSize = 256;

    /// <summary>Step between decoder windows; they overlap by a quarter.</summary>
    private const int DecoderTileStride = 192;

    /// <summary>The EDM data standard deviation the models were trained with.</summary>
    private const float SigmaData = EdmScheduler.SigmaData;

    /// <summary>
    /// Means of the base model's seven conditioning inputs (elevation, p5, the climate channels, the land
    /// mask), for normalising them.
    /// </summary>
    private static readonly float[] CondMeans = { 14.99f, 11.65f, 15.87f, 619.26f, 833.12f, 69.40f, 0.66f };

    /// <summary>Standard deviations matching <see cref="CondMeans"/>.</summary>
    private static readonly float[] CondStds = { 21.72f, 21.78f, 10.40f, 452.29f, 738.09f, 34.59f, 0.47f };

    /// <summary>Mean of the latents' low-frequency elevation channel, for denormalising it.</summary>
    private const float LowFreqMean = -31.4f;

    /// <summary>Standard deviation of the low-frequency channel.</summary>
    private const float LowFreqStd = 38.6f;

    /// <summary>mp_concat component widths: means, p5, climate, mask, histogram, noise level.</summary>
    private static readonly int[] CondDims = { 16, 16, 4, 16, 5, 1 };

    /// <summary>The model's constants, from <c>world_pipeline_config.json</c>.</summary>
    private readonly WorldPipelineModelConfig _config;

    /// <summary>The coarse model's channels; the coarse map adds the blend weight after them, at this index.</summary>
    private readonly CoarseModelSpec _coarseSpec;

    /// <summary><see cref="_coarseSpec"/>'s channel count.</summary>
    private readonly int _coarseChannels;

    /// <summary>Native pixels per latent pixel along each axis.</summary>
    private readonly int _latentCompression;

    /// <summary>Means of the coarse model's channels, for normalising them.</summary>
    private readonly float[] _modelMeans;

    /// <summary>Standard deviations of the coarse model's channels.</summary>
    private readonly float[] _modelStds;

    /// <summary>
    /// How loosely each conditioning channel binds the coarse model (smaller binds tighter); a copy of the
    /// config's, adjusted for this world.
    /// </summary>
    private readonly float[] _condSnr;

    /// <summary><see cref="_condSnr"/> as the model takes it: log(snr / 8).</summary>
    private readonly float[] _condVals;

    /// <summary>
    /// Scale of each conditioning component in the base model's magnitude-preserving concatenation.
    /// </summary>
    private readonly float[] _mpConcatScales;

    /// <summary>The base model's terrain-type histogram input.</summary>
    private readonly float[] _histogramRaw;

    /// <summary>Mean of the decoder's residual, for denormalising it.</summary>
    private readonly float _residualMean;

    /// <summary>Standard deviation of the decoder's residual.</summary>
    private readonly float _residualStd;

    /// <summary>The coarse model.</summary>
    private readonly IModelRunner _coarseModel;

    /// <summary>The base (latent) model.</summary>
    private readonly IModelRunner _baseModel;

    /// <summary>The decoder.</summary>
    private readonly IModelRunner _decoderModel;

    /// <summary>The cache every stage's windows are kept in.</summary>
    private readonly MemoryTileStore _tileStore;

    /// <summary>Latent windows per base-model call.</summary>
    private readonly int _latentBatchSize;

    /// <summary>
    /// How the coarse map is pooled; <see cref="CoarsePooling.None"/> unless the world asked.
    /// </summary>
    private readonly CoarsePooling _pooling;

    /// <summary>The second latent step's re-noise angle; see <see cref="DiffusionWorldSettings.BaseRenoiseSigma"/>.</summary>
    private readonly float _renoiseT;

    /// <summary>Noise added to the coarse elevation; see <see cref="DiffusionWorldSettings.CoarseHeightNoise"/>.</summary>
    private readonly float _coarseHeightNoise;

    /// <summary>Extra cooling with height, in reference lapse rates; see <see cref="DiffusionWorldSettings.AltitudeCooling"/>.</summary>
    private readonly float _altitudeCooling;

    /// <summary>
    /// Whether the synthetic map fills sea cells next to land with the land's climate; see
    /// <see cref="CoastFill"/>.
    /// </summary>
    private readonly bool _coastFill;

    /// <summary>Square-root metres of elevation over which <see cref="_coarseHeightNoise"/> fades in from sea level.</summary>
    private const float CoarseNoiseShoreRamp = 10f;



    /// <summary>The coarse map.</summary>
    private readonly InfiniteTensor _coarse;

    /// <summary>The final latent map.</summary>
    private readonly InfiniteTensor _latents;

    /// <summary>The decoder's residual map.</summary>
    private readonly InfiniteTensor _residual;

    /// <summary>Where the sea goes, or null to let the model decide.</summary>
    private readonly ILandmaskSource _landmask;

    /// <summary>River basins to carve into the coarse conditioning, or null.</summary>
    private readonly IRiverBasinSource _riverBasins;

    /// <summary>How deep the basins are carved, metres.</summary>
    private readonly float _riverBasinDepth;

    /// <summary>How hard the land mask binds; zero without one.</summary>
    private readonly float _landmaskStrength;

    /// <summary>The world's climate bias.</summary>
    private readonly ClimateShift _climate;

    /// <summary>The world's latitude bands, or null for none.</summary>
    private readonly ILatitudeSource _latitude;

    /// <summary>Places that ask the coarse model for a climate or landform of their own; see <see cref="CoarseConditioningSite"/>.</summary>
    private readonly IReadOnlyList<CoarseConditioningSite> _sites;

    /// <summary>Builds the coarse model's conditioning map; rebuilt when the seed changes.</summary>
    private SyntheticMapFactory _syntheticMapFactory;

    /// <summary>The world seed.</summary>
    private ulong _seed;

    /// <param name="landmask">
    /// Decides where the sea goes, overriding the pipeline's own noise. Null lets the model invent
    /// its own continents, which is the reference implementation's behaviour.
    /// </param>
    /// <param name="climate">
    /// Biases the climate the model is conditioned on, for a world the player asked to be hotter,
    /// colder, wetter or drier than the one the model would draw by itself.
    /// </param>
    /// <param name="latitude">
    /// Puts that climate on a north-south axis, so the tropics and the ice land where the world's
    /// polar distance says they should. Null leaves the climate unrooted, which is the reference
    /// implementation's behaviour.
    /// </param>
    public WorldPipeline(ulong seed, PipelineModels models, ILandmaskSource landmask = null,
                         ClimateShift climate = default, ILatitudeSource latitude = null,
                         IRiverBasinSource riverBasins = null, float riverBasinDepth = 0f,
                         CoarsePooling pooling = default, float baseRenoiseSigma = 0.35f,
                         float coarseHeightNoise = 0f, float altitudeCooling = 1f,
                         IReadOnlyList<CoarseConditioningSite> conditioningSites = null, bool coastFill = false)
    {
        _coastFill = coastFill;
        _sites = conditioningSites;
        _pooling = pooling.Factor >= 1 ? pooling : CoarsePooling.None;
        _riverBasins = riverBasins;
        _riverBasinDepth = riverBasinDepth;

        _seed = seed;
        _config = WorldPipelineModelConfig.Instance;

        // Pooling is the world's choice (CoarsePooling), not the model's; a model config asking for
        // it would be pooled twice.
        if (_config.CoarsePooling != 1)
        {
            throw new NotSupportedException(
                "coarse_pooling=" + _config.CoarsePooling + " in the model config is not supported by this pipeline");
        }

        WorldGenConfig worldGen = DiffusionConfig.Instance.WorldGen;
        _landmask = landmask;
        _landmaskStrength = landmask != null ? worldGen.LandmaskStrength : 0f;
        _climate = climate;
        _latitude = latitude != null && !latitude.IsNeutral ? latitude : null;

        _latentCompression = _config.LatentCompression;
        _coarseSpec = models.CoarseSpec ?? CoarseModelSpec.Annual;
        _coarseChannels = _coarseSpec.Channels;
        _modelMeans = _coarseSpec.Means(_config);
        _modelStds = _coarseSpec.Stds(_config);
        if (_modelMeans.Length < _coarseChannels || _modelStds.Length < _coarseChannels)
        {
            throw new ArgumentException(
                $"The {_coarseSpec.Code} coarse model has {_coarseChannels} channels but statistics for {_modelMeans.Length}");
        }
        _residualMean = _config.ResidualMean;
        _residualStd = _config.ResidualStd;
        _histogramRaw = _config.HistogramRaw ?? new float[5];

        // A landmask is only worth supplying if the model is told to take it seriously. cond_snr is
        // a first-class model input deciding how far each channel's conditioning binds, so the
        // elevation channel is turned down (see the mixing in CoarseTile: despite the name, smaller
        // means more conditioning). Copied rather than edited in place, because the config array is
        // shared.
        _condSnr = (float[])_config.CondSnr.Clone();
        if (_landmaskStrength > 0f && worldGen.LandmaskNoiseLevel > 0f)
        {
            _condSnr[0] = worldGen.LandmaskNoiseLevel;
        }

        // Likewise for the two climate channels, but only for a world that asked for a climate of
        // its own - a global setting off its default, or latitude bands: with nothing to bind them
        // to, binding them tighter would only trade the model's own sense of where weather comes
        // from for the synthetic map's.
        bool steeredClimate = _latitude != null
                              || (!_climate.IsNeutral && worldGen.GlobalClimateStrength > 0f);
        if (steeredClimate && worldGen.ClimateNoiseLevel > 0f)
        {
            _condSnr[1] = worldGen.ClimateNoiseLevel;
            _condSnr[3] = worldGen.ClimateNoiseLevel;
        }

        _condVals = new float[_condSnr.Length];
        for (int i = 0; i < _condSnr.Length; i++) _condVals[i] = (float)Math.Log(_condSnr[i] / 8.0);

        _mpConcatScales = BuildMpConcatScales();

        _renoiseT = (float)Math.Atan(baseRenoiseSigma / SigmaData);
        _coarseHeightNoise = coarseHeightNoise;
        _altitudeCooling = altitudeCooling;

        _coarseModel = models.Coarse;
        _baseModel = models.Base;
        _decoderModel = models.Decoder;

        _syntheticMapFactory = new SyntheticMapFactory(seed, _landmask, _landmaskStrength, _climate, _latitude,
                                                      _riverBasins, _riverBasinDepth,
                                                      SiteBasins);
        long cacheLimitBytes = Math.Max(32L, DiffusionConfig.Instance.TileCacheMegabytes) * 1024 * 1024;
        _tileStore = new MemoryTileStore(cacheLimitBytes);
        _latentBatchSize = ResolveLatentBatchSize();

        _coarse = BuildCoarseStage();
        _latents = BuildLatentStage();
        _residual = BuildDecoderStage();
    }

    /// <summary>
    /// Latent windows per base-model call. Also decides the widest batch a TensorRT RTX engine has
    /// to be built for, so it lives here rather than inline in the constructor.
    /// </summary>
    internal static int ResolveLatentBatchSize()
    {
        int configured = DiffusionConfig.Instance.LatentBatchSize;
        return configured > 0
            ? configured
            : OnnxModel.ActiveProvider is InferenceProvider.Cpu or InferenceProvider.OpenVino ? 1 : 4;
    }

    /// <summary>The world seed.</summary>
    public ulong Seed => _seed;

    /// <summary>The coarse model this pipeline runs, and so which channels its coarse map holds.</summary>
    public CoarseModelSpec CoarseSpec => _coarseSpec;

    /// <summary>Metres per native pixel.</summary>
    public float NativeResolution => _config.NativeResolution;

    /// <summary>Windows computed by every stage so far.</summary>
    public long TotalComputedWindowCount => _tileStore.TotalComputedWindowCount;

    /// <summary>Bytes of windows held in the cache.</summary>
    public long CachedBytes => _tileStore.CachedBytes;

    /// <summary>Latent windows per base-model call.</summary>
    public int LatentBatchSize => _latentBatchSize;

    /// <summary>Calls, items and time per model, for the log and <c>/tdiff status</c>.</summary>
    public string ModelTimingSummary =>
        $"coarse [{_coarseModel.Backend}] {_coarseModel.RunCount} calls/{_coarseModel.RunItems} items/{_coarseModel.RunMilliseconds} ms, " +
        $"base [{_baseModel.Backend}] {_baseModel.RunCount} calls/{_baseModel.RunItems} items/{_baseModel.RunMilliseconds} ms, " +
        $"decoder [{_decoderModel.Backend}] {_decoderModel.RunCount} calls/{_decoderModel.RunItems} items/{_decoderModel.RunMilliseconds} ms";

    /// <summary>Time spent in the three models so far.</summary>
    public long TotalModelInferenceMilliseconds =>
        _coarseModel.RunMilliseconds + _baseModel.RunMilliseconds + _decoderModel.RunMilliseconds;

    /// <summary>Drops every cached window, for when the world closes.</summary>
    public void ClearCaches() => _tileStore.ClearAllCaches();

    /// <summary>Switches to a different world seed and drops every cached window.</summary>
    public void SetSeed(ulong newSeed)
    {
        if (newSeed == _seed) return;
        _seed = newSeed;
        _syntheticMapFactory = new SyntheticMapFactory(newSeed, _landmask, _landmaskStrength, _climate, _latitude,
                                                      _riverBasins, _riverBasinDepth,
                                                      SiteBasins);
        _tileStore.ClearAllCaches();
    }

    /// <summary>
    /// The scales of the magnitude-preserving concatenation: each of the k components is scaled by sqrt(N k)
    /// / sqrt(n) / k, where n is its width and N the total.
    /// </summary>
    /// <returns>One scale per <see cref="CondDims"/> entry.</returns>
    private static float[] BuildMpConcatScales()
    {
        int sumN = 0;
        foreach (int n in CondDims) sumN += n;
        int k = CondDims.Length;
        float c = (float)Math.Sqrt((double)sumN * k);
        var scales = new float[k];
        for (int i = 0; i < k; i++) scales[i] = c / (float)Math.Sqrt(CondDims[i]) / k;
        return scales;
    }
}
