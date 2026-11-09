using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Puts the world's sea level where its <c>terraindiffusionSeaLevel</c> setting says, for every
/// system that reads it: the server's own figure (sent to clients with the world), world
/// generation's <c>TerraGenConfig.seaLevel</c>, and the climate helpers' <c>Climate.Sealevel</c>.
///
/// Vanilla sets all three in <c>GenTerra.AssetsFinalize</c> (execute order 0), and the deposit
/// generator reads them in its own <c>AssetsFinalize</c> at 0.2, so this runs in between. Rivers'
/// generator sets them again at <c>ModsAndConfigReady</c> (once <c>LoadGamePre</c>) - registered or not, and even when stood down -
/// so they are set once more after it. Watersheds keeps whatever it finds.
///
/// Nearly everything in the game measures from sea level (<c>y - seaLevel</c>) or across the room
/// above it (<c>(y - seaLevel) / (MapSizeY - seaLevel)</c>), and follows the sea wherever it goes.
/// The few things written as fractions of world height are moved through <see cref="HeightFrame"/>
/// by <see cref="BlockLayerAltitude"/> and <see cref="TreeHeightFrame"/>.
///
/// Clouds sit at the top of the world, and vanilla's cloud map reaches only four view distances
/// out, so with the sea moved down the clouds hang so far overhead that the map's edge shows well
/// above the horizon. <see cref="LowerClouds"/> puts them as far above the sea as in a default
/// vanilla world.
/// </summary>
public class SeaLevelOverride : ModSystem
{
    /// <summary>The world setting naming the sea level: a block Y, or "vanilla".</summary>
    public const string WorldConfigCode = "terraindiffusionSeaLevel";

    /// <summary>Least room left above the sea, so a short world cannot be all ocean.</summary>
    private const int MinimumBlocksAboveSea = 64;

    /// <summary>Least room below it, for a sea floor and the rock beneath.</summary>
    private const int MinimumBlocksBelowSea = 32;

    /// <summary>How far clouds are above the sea in a default vanilla world: 256 tall, sea at 110.</summary>
    private const int VanillaCloudsAboveSea = 256 - 110;

    /// <summary>Vanilla's world setting for cloud height, as a fraction of world height.</summary>
    private const string CloudHeightCode = "cloudypos";

    /// <summary>Backing field of <see cref="Current"/>.</summary>
    private static HeightFrame? _current;

    /// <summary>The server.</summary>
    private ICoreServerAPI _api;

    /// <summary>The sea level the world asks for, or null for vanilla's.</summary>
    private int? _seaLevel;

    /// <summary>Whether the moved sea level has been logged.</summary>
    private bool _logged;

    /// <summary>The world's vertical layout once sea level is settled; null before that, or client side.</summary>
    public static HeightFrame? Current => _current;

    /// <summary>After GenTerra sets vanilla's sea level (0), before GenDeposits reads it (0.2).</summary>
    public override double ExecuteOrder() => 0.05;

    /// <summary>
    /// Applies the sea level once mods and config are ready, and lowers the clouds once the game runs.
    /// </summary>
    public override void StartServerSide(ICoreServerAPI api)
    {
        _api = api;
        _current = null;

        // Registered after Rivers', whose generator (execute order 0) resets sea level here.
        api.Event.ServerRunPhase(EnumServerRunPhase.ModsAndConfigReady, Apply);

        // The weather system reads its cloud height when the save loads; players are sent it as they join.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, LowerClouds);
    }

    /// <summary>Reads the world's sea level and applies it before world generation sets up.</summary>
    public override void AssetsFinalize(ICoreAPI api)
    {
        if (api is not ICoreServerAPI sapi) return;
        _api = sapi;
        _seaLevel = Requested(sapi);
        Apply();
    }

    /// <summary>
    /// Gives the client's climate helpers the world's sea level, which only the server otherwise sets.
    /// </summary>
    public override void StartClientSide(ICoreClientAPI api)
    {
        // The climate helpers read a static that only the server's GenTerra ever sets, so a client
        // on a dedicated server otherwise works out altitude temperatures from the default 110.
        api.Event.LevelFinalize += () => Climate.Sealevel = api.World.SeaLevel;
    }

    /// <summary>The sea level this world asks for, or null for vanilla's.</summary>
    private static int? Requested(ICoreServerAPI api)
    {
        if (api.WorldManager.SaveGame?.WorldType != "standard") return null;
        if (!DiffusionWorldSettings.EnabledForWorld(api)) return null;

        ITreeAttribute config = api.WorldManager.SaveGame.WorldConfiguration;

        // New worlds get worldconfig.json's default (128) written into their settings. A save without
        // the setting predates it and keeps the vanilla sea it was generated with.
        string value = DiffusionWorldSettings.ReadWorldConfig(config, WorldConfigCode, "vanilla").Trim().ToLowerInvariant();
        if (value == "vanilla" || !int.TryParse(value, out int seaLevel)) return null;

        int mapSizeY = api.WorldManager.MapSizeY;
        int clamped = Math.Clamp(seaLevel, MinimumBlocksBelowSea, Math.Max(MinimumBlocksBelowSea, mapSizeY - MinimumBlocksAboveSea));
        if (clamped != seaLevel)
        {
            api.Logger.Warning("[{0}] Sea level {1} leaves too little room in a world {2} tall; using {3}.",
                DiffusionPaths.ModId, seaLevel, mapSizeY, clamped);
        }
        return clamped;
    }

    /// <summary>
    /// Puts clouds <see cref="VanillaCloudsAboveSea"/> above a moved sea, unless the world sets its
    /// own cloud height. Not saved: <c>/weather cloudypos</c> still overrides it until a restart.
    /// </summary>
    private void LowerClouds()
    {
        if (_seaLevel is not int seaLevel) return;
        if (_api.WorldManager.SaveGame.WorldConfiguration?.HasAttribute(CloudHeightCode) == true) return;
        if (_api.ModLoader.GetModSystem<WeatherSystemServer>() is not { } weather) return;

        int mapSizeY = _api.WorldManager.MapSizeY;
        int cloudY = Math.Min(mapSizeY, seaLevel + VanillaCloudsAboveSea);
        weather.CloudLevelRel = cloudY / (float)mapSizeY;
        _api.Logger.Notification("[{0}] Clouds at y {1}, {2} blocks above the sea as in a default world.",
            DiffusionPaths.ModId, cloudY, cloudY - seaLevel);
    }

    /// <summary>Sets the sea level everywhere the game keeps it, and records <see cref="Current"/>.</summary>
    private void Apply()
    {
        if (_api == null) return;
        int mapSizeY = _api.WorldManager.MapSizeY;

        if (_seaLevel is int seaLevel)
        {
            TerraGenConfig.seaLevel = seaLevel;
            _api.WorldManager.SetSeaLevel(seaLevel);
            Climate.Sealevel = seaLevel;
        }

        _current = new HeightFrame(_api.World.SeaLevel, mapSizeY);
        if (_seaLevel != null && !_logged)
        {
            _logged = true;
            _api.Logger.Notification("[{0}] Sea level moved: {1}.", DiffusionPaths.ModId, _current);
        }
    }
}
