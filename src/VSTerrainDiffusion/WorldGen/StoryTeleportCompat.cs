using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Makes <c>/wgen story tp</c> (alias <c>/tpstoryloc</c>) land on the ground.
///
/// Vanilla teleports to the middle of the structure's height range. Until the structure's chunks
/// are generated that range is a guess - sea level plus the schematic's offset for surface
/// structures and ruins - which is right on vanilla's own flattened sites, built at sea level, and
/// inside the hill on this mod's terrain. Once a surface ruin is placed its range is mostly below
/// ground (the Crossroads runs from 80 blocks under the surface to 18 above), so its middle is
/// underground too. This keeps vanilla's X and Z and puts the player on top of whatever is there,
/// structure included: at once if the column exists, else as soon as it has been generated.
/// </summary>
public static class StoryTeleportCompat
{
    private const string HarmonyId = "vsterraindiffusion.storyteleport";

    private static Harmony _harmony;
    private static ICoreServerAPI _api;
    private static TerrainDiffusionProvider _provider;

    public static void Install(ICoreServerAPI api, TerrainDiffusionProvider provider)
    {
        _api = api;
        _provider = provider;
        if (_harmony != null) return;
        MethodInfo target = AccessTools.Method(typeof(GenStoryStructures), "OnTpStoryLoc");
        if (target == null)
        {
            api.Logger.Warning("[{0}] Could not find the story structure teleport; /tpstoryloc keeps vanilla's height.",
                DiffusionPaths.ModId);
            return;
        }
        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, prefix: new HarmonyMethod(typeof(StoryTeleportCompat), nameof(BeforeTpStoryLoc)));
    }

    public static void Uninstall()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        _api = null;
        _provider = null;
    }

    private static void TeleportOnTop(ICoreServerAPI api, Entity entity, int x, int z)
    {
        if (entity == null || !entity.Alive) return;
        int top = api.World.BlockAccessor.GetRainMapHeightAt(x, z);
        entity.TeleportTo(new Vec3d(x + 0.5, top + 1, z + 0.5));
    }

    private static bool BeforeTpStoryLoc(GenStoryStructures __instance, TextCommandCallingArgs args, ref TextCommandResult __result)
    {
        ICoreServerAPI api = _api;
        TerrainDiffusionProvider provider = _provider;
        if (api == null || provider == null || args[0] is not string code || args.Caller?.Entity == null) return true;
        StoryStructureLocation location = __instance.Structures?.Get(code);
        if (location?.CenterPos == null) return true;

        int x = location.CenterPos.X, z = location.CenterPos.Z;
        Entity entity = args.Caller.Entity;
        int chunkX = x / GlobalConstants.ChunkSize, chunkZ = z / GlobalConstants.ChunkSize;
        IMapChunk mapChunk = api.WorldManager.GetMapChunk(chunkX, chunkZ);
        if (mapChunk != null && mapChunk.CurrentPass >= EnumWorldGenPass.Done)
        {
            TeleportOnTop(api, entity, x, z);
            __result = TextCommandResult.Success($"Teleporting to {code}");
            return false;
        }

        // Not generated yet, so its top - the structure's own blocks included - is not known: make
        // the column first, then land on it. A structure standing on the ground would otherwise be
        // built around the player.
        api.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions
        {
            OnLoaded = () => api.Event.EnqueueMainThreadTask(() => TeleportOnTop(api, entity, x, z), "storyteleport")
        });
        __result = TextCommandResult.Success($"Generating {code}; teleporting once its ground is ready");
        return false;
    }
}
