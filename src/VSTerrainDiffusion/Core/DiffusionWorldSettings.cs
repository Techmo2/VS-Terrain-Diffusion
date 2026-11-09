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
public sealed partial class DiffusionWorldSettings
{
    /// <summary>Whether the mod generates this world's terrain.</summary>
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

    /// <summary>
    /// How much of the base model's first draw its second step redraws, as a noise level. 0.35 is
    /// the reference implementation's quality optimum, and the default. World
    /// setting <c>terraindiffusionDetailRedraw</c>, in hundredths.
    /// </summary>
    public float BaseRenoiseSigma { get; private set; } = 0.35f;

    /// <summary>
    /// Standard deviation, in signed square-root metres, of noise added to the coarse model's
    /// elevation before the base model draws the detail. 0 is off. World setting
    /// <c>terraindiffusionHeightNoise</c>, in hundredths.
    /// </summary>
    public float CoarseHeightNoise { get; private set; }

    /// <summary>
    /// How fast temperature falls with height, as a share of the reference 6.5 C/km: above 1, the
    /// difference is added to the model's own local rate, at the ground and in the air above it
    /// alike, so 300% is 13 C/km colder than the model; sea level keeps its temperature. World
    /// setting <c>terraindiffusionAltitudeCooling</c>, in percent.
    /// </summary>
    public float AltitudeCooling { get; private set; } = 1f;

    /// <summary>
    /// Whether the sea takes its shore's climate before the climate is spread to columns (see
    /// <c>Pipeline.CoastFill</c>). Pinned when a world is created: on for new worlds, off for worlds
    /// made before it, whose coasts were generated without it. Left out of the tile database's
    /// resolved-settings hash: the world setting carries it there.
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool CoastFill { get; private set; }

    /// <summary>
    /// Whether vanilla's block layers may raise dry-climate water up to 20 blocks deep into land
    /// (see <c>WorldGen.DryShallowsCompat</c>). Pinned when a world is created: off for new worlds,
    /// on for worlds made before, whose dry coasts were generated with it. Not part of the tiles.
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool DryShallowsFill { get; private set; }

    /// <summary>
    /// Whether the world gets lakes and rivers (see <c>WorldGen.WorldWater</c>). World setting
    /// <c>terraindiffusionWaterFill</c>, pinned when a world is created: on for new worlds, off for
    /// worlds made before lakes. Not part of the tiles, like every water setting below.
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool WaterFill { get; private set; }

    /// <summary>
    /// Blocks lakes stand below where they would spill; the ground they leave is wetland, marsh at
    /// the waterline with pools. 0 fills lakes to the brim (<c>terraindiffusionWetlandDepth</c>).
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public int WetlandDepth { get; private set; } = 1;

    /// <summary>
    /// Share of the peak discharge (the discharge cap) a river section must carry to be cut, on top
    /// of the river mode's threshold: trims the small tributaries (<c>terraindiffusionRiverTrim</c>, percent).
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public float RiverTrim { get; private set; } = 0.3f;

    /// <summary>Which rivers are cut into the land (<c>terraindiffusionRiverMode</c>).</summary>
    [Newtonsoft.Json.JsonIgnore]
    public RiverMode RiverMode { get; private set; } = RiverMode.Trunk;

    /// <summary>
    /// Multiplies every river's physical width and depth; 1 is true to scale
    /// (<c>terraindiffusionRiverWidth</c>, in percent).
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public float RiverWidth { get; private set; } = 20f;

    /// <summary>Path length over straight distance of a river on a wide plain; 1 is straight.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public float MeanderSinuosity { get; private set; } = 1.9f;

    /// <summary>About the distance between successive bends, metres.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public float MeanderWavelengthMeters { get; private set; } = 600f;

    /// <summary>Gradient at which the meander strength halves.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public float MeanderSlope { get; private set; } = 0.015f;

    /// <summary>Local relief, metres, at which the meander strength halves.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public float MeanderReliefMeters { get; private set; } = 4f;

    /// <summary>Distance, metres, over which relief holds a river in.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public float MeanderConfinementMeters { get; private set; } = 240f;

    /// <summary>Relief, as a multiple of <see cref="MeanderReliefMeters"/>, above which rivers run straight downhill.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public float MeanderFreeRelief { get; private set; } = 2f;

    /// <summary>Threads working out the water; 0 is automatic. Never changes the world.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public int WaterThreads { get; private set; }

    /// <summary>The water library version this world's water is made with (<c>terraindiffusionWaterVersion</c>).</summary>
    [Newtonsoft.Json.JsonIgnore]
    public int WaterVersion { get; private set; } = 1;

    /// <summary>
    /// Scales the forest cover the model's moisture implies. Trees on the ground go as its square:
    /// vanilla accepts each candidate tree with probability <c>(byte / 255)^2</c>. World setting
    /// <c>terraindiffusionForestDensity</c>, in percent.
    /// </summary>
    public float ForestDensity { get; private set; } = 1f;

    /// <summary>Scales shrub cover the same way. World setting <c>terraindiffusionShrubDensity</c>, in percent.</summary>
    public float ShrubDensity { get; private set; } = 1f;

    /// <summary>
    /// How far the land is lowered where sneeze's Rivers runs a river, 0 to 1, so the model draws a
    /// valley for it rather than a ridge to be cut through. Nothing without that mod. Measured against
    /// ~505 m either side of a corridor: 0.3 brings it to 334 m, 0.5 to 251 m, 1.0 to 28 m but
    /// drowns a fifth of it. World setting <c>terraindiffusionRiverValleyDepth</c>, in percent: 20 for
    /// new worlds (worldconfig.json); a world from before the setting existed reads 30, what it was
    /// generated with.
    /// </summary>
    public float RiverBasinDepth { get; private set; } = 0.3f;

    /// <summary>Pooling of the coarse model's output; see <see cref="Pipeline.CoarsePooling"/>.</summary>
    public Pipeline.CoarsePooling CoarsePooling { get; private set; } = Pipeline.CoarsePooling.None;

    /// <summary>Metres of real-world distance per block, horizontally.</summary>
    public float MetersPerBlock => NativeResolution / Scale;

    /// <summary>
    /// Blocks across one pixel of the coarse model's conditioning. A coarse cell of the world is 256
    /// model pixels, <c>256 * Scale</c> blocks; pooling packs that many conditioning pixels into it
    /// in each direction. Whatever feeds the conditioning from a world map - the ocean map, latitude,
    /// river basins - samples at this spacing.
    /// </summary>
    public int BlocksPerConditioningPixel =>
        32 * Pipeline.WorldPipelineModelConfig.Instance.LatentCompression * Scale / CoarsePooling.Factor;

    /// <summary>Metres per model pixel, straight from the model config.</summary>
    public float NativeResolution { get; private set; } = 30f;

    /// <summary>The world's sea level, the Y of the first air block above the sea.</summary>
    public int SeaLevel { get; private set; }

    /// <summary>The world's height in blocks.</summary>
    public int MapSizeY { get; private set; }

    /// <summary>Sea level and the room around it; see <see cref="HeightFrame"/>.</summary>
    public HeightFrame Heights => new(SeaLevel, MapSizeY);

    /// <summary>The world's size along X in blocks.</summary>
    public int MapSizeX { get; private set; }

    /// <summary>The world's size along Z in blocks.</summary>
    public int MapSizeZ { get; private set; }

    /// <summary>Whether a world block column exists at these coordinates.</summary>
    public bool IsInsideWorld(int blockX, int blockZ)
        => blockX >= 0 && blockZ >= 0 && blockX < MapSizeX && blockZ < MapSizeZ;

    /// <summary>
    /// World block coordinates that the model's own origin maps to. Vintage Story worlds are
    /// centred on (MapSizeX/2, MapSizeZ/2), so anchoring the model there means a given seed
    /// produces the same landscape around spawn no matter how large the world is.
    /// </summary>
    public int OriginBlockX { get; private set; }

    /// <summary>See <see cref="OriginBlockX"/>.</summary>
    public int OriginBlockZ { get; private set; }

    /// <summary>Multiplies the Perlin roughness added to sloped ground.</summary>
    public float SlopeDetailStrength { get; private set; } = 1f;

    /// <summary>The config's world generation section, as it was when this world was read.</summary>
    private WorldGenConfig _shaping = new();

    /// <summary>
    /// Whether this world wants the mod at all. Deliberately the only thing that can be asked
    /// before the models are on disk: <see cref="FromWorld"/> reads the model's own config and its
    /// climate tables, so it cannot answer until the download has finished.
    /// </summary>
    public static bool EnabledForWorld(ICoreServerAPI api) =>
        ReadWorldConfig(api.WorldManager.SaveGame.WorldConfiguration, "terraindiffusionEnabled", "true").ToBool(true);

    /// <summary>
    /// Reads the world's settings. Requires the model assets, so call it only once
    /// <see cref="Pipeline.PipelineModels.Await"/> has returned.
    /// </summary>
    public static DiffusionWorldSettings FromWorld(ICoreServerAPI api, float nativeResolution)
    {
        ITreeAttribute worldConfig = api.WorldManager.SaveGame.WorldConfiguration;
        WorldGenConfig shaping = DiffusionConfig.Instance.WorldGen;

        int scale = shaping.ScaleOverride != 0
            ? shaping.ScaleOverride
            : GameMathClamp(ReadWorldConfig(worldConfig, "terraindiffusionScale", "2").ToInt(2), 1, 16);

        float exaggeration = shaping.VerticalExaggerationOverride != 0f
            ? shaping.VerticalExaggerationOverride
            : Math.Clamp(ReadWorldConfig(worldConfig, "terraindiffusionVerticalExaggeration", "1").ToFloat(1f), 0.05f, 20f);

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
            Enabled = EnabledForWorld(api),
            ClimateMode = ParseClimateMode(shaping.ClimateMode.Length > 0
                ? shaping.ClimateMode
                : ReadWorldConfig(worldConfig, "terraindiffusionClimate", "full")),
            Scale = scale,
            VerticalExaggeration = exaggeration,
            // Both in hundredths: the mod DB refuses the decimal slider type, so they are integer sliders.
            BaseRenoiseSigma = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionDetailRedraw", "35").ToInt(35) / 100f, 0.05f, 4f),
            CoarseHeightNoise = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionHeightNoise", "0").ToInt(0) / 100f, 0f, 10f),
            CoastFill = ReadWorldConfig(worldConfig, "terraindiffusionCoastFill", "false").ToBool(false),
            DryShallowsFill = ReadWorldConfig(worldConfig, "terraindiffusionDryShallowsFill", "true").ToBool(true),
            // Pinned when the world was created (WorldValuePins.Pin); a world from
            // before lakes reads off, as it was generated.
            WaterFill = ReadWorldConfig(worldConfig, "terraindiffusionWaterFill", "false").ToBool(false),
            // Pinned like WaterFill; the defaults equal worldconfig.json's. Integer sliders in fixed units.
            WetlandDepth = Math.Clamp(ReadWorldConfig(worldConfig, "terraindiffusionWetlandDepth", "1").ToInt(1), 0, 4),
            RiverTrim = Math.Clamp(ReadWorldConfig(worldConfig, "terraindiffusionRiverTrim", "30").ToInt(30), 0, 100) / 100f,
            RiverMode = ParseRiverMode(ReadWorldConfig(worldConfig, "terraindiffusionRiverMode", "trunk")),
            RiverWidth = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionRiverWidth", "2000").ToInt(2000), 100, 4000) / 100f,
            MeanderSinuosity = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionMeanderSinuosity", "190").ToInt(190), 100, 300) / 100f,
            MeanderWavelengthMeters = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionMeanderWavelength", "600").ToInt(600), 100, 3000),
            MeanderSlope = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionMeanderSlope", "150").ToInt(150), 10, 1000) / 10000f,
            MeanderReliefMeters = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionMeanderRelief", "40").ToInt(40), 5, 500) / 10f,
            MeanderConfinementMeters = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionMeanderConfinement", "240").ToInt(240), 30, 1500),
            MeanderFreeRelief = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionMeanderFreeRelief", "20").ToInt(20), 5, 100) / 10f,
            WaterThreads = Math.Clamp(ReadWorldConfig(worldConfig, "terraindiffusionWaterThreads", "0").ToInt(0), 0, 64),
            WaterVersion = ReadWorldConfig(worldConfig, "terraindiffusionWaterVersion", "1").ToInt(1),
            AltitudeCooling = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionAltitudeCooling", "100").ToInt(100) / 100f, 0.1f, 5f),
            ForestDensity = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionForestDensity", "100").ToInt(100) / 100f, 0f, 4f),
            ShrubDensity = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionShrubDensity", "100").ToInt(100) / 100f, 0f, 4f),
            RiverBasinDepth = Math.Clamp(
                ReadWorldConfig(worldConfig, "terraindiffusionRiverValleyDepth", "30").ToInt(30) / 100f, 0f, 1f),
            CoarsePooling = Pipeline.CoarsePooling.Of(
                ReadWorldConfig(worldConfig, "terraindiffusionCoarsePooling", "1").ToInt(1),
                ReadWorldConfig(worldConfig, "terraindiffusionCoarsePoolMode", "average") == "extreme"),
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
            shaping.LatitudeStrength, climate,
            // Pinned when the world was created (WorldValuePins.Pin); a world
            // from before version 2 keeps the profile its ground was made with.
            ReadWorldConfig(worldConfig, "terraindiffusionLatitudeProfile", "1").ToInt(1),
            // Off for new worlds (worldconfig.json); a world made before the setting reads true, as generated.
            ReadWorldConfig(worldConfig, "terraindiffusionBandedRainfallBias", "true").ToBool(true));

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
    /// <param name="nativeResolution">Metres per model pixel, from the model config.</param>
    /// <param name="scale">How many times each model pixel is subdivided, as <see cref="Scale"/>.</param>
    /// <param name="mapSizeY">World height in blocks.</param>
    /// <param name="seaLevel">Sea level, as <see cref="SeaLevel"/>.</param>
    /// <param name="latitude">
    /// Bands to generate under, for checking a banded world without a server. Null for an
    /// unbanded one.
    /// </param>
    /// <param name="pooling">Coarse pooling, as <see cref="CoarsePooling"/>; none by default.</param>
    public static DiffusionWorldSettings ForOfflineUse(float nativeResolution, int scale, int mapSizeY,
                                                       int seaLevel, LatitudeBands latitude = null,
                                                       Pipeline.CoarsePooling pooling = default)
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
            Latitude = latitude ?? LatitudeBands.None,
            CoarsePooling = pooling.Factor >= 1 ? pooling : Pipeline.CoarsePooling.None
        };

        settings.RecomputeMapping();
        return settings;
    }

    /// <summary>
    /// Reads a world config value as text whatever attribute type it ended up as. The world
    /// creation screen writes these as strings, but a value set in serverconfig.json arrives
    /// typed, and <c>ITreeAttribute.GetString</c> silently returns the fallback for those.
    /// </summary>
    internal static string ReadWorldConfig(ITreeAttribute config, string code, string fallback)
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

    /// <summary>Clamps an integer to [<paramref name="min"/>, <paramref name="max"/>].</summary>
    private static int GameMathClamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>Keeps the model origin on a chunk boundary so tile and chunk grids stay aligned.</summary>
    private static int RoundToChunk(int blocks) => blocks / 32 * 32;

    /// <summary>
    /// A <c>terraindiffusionClimate</c> value; anything not naming vanilla means the model's climate.
    /// </summary>
    private static WorldGen.DiffusionClimateMode ParseClimateMode(string value) => value switch
    {
        "off" or "false" or "vanilla" => WorldGen.DiffusionClimateMode.Off,
        _ => WorldGen.DiffusionClimateMode.Full
    };

    /// <summary>
    /// A <c>terraindiffusionRiverMode</c> value; anything unrecognised means <see cref="RiverMode.Trunk"/>.
    /// </summary>
    private static RiverMode ParseRiverMode(string value) => value switch
    {
        "off" => RiverMode.Off,
        "creek" => RiverMode.Creek,
        "river" => RiverMode.River,
        _ => RiverMode.Trunk
    };

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
}
