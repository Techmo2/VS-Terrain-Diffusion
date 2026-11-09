using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using VSTerrainDiffusion.WorldGen;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// World settings this mod decides for itself, once, when a world is created, and pins into the
/// world's configuration so that the same seed and settings always generate the same ground.
/// </summary>
/// <remarks>
/// Pinning has to happen in <c>StartPre</c>, before the game first writes a new world's save game at
/// asset finalisation. Until the world's first autosave or a completed shutdown that is the only
/// copy on disk, and the world generator's set-up comes after it: a world closed before either lost
/// values written there, and on its next load was taken for one from before they existed,
/// generating the rest of its terrain differently from the ground already there.
/// </remarks>
public static class WorldValuePins
{
    /// <summary>Shore detail (sqrt-encoded coast flattening), in the world configuration.</summary>
    public const string ShoreDetailCode = "terraindiffusionShoreDetail";

    /// <summary>How far inland the shore detail fades out, in the world configuration.</summary>
    public const string ShoreFadeCode = "terraindiffusionShoreFade";

    /// <summary>Ocean map lattice samples per coarse-cell axis, in the world configuration.</summary>
    public const string OceanMapSamplesCode = "terraindiffusionOceanMapSamples";

    /// <summary>Version of the latitude profile the world's climate bands follow.</summary>
    public const string LatitudeProfileCode = "terraindiffusionLatitudeProfile";

    /// <summary>Whether the sea takes the climate of the land beside it.</summary>
    public const string CoastFillCode = "terraindiffusionCoastFill";

    /// <summary>Whether vanilla's block layers may fill dry-climate shallows with land.</summary>
    public const string DryShallowsFillCode = "terraindiffusionDryShallowsFill";

    /// <summary>Whether the world has lakes and rivers.</summary>
    public const string WaterFillCode = "terraindiffusionWaterFill";

    /// <summary>Which rivers the world draws (<see cref="RiverMode"/>).</summary>
    public const string RiverModeCode = "terraindiffusionRiverMode";

    /// <summary>Blocks a lake stands below its spill point, leaving wetland above.</summary>
    public const string WetlandDepthCode = "terraindiffusionWetlandDepth";

    /// <summary>Percent of the discharge cap a channel needs to be drawn as a river.</summary>
    public const string RiverTrimCode = "terraindiffusionRiverTrim";

    /// <summary>The water library version the world's water was made with.</summary>
    public const string WaterVersionCode = "terraindiffusionWaterVersion";

    /// <summary>The water library version new worlds are made with; bump it when the library changes the water.</summary>
    public const int CurrentWaterVersion = 1;

    /// <summary>
    /// Water settings with their defaults (the same as worldconfig.json's), pinned so that a later
    /// default never changes the water of a world's ungenerated chunks.
    /// </summary>
    private static readonly (string Code, string Default)[] PinnedWaterDefaults =
    {
        ("terraindiffusionRiverWidth", "2000"),
        ("terraindiffusionMeanderSinuosity", "190"),
        ("terraindiffusionMeanderWavelength", "600"),
        ("terraindiffusionMeanderSlope", "150"),
        ("terraindiffusionMeanderRelief", "40"),
        ("terraindiffusionMeanderConfinement", "240"),
        ("terraindiffusionMeanderFreeRelief", "20")
    };

    /// <summary>
    /// World settings that decide what is built on the tiles, never what a tile holds. The region
    /// size of the earlier lakes stays listed: worlds made with them still carry it.
    /// </summary>
    public static readonly IReadOnlyList<string> TileNeutralCodes = new[]
    {
        WaterFillCode, "terraindiffusionWaterRegion", WetlandDepthCode, RiverTrimCode, RiverModeCode, WaterVersionCode,
        "terraindiffusionWaterThreads", "terraindiffusionRiverWidth", "terraindiffusionMeanderSinuosity",
        "terraindiffusionMeanderWavelength", "terraindiffusionMeanderSlope", "terraindiffusionMeanderRelief",
        "terraindiffusionMeanderConfinement", "terraindiffusionMeanderFreeRelief"
    };

    /// <summary>
    /// Values <see cref="Pin"/> writes onto a world made before they existed, with what such a world
    /// always had. A world holding exactly these hashes its tiles as it did before they were added.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyValues = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [LatitudeProfileCode] = "1",
        [CoastFillCode] = "false",
        [DryShallowsFillCode] = "true"
    };

    /// <summary>Save game mod data that held the shore detail before it was a world setting.</summary>
    private const string LegacyShoreDetailKey = "vsterraindiffusion:shoredetail";

    /// <summary>Save game mod data that held the ocean map sampling before it was a world setting.</summary>
    private const string LegacyOceanMapSamplesKey = "vsterraindiffusion:oceanmapsamples";

    /// <summary>
    /// Settles, once per world, every value that shapes its terrain without being on the Customize
    /// screen, and writes it into the world's settings.
    /// </summary>
    /// <remarks>
    /// A new world takes the config's shore detail, the sampled ocean map and today's climate and
    /// water behaviour. A world that kept a value in save game mod data carries it over. Any other
    /// world predates the value and is pinned to what it was generated with.
    /// </remarks>
    /// <param name="api">The server API of the world being loaded.</param>
    /// <exception cref="Exception">Fatal (see <see cref="DiffusionFailure"/>) when the settings cannot be read or written.</exception>
    public static void Pin(ICoreServerAPI api)
    {
        ISaveGame save = api.WorldManager.SaveGame;
        ITreeAttribute world = save?.WorldConfiguration;
        if (world == null) return;

        try
        {
            PinShoreDetail(api, save, world);

            // New worlds take today's latitude profile; one made before profiles were versioned keeps
            // version 1, so ground generated from here on still meets the ground it already has.
            PinIfMissing(world, LatitudeProfileCode,
                (save.IsNew ? LatitudeProfile.CurrentVersion : 1).ToString(CultureInfo.InvariantCulture));
            PinIfMissing(world, DryShallowsFillCode, save.IsNew ? "false" : "true");

            PinWater(api, save, world);

            PinIfMissing(world, CoastFillCode, save.IsNew ? "true" : "false");
            PinOceanMapSamples(save, world);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(api.Logger, "This world's terrain settings could not be read or saved.", e);
        }
    }

    /// <summary>The shore detail and fade <see cref="Pin"/> settled for this world.</summary>
    /// <param name="world">The world's configuration.</param>
    public static (float Detail, float Fade) ShoreDetail(ITreeAttribute world) =>
        (DiffusionWorldSettings.ReadWorldConfig(world, ShoreDetailCode, "0").ToFloat(0f),
         DiffusionWorldSettings.ReadWorldConfig(world, ShoreFadeCode, "3").ToFloat(3f));

    /// <summary>The ocean map sampling <see cref="Pin"/> settled for this world; 0 reads the full map.</summary>
    /// <param name="world">The world's configuration.</param>
    public static int OceanMapSamples(ITreeAttribute world) =>
        DiffusionWorldSettings.ReadWorldConfig(world, OceanMapSamplesCode, "0").ToInt(0);

    /// <summary>Writes <paramref name="value"/> under <paramref name="code"/> unless the world already has one.</summary>
    private static void PinIfMissing(ITreeAttribute world, string code, string value)
    {
        if (!world.HasAttribute(code)) world.SetString(code, value);
    }

    /// <summary>
    /// Pins shore detail and fade: carried over from legacy mod data, the config's value for a new
    /// world, or 0 (the plain metre mapping) for a world that predates it.
    /// </summary>
    private static void PinShoreDetail(ICoreServerAPI api, ISaveGame save, ITreeAttribute world)
    {
        if (world.HasAttribute(ShoreDetailCode) && world.HasAttribute(ShoreFadeCode)) return;

        WorldGenConfig shaping = DiffusionConfig.Instance.WorldGen;
        float detail, fade = shaping.ShoreFade;
        if (save.GetData(LegacyShoreDetailKey) is { Length: 2 * sizeof(float) } stored)
        {
            detail = BitConverter.ToSingle(stored, 0);
            fade = BitConverter.ToSingle(stored, sizeof(float));
        }
        else
        {
            detail = save.IsNew ? shaping.ShoreDetail : 0f;
            if (!save.IsNew && shaping.ShoreDetail != 0f)
            {
                api.Logger.Notification(
                    "[{0}] This world was generated before shore detail existed, so its coasts keep the " +
                    "plain metre mapping; shoreDetail applies to worlds created from now on.", DiffusionPaths.ModId);
            }
        }
        world.SetString(ShoreDetailCode, detail.ToString(CultureInfo.InvariantCulture));
        world.SetString(ShoreFadeCode, fade.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Pins the lake and river settings. Each arrived later than the last, and a world explored
    /// without one keeps its ground the way it was: no lakes, then no rivers, brim-full lakes and
    /// every small tributary.
    /// </summary>
    private static void PinWater(ICoreServerAPI api, ISaveGame save, ITreeAttribute world)
    {
        PinIfMissing(world, WaterFillCode, save.IsNew ? "true" : "false");

        // A world that already has lakes keeps its explored ground free of rivers that would start at
        // its edge; its lakes carry on with the new water.
        if (!world.HasAttribute(RiverModeCode))
        {
            world.SetString(RiverModeCode, save.IsNew ? "trunk" : "off");
            if (!save.IsNew && DiffusionWorldSettings.ReadWorldConfig(world, WaterFillCode, "false").ToBool(false))
            {
                api.Logger.Notification(
                    "[{0}] This world was made before rivers, so it gets none; its lakes are worked out by the " +
                    "new water from here on.", DiffusionPaths.ModId);
            }
        }

        PinIfMissing(world, WetlandDepthCode, save.IsNew ? "1" : "0");
        PinIfMissing(world, RiverTrimCode, save.IsNew ? "30" : "0");

        foreach ((string code, string value) in PinnedWaterDefaults) PinIfMissing(world, code, value);

        PinIfMissing(world, WaterVersionCode, CurrentWaterVersion.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Pins the ocean map sampling: carried over from legacy mod data, the default lattice for a new
    /// world, or 0 (the full map) for a world that predates it.
    /// </summary>
    private static void PinOceanMapSamples(ISaveGame save, ITreeAttribute world)
    {
        if (world.HasAttribute(OceanMapSamplesCode)) return;

        int samples = save.GetData(LegacyOceanMapSamplesKey) is { Length: sizeof(int) } stored
            ? BitConverter.ToInt32(stored, 0)
            : save.IsNew ? OceanMapLandmask.DefaultSamplesPerAxis : 0;
        world.SetString(OceanMapSamplesCode, samples.ToString(CultureInfo.InvariantCulture));
    }
}
