using System;
using System.Globalization;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using VSTerrainDiffusion.Pipeline;

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
    public int MapSizeX { get; private set; }
    public int MapSizeZ { get; private set; }

    /// <summary>
    /// The world's "Starting climate" choice, or null when the search for it is switched off or the
    /// setting holds something unrecognised. The spawn search uses it to pick where to put the
    /// player; nothing else reads it.
    /// </summary>
    public StartingClimate? StartingClimate { get; private set; }

    /// <summary>
    /// The world's "Global temperature" and "Global precipitation" settings, as read. What the
    /// model makes of them is decided by the plan they were turned into, not here.
    /// </summary>
    public ClimateShift Climate { get; private set; }

    /// <summary>
    /// The share of <c>globalTemperature</c> the conditioning did not deliver, applied to the
    /// model's output. One when the model was asked for all of it, or when neither is in play.
    /// </summary>
    public float TemperatureCorrection { get; private set; } = 1f;

    /// <summary>The same for <c>globalPrecipitation</c>.</summary>
    public float RainfallCorrection { get; private set; } = 1f;

    /// <summary>
    /// Where the equator and the poles are, and what climate that implies. Never null;
    /// <see cref="LatitudeBands.None"/> for a world with no latitude gradient.
    /// </summary>
    public LatitudeBands Latitude { get; private set; } = LatitudeBands.None;

    /// <summary>
    /// A model temperature as the world will actually read it, once whatever is left of the
    /// world's global setting and the config's offset are in. Every column's temperature passes
    /// through here on its way into a tile, so the climate map, the freeze line, the surface rules
    /// and the spawn search all read one number.
    ///
    /// The Z coordinate is what decides latitude in Vintage Story, and the latitude band the model
    /// could not be conditioned all the way into is added here.
    /// </summary>
    public float WorldTemperature(float modelTemperatureC, int blockZ)
    {
        // Latitude first, because the world's global setting scales the whole climate including
        // its north-south gradient: half a world is half its tropics and half its ice.
        float banded = modelTemperatureC + Latitude.TemperatureOffsetC(blockZ);
        float celsius = ClimateShift.ApplyTemperature(banded, TemperatureCorrection)
                        + _shaping.TemperatureOffsetC;

        // The far ends of the temperature setting ask for climates that are not on any scale the
        // game has: four times a 15 C world is 120 C. Vanilla arrives at the same place from the
        // other direction, its climate byte having saturated, so the honest reading of the setting
        // there is the top of the scale rather than a number nothing downstream can hold.
        return Math.Clamp(celsius, ClimateShift.ScaleFloorC, ClimateShift.ScaleCeilingC);
    }

    /// <summary>Annual rainfall as the world will read it, in millimetres.</summary>
    public float WorldPrecipitation(float modelPrecipitationMm, int blockZ)
        => modelPrecipitationMm * RainfallCorrection * Latitude.RainfallFactor(blockZ);

    /// <summary>Whether a world block column exists at these coordinates.</summary>
    public bool IsInsideWorld(int blockX, int blockZ)
        => blockX >= 0 && blockZ >= 0 && blockX < MapSizeX && blockZ < MapSizeZ;

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

        // The two global climate settings are split between the climate the model is conditioned
        // on and a correction to what it produces; see ClimateShift. The model is asked for the
        // split rather than told, so that the warp and the correction agree by construction.
        var climate = new ClimateShift(
            ReadWorldConfig(worldConfig, "globalTemperature", "1").ToFloat(1f),
            ReadWorldConfig(worldConfig, "globalPrecipitation", "1").ToFloat(1f));
        ClimatePlan plan = SyntheticMapFactory.PlanClimate(climate);

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
            MapSizeX = api.WorldManager.MapSizeX,
            MapSizeZ = api.WorldManager.MapSizeZ,
            SeaLevel = api.World.SeaLevel,
            OriginBlockX = RoundToChunk(api.WorldManager.MapSizeX / 2),
            OriginBlockZ = RoundToChunk(api.WorldManager.MapSizeZ / 2),
            Climate = climate,
            TemperatureCorrection = plan.TemperatureCorrection,
            RainfallCorrection = plan.RainfallCorrection,
            StartingClimate = shaping.StartingClimateSearch
                ? Core.StartingClimate.Parse(ReadWorldConfig(worldConfig, "startingClimate", "temperate"))
                : null
        };

        // Built after the settings object because it needs the model origin and the block scale,
        // and read straight from the game rather than invented: polarEquatorDistance is the world's
        // own setting, and the phase that puts the map centre on the chosen starting climate is
        // Vintage Story's own too.
        settings.Latitude = LatitudeBands.ForWorld(
            api, settings,
            ReadWorldConfig(worldConfig, "worldClimate", "realistic"),
            ReadWorldConfig(worldConfig, "polarEquatorDistance", "50000").ToInt(50000),
            shaping.LatitudeStrength, climate);

        if (!settings.Latitude.IsNeutral)
        {
            // Each band already carries the world's global climate settings, so the whole-world
            // correction that stands in for them on an unbanded world would apply them twice.
            settings.TemperatureCorrection = 1f;
            settings.RainfallCorrection = 1f;
        }

        settings.RecomputeMapping();
        return settings;
    }

    /// <summary>
    /// Builds settings without a running world, for the offline tools. The model origin sits at
    /// block (0, 0) so tool coordinates are model coordinates.
    /// </summary>
    /// <param name="latitude">
    /// Bands to generate under, for checking a banded world without a server. Null for an
    /// unbanded one.
    /// </param>
    public static DiffusionWorldSettings ForOfflineUse(float nativeResolution, int scale, int mapSizeY,
                                                       int seaLevel, LatitudeBands latitude = null)
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
            SeaLevel = seaLevel,
            Latitude = latitude ?? LatitudeBands.None
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

        // Measured from the curve's own value at the shore, not from zero, because that is where
        // the sea floor now starts: scaling the whole curve would scale its constant term too, and
        // that term is meant to be one block of water whatever the world's height.
        _oceanScale = _shaping.OceanDepthFraction * Math.Max(8, SeaLevel - 4)
                      / (DepthCurve(ModelMaxDepthMeters) - DepthCurve(0f));
    }

    /// <summary>Shape of the sea-floor curve: steep near the coast, heavily compressed in the abyss.</summary>
    private static float DepthCurve(float meters) => (float)(Math.Sqrt(meters + 10.0) - Math.Sqrt(10.0) + 1.0);

    /// <summary>
    /// Converts a model elevation in metres to a block Y coordinate: the topmost solid block of
    /// that column.
    ///
    /// Zero metres is the waterline, which is the <em>top</em> of block
    /// <see cref="WaterSurfaceY"/> — the sea fills every block below <see cref="SeaLevel"/>, so
    /// the last one it fills is the one under it. Land measured from <c>SeaLevel</c> instead would
    /// stand a block proud of the water everywhere along every coast, since a whole block of
    /// height is 15 m of elevation at the default resolution and the entire shore falls inside the
    /// first one.
    ///
    /// Land is linear up to the knee and then bends over towards the ceiling. The bend is
    /// <c>u / (1 + u)</c>, which has slope 1 at the join so there is no crease, and — unlike a
    /// saturating exponential — never quite flattens, so even in a region whose mountains overrun
    /// the world by several kilometres the summits stay rounded instead of shearing off into a
    /// mesa.
    ///
    /// Ocean floors use a square-root curve so that abyssal plains stay within the much shallower
    /// block budget below sea level. It is measured from the same waterline the land is, and has
    /// its own value at the shore taken off it, so that a column a handful of centimetres under
    /// water is a handful of centimetres under water rather than a cliff. What stops it being a
    /// puddle instead is the one-block floor below, which is a block because a block is the
    /// smallest depth the world can hold — not because of anything to do with the depth curve.
    /// That distinction is the whole bug this replaced: the floor used to be the curve's constant
    /// term, which <see cref="_oceanScale"/> then multiplied, so the shallows came out three
    /// blocks deep in a tall world and bone dry in a short one.
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
            return WaterSurfaceY + (int)y;
        }

        // At least one block, because anything below the waterline has to hold water, and a block
        // is the least the world can express: 15 m of elevation at the default resolution, so the
        // whole intertidal zone lands inside the first one.
        int depth = Math.Max(1, (int)((DepthCurve(-meters) - DepthCurve(0f)) * _oceanScale));
        return Math.Max(2, WaterSurfaceY - depth);
    }

    /// <summary>
    /// The topmost block the sea fills, whose upper face is the waterline. Ground at this height
    /// is level with the water rather than a step above it.
    /// </summary>
    public int WaterSurfaceY => SeaLevel - 1;

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

    /// <summary>
    /// How the terrain's height is being mapped, as a phrase. Shared by the one-line log summary
    /// and the field-per-line command readout so the two cannot drift apart.
    /// </summary>
    public string DescribeHeight() => IsCalibrated
        ? $"calibrated to a {CalibratedPeakMeters:0} m peak ({EffectiveExaggeration:0.##}x, " +
          $"{MetersPerBlockVertical:0.##} m/block vertical)"
        : IsIsotropic
            ? "true to scale (1 block = 1 block in every direction)"
            : $"{EffectiveExaggeration:0.##}x ({MetersPerBlockVertical:0.##} m/block vertical)";

    /// <summary>Human-readable summary for the log and the /terraindiffusion command.</summary>
    public string Describe()
    {
        string height = "height " + DescribeHeight();

        return $"scale {Scale} ({MetersPerBlock:0.##} m/block), {height}, " +
               $"sea level {SeaLevel}, world height {MapSizeY}, headroom {HeadroomBlocks} blocks " +
               $"(linear up to {LinearRangeMeters:0} m), climate {ClimateMode.ToString().ToLowerInvariant()}" +
               (Climate.IsNeutral ? "" : $" ({Climate})") +
               $", latitude bands {Latitude.Status}";
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
