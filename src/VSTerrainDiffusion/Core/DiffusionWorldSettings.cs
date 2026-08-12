using System;
using System.Globalization;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Per-world settings plus the metre-to-block mapping that follows from them.
///
/// The model works in real-world units: one native pixel is 30 m across and elevations run from
/// roughly -10 000 m to +9 000 m.
///
/// By default a block is exactly as tall as it is wide, matching the Terrain Diffusion Minecraft
/// mod: the landscape is at true scale in every direction, so a 2 000 m massif really is 2 000 m
/// of climbing and slopes have the grade they would have in the real world. That needs a world with
/// the height to hold it, which is why the mod asks for a tall world rather than squashing terrain
/// into a short one. Where that is not an option, <c>heightMode: "auto"</c> measures the region's
/// peaks and stretches the terrain to fill whatever height there is instead.
/// </summary>
public sealed class DiffusionWorldSettings
{
    /// <summary>The tallest elevation the model is expected to produce, in metres.</summary>
    private const float ModelMaxElevationMeters = 10000f;

    /// <summary>Deepest ocean the compression curve is calibrated against, in metres.</summary>
    private const float ModelMaxDepthMeters = 10000f;

    public bool Enabled { get; private set; }

    /// <summary>How much of the climate the model drives; see <see cref="WorldGen.DiffusionClimateMode"/>.</summary>
    public WorldGen.DiffusionClimateMode ClimateMode { get; private set; }

    /// <summary>1..16; the model's 30 m pixels are subdivided this many times.</summary>
    public int Scale { get; private set; } = 2;

    /// <summary>
    /// The player-facing height multiplier. In auto mode this multiplies the calibrated height, so
    /// 1 means "whatever fills the world"; in manual mode it multiplies true real-world scale.
    /// </summary>
    public float VerticalExaggeration { get; private set; } = 1f;

    /// <summary>Metres of real-world distance per block, horizontally.</summary>
    public float MetersPerBlock => NativeResolution / Scale;

    /// <summary>Metres per model pixel, straight from the model config.</summary>
    public float NativeResolution { get; private set; } = 30f;

    public int SeaLevel { get; private set; }
    public int MapSizeY { get; private set; }

    /// <summary>
    /// World block coordinates that the model's own origin maps to. Vintage Story worlds are
    /// centred on (MapSizeX/2, MapSizeZ/2), so anchoring the model there means a given seed
    /// produces the same landscape around spawn no matter how large the world is.
    /// </summary>
    public int OriginBlockX { get; private set; }

    public int OriginBlockZ { get; private set; }

    /// <summary>Blocks available between sea level and the world ceiling.</summary>
    public int HeadroomBlocks { get; private set; }

    /// <summary>Elevation, in metres, below which the mapping is perfectly linear.</summary>
    public float LinearRangeMeters { get; private set; }

    /// <summary>True when the vertical gain came from measuring this world's terrain.</summary>
    public bool IsCalibrated { get; private set; }

    /// <summary>The measured peak elevation the gain was fitted to, in metres. Zero if uncalibrated.</summary>
    public float CalibratedPeakMeters { get; private set; }

    /// <summary>
    /// True when calibration wanted more vertical gain than <c>maxAutoExaggeration</c> allows, so
    /// the region's peaks will fall short of the ceiling.
    /// </summary>
    public bool CalibrationClamped { get; private set; }

    /// <summary>Total height multiplier relative to true real-world scale.</summary>
    public float EffectiveExaggeration => _blocksPerMeter * MetersPerBlock;

    /// <summary>Metres of elevation per block of height, the inverse of the vertical gain.</summary>
    public float MetersPerBlockVertical => _blocksPerMeter > 0f ? 1f / _blocksPerMeter : 0f;

    /// <summary>Multiplies the Perlin roughness added to sloped ground.</summary>
    public float SlopeDetailStrength { get; private set; } = 1f;

    private WorldGenConfig _shaping = new();

    /// <summary>Vertical gain before <see cref="VerticalExaggeration"/> is applied, in blocks per metre.</summary>
    private float _baseBlocksPerMeter;

    private float _kneeBlocks;
    private float _blocksPerMeter;
    private float _oceanScale;

    public static DiffusionWorldSettings FromWorld(ICoreServerAPI api, float nativeResolution)
    {
        ITreeAttribute worldConfig = api.WorldManager.SaveGame.WorldConfiguration;
        WorldGenConfig shaping = DiffusionConfig.Instance.WorldGen;

        int scale = shaping.ScaleOverride != 0
            ? shaping.ScaleOverride
            : GameMathClamp(ReadWorldConfig(worldConfig, "diffusionScale", "2").ToInt(2), 1, 16);

        float exaggeration = shaping.VerticalExaggerationOverride != 0f
            ? shaping.VerticalExaggerationOverride
            : Math.Clamp(ReadWorldConfig(worldConfig, "diffusionVerticalExaggeration", "1").ToFloat(1f), 0.05f, 20f);

        var settings = new DiffusionWorldSettings
        {
            _shaping = shaping,
            NativeResolution = nativeResolution,
            Enabled = ReadWorldConfig(worldConfig, "diffusionTerrain", "true").ToBool(true),
            ClimateMode = ParseClimateMode(shaping.ClimateMode.Length > 0
                ? shaping.ClimateMode
                : ReadWorldConfig(worldConfig, "diffusionClimate", "full")),
            Scale = scale,
            VerticalExaggeration = exaggeration,
            SlopeDetailStrength = shaping.SlopeDetailStrength,
            MapSizeY = api.WorldManager.MapSizeY,
            SeaLevel = api.World.SeaLevel,
            OriginBlockX = RoundToChunk(api.WorldManager.MapSizeX / 2),
            OriginBlockZ = RoundToChunk(api.WorldManager.MapSizeZ / 2)
        };

        settings.RecomputeMapping();
        return settings;
    }

    /// <summary>
    /// Builds settings without a running world, for the offline tools. The model origin sits at
    /// block (0, 0) so tool coordinates are model coordinates.
    /// </summary>
    public static DiffusionWorldSettings ForOfflineUse(float nativeResolution, int scale, int mapSizeY, int seaLevel)
    {
        var settings = new DiffusionWorldSettings
        {
            _shaping = DiffusionConfig.Instance.WorldGen,
            NativeResolution = nativeResolution,
            Enabled = true,
            ClimateMode = WorldGen.DiffusionClimateMode.Full,
            Scale = scale,
            VerticalExaggeration = 1f,
            SlopeDetailStrength = DiffusionConfig.Instance.WorldGen.SlopeDetailStrength,
            MapSizeY = mapSizeY,
            SeaLevel = seaLevel
        };

        settings.RecomputeMapping();
        return settings;
    }

    /// <summary>
    /// Reads a world config value as text whatever attribute type it ended up as. The world
    /// creation screen writes these as strings, but a value set in serverconfig.json arrives
    /// typed, and <c>ITreeAttribute.GetString</c> silently returns the fallback for those.
    /// </summary>
    private static string ReadWorldConfig(ITreeAttribute config, string code, string fallback)
    {
        object value = config?[code]?.GetValue();
        string text = value switch
        {
            null => null,
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static int GameMathClamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>Keeps the model origin on a chunk boundary so tile and chunk grids stay aligned.</summary>
    private static int RoundToChunk(int blocks) => blocks / 32 * 32;

    private static WorldGen.DiffusionClimateMode ParseClimateMode(string value) => value switch
    {
        "off" or "false" or "vanilla" => WorldGen.DiffusionClimateMode.Off,
        _ => WorldGen.DiffusionClimateMode.Full
    };

    /// <summary>True when the world wants its terrain height measured before generating anything.</summary>
    public bool WantsCalibration => _shaping.HeightMode == "auto" && !IsCalibrated;

    /// <summary>
    /// True when a block is exactly as tall as it is wide — the Minecraft mod's geometry, and the
    /// case in which vanilla's altitude-keyed surface rules already line up with the terrain.
    /// </summary>
    public bool IsIsotropic => Math.Abs(EffectiveExaggeration - 1f) < 0.001f;

    /// <summary>Half-width in blocks of the area calibration should survey.</summary>
    public int CalibrationRadiusBlocks => _shaping.CalibrationRadiusBlocks;

    public int CalibrationProbes => _shaping.CalibrationProbes;

    public float PeakQuantile => _shaping.PeakQuantile;

    public float ReliefFactor => _shaping.ReliefFactor;

    /// <summary>
    /// Fits the vertical gain so that terrain <paramref name="peakMeters"/> tall reaches
    /// <c>targetPeakFillFraction</c> of the way from sea level to the world ceiling. Everything
    /// derived from the mapping is recomputed, so this must run before the first tile is built.
    /// </summary>
    public void ApplyCalibration(float peakMeters)
    {
        if (peakMeters <= 1f || float.IsNaN(peakMeters) || float.IsInfinity(peakMeters)) return;

        CalibratedPeakMeters = peakMeters;
        IsCalibrated = true;
        RecomputeMapping();
    }

    private void RecomputeMapping()
    {
        // Two blocks of safety below the world ceiling: the topmost layer must stay air.
        HeadroomBlocks = Math.Max(8, MapSizeY - 3 - SeaLevel);

        float trueScaleBlocksPerMeter = 1f / MetersPerBlock;

        if (IsCalibrated)
        {
            float targetBlocks = _shaping.TargetPeakFillFraction * HeadroomBlocks;
            float wanted = targetBlocks / CalibratedPeakMeters * MetersPerBlock;
            float allowed = Math.Clamp(wanted, _shaping.MinAutoExaggeration, _shaping.MaxAutoExaggeration);
            CalibrationClamped = allowed < wanted;
            _baseBlocksPerMeter = allowed * trueScaleBlocksPerMeter;
        }
        else if (_shaping.MetersPerBlockVertical > 0f && _shaping.HeightMode == "manual")
        {
            _baseBlocksPerMeter = 1f / _shaping.MetersPerBlockVertical;
            CalibrationClamped = false;
        }
        else
        {
            _baseBlocksPerMeter = trueScaleBlocksPerMeter;
            CalibrationClamped = false;
        }

        _blocksPerMeter = VerticalExaggeration * _baseBlocksPerMeter;

        float naturalPeakBlocks = ModelMaxElevationMeters * _blocksPerMeter;
        if (naturalPeakBlocks <= HeadroomBlocks)
        {
            // Everything the model can produce already fits; no compression needed.
            _kneeBlocks = HeadroomBlocks;
            LinearRangeMeters = ModelMaxElevationMeters;
        }
        else
        {
            _kneeBlocks = HeadroomBlocks * _shaping.LinearKneeFraction;
            LinearRangeMeters = _kneeBlocks / _blocksPerMeter;
        }

        _oceanScale = _shaping.OceanDepthFraction * Math.Max(8, SeaLevel - 4) / DepthCurve(ModelMaxDepthMeters);
    }

    /// <summary>Shape of the sea-floor curve: steep near the coast, heavily compressed in the abyss.</summary>
    private static float DepthCurve(float meters) => (float)(Math.Sqrt(meters + 10.0) - Math.Sqrt(10.0) + 1.0);

    /// <summary>
    /// Converts a model elevation in metres to a block Y coordinate.
    ///
    /// Land is linear up to the knee and then bends over towards the ceiling. The bend is
    /// <c>u / (1 + u)</c>, which has slope 1 at the join so there is no crease, and — unlike a
    /// saturating exponential — never quite flattens, so even in a region whose mountains overrun
    /// the world by several kilometres the summits stay rounded instead of shearing off into a
    /// mesa. Ocean floors use a square-root curve so that abyssal plains stay within the (much
    /// shallower) block budget below sea level.
    /// </summary>
    public int ElevationToBlockY(float meters)
    {
        if (meters >= 0f)
        {
            float linear = meters * _blocksPerMeter;
            float y;
            if (linear <= _kneeBlocks)
            {
                y = linear;
            }
            else
            {
                float span = Math.Max(1f, HeadroomBlocks - _kneeBlocks);
                float u = (linear - _kneeBlocks) / span;
                y = _kneeBlocks + span * (u / (1f + u));
            }
            return SeaLevel + (int)y;
        }

        return Math.Max(2, SeaLevel - (int)(DepthCurve(-meters) * _oceanScale));
    }

    /// <summary>Highest block Y the mapping can ever return, used for sanity logging.</summary>
    public int MaxBlockY => ElevationToBlockY(ModelMaxElevationMeters);

    /// <summary>
    /// Highest ground at <paramref name="temperatureC"/> whose temperature the climate map can still
    /// represent, in metres.
    ///
    /// Vintage Story stores temperature as one byte and re-applies its own lapse rate — a flat
    /// 1/1.5 units per block — whenever it reads the map, so the value written has to carry that
    /// correction on top of the real temperature. The two together can overflow the byte: at a fine
    /// vertical scale there are a great many blocks between sea level and a summit, and warm high
    /// ground runs out of scale and reads colder than the model said. Above this elevation the
    /// error grows by 0.235 C per block.
    /// </summary>
    public float TemperatureCeilingMeters(float temperatureC)
    {
        float blocks = 1.5f * (255f - (temperatureC + 20f) * 4.25f);
        return blocks <= 0f ? 0f : blocks * MetersPerBlockVertical;
    }

    /// <summary>Human-readable summary for the log and the /terraindiffusion command.</summary>
    public string Describe()
    {
        string height = IsCalibrated
            ? $"height calibrated to a {CalibratedPeakMeters:0} m peak ({EffectiveExaggeration:0.##}x, " +
              $"{MetersPerBlockVertical:0.##} m/block vertical)"
            : IsIsotropic
                ? "height true to scale (1 block = 1 block in every direction)"
                : $"height {EffectiveExaggeration:0.##}x ({MetersPerBlockVertical:0.##} m/block vertical)";

        return $"scale {Scale} ({MetersPerBlock:0.##} m/block), {height}, " +
               $"sea level {SeaLevel}, world height {MapSizeY}, headroom {HeadroomBlocks} blocks " +
               $"(linear up to {LinearRangeMeters:0} m), climate {ClimateMode.ToString().ToLowerInvariant()}";
    }

    /// <summary>
    /// True when the world is so short that most mountains would be squashed. Only meaningful
    /// without calibration: a calibrated world fits its own terrain by construction, and when it
    /// cannot, <see cref="CalibrationClamped"/> says so instead.
    /// </summary>
    public bool IsWorldTooShort => !IsCalibrated && LinearRangeMeters < 3200f;

    /// <summary>World height that would keep terrain up to 5000 m perfectly to scale.</summary>
    public int RecommendedMapSizeY
    {
        get
        {
            float neededHeadroom = 5000f * _blocksPerMeter / _shaping.LinearKneeFraction;
            float sealevelFraction = MapSizeY > 0 ? (float)SeaLevel / MapSizeY : 0.4313725f;
            int needed = (int)Math.Ceiling((neededHeadroom + 3) / Math.Max(0.05f, 1f - sealevelFraction));
            return Math.Min(4096, ((needed + 127) / 128) * 128);
        }
    }
}
