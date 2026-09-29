using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
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
/// </summary>
public class SeaLevelOverride : ModSystem
{
    public const string WorldConfigCode = "terraindiffusionSeaLevel";

    /// <summary>Least room left above the sea, so a short world cannot be all ocean.</summary>
    private const int MinimumBlocksAboveSea = 64;

    /// <summary>Least room below it, for a sea floor and the rock beneath.</summary>
    private const int MinimumBlocksBelowSea = 32;

    private static HeightFrame? _current;

    private ICoreServerAPI _api;
    private int? _seaLevel;
    private bool _logged;

    /// <summary>The world's vertical layout once sea level is settled; null before that, or client side.</summary>
    public static HeightFrame? Current => _current;

    /// <summary>After GenTerra sets vanilla's sea level (0), before GenDeposits reads it (0.2).</summary>
    public override double ExecuteOrder() => 0.05;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _api = api;
        _current = null;

        // Registered after Rivers', whose generator (execute order 0) resets sea level here.
        api.Event.ServerRunPhase(EnumServerRunPhase.ModsAndConfigReady, Apply);
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        if (api is not ICoreServerAPI sapi) return;
        _api = sapi;
        _seaLevel = Requested(sapi);
        Apply();
    }

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
