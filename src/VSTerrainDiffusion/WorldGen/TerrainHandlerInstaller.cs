using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Gets the model's heights into the world, whichever mod is generating terrain.
/// </summary>
/// <remarks>
/// Normally that is vanilla, and this mod's Terrain-pass handler takes the place of GenTerra's.
/// When another mod has replaced terrain generation outright there is nowhere to stand: two
/// generators filling the same column produce the union of both landscapes with only one mod's
/// heightmaps recorded. So the only supported arrangement with such a mod (Algernon's Watersheds) is
/// to hand it our heights and let it do the filling. Anything else stops the game: standing aside
/// would leave the world's existing chunks modelled and everything after them not.
/// </remarks>
public sealed class TerrainHandlerInstaller
{
    /// <summary>How many wrapper levels deep another mod may have nested our handler.</summary>
    private const int MaxWrapperDepth = 3;

    private readonly ICoreServerAPI _api;

    /// <summary>The handler put in the Terrain pass by the last installation, or null.</summary>
    private ChunkColumnGenerationDelegate _installedHandler;

    /// <summary>Creates the installer for one server.</summary>
    /// <param name="api">The server API.</param>
    public TerrainHandlerInstaller(ICoreServerAPI api)
    {
        _api = api;
    }

    /// <summary>
    /// Installs <paramref name="handler"/> in GenTerra's place, or hands the heights to Watersheds when
    /// it is present. Safe to call again on re-initialisation.
    /// </summary>
    /// <param name="handler">The Terrain-pass handler that fills a chunk column from the model.</param>
    /// <param name="provider">The world's terrain, for a handover.</param>
    public void Install(ChunkColumnGenerationDelegate handler, TerrainDiffusionProvider provider)
    {
        if (WatershedsCompat.IsPresent(_api))
        {
            HandOverToWatersheds(provider);
            return;
        }

        ReplaceGenTerra(handler);
    }

    /// <summary>
    /// Hands the diffusion heightmap to Algernon's Watersheds and leaves the filling, the rivers and
    /// everything downstream of them to it. See <see cref="WatershedsCompat"/> for how.
    /// </summary>
    private void HandOverToWatersheds(TerrainDiffusionProvider provider)
    {
        if (WatershedsCompat.TryInstall(_api, provider, out string failure))
        {
            _api.Logger.Notification(
                "[{0}] Algernon's Watersheds is generating this world's terrain, so the model is " +
                "supplying its heights instead of filling chunks itself. Its rivers and streams are " +
                "routed over the modelled landscape.", DiffusionPaths.ModId);
            return;
        }

        throw DiffusionFailure.Fatal(_api.Logger,
            "Algernon's Watersheds also generates terrain, and " + failure +
            ". Remove or update one of the two mods.");
    }

    /// <summary>
    /// Replaces vanilla GenTerra's Terrain-pass delegate in place, so the ordering relative to rock
    /// strata, caves and block layers is exactly what those systems expect.
    /// </summary>
    private void ReplaceGenTerra(ChunkColumnGenerationDelegate replacement)
    {
        IWorldGenHandler handlers = _api.Event.GetRegisteredWorldGenHandlers("standard");
        List<ChunkColumnGenerationDelegate> terrainPass = handlers.OnChunkColumnGen[(int)EnumWorldGenPass.Terrain];

        // Rivers leaves its own generator registered even when asked to stand aside, and two
        // generators filling one column produce the union of both landscapes. Before anything else,
        // so the vanilla slot below is the only one left to take.
        if (RiversCompat.Installed) RiversCompat.RemoveTerrainHandler(_api, terrainPass);

        // Re-initialisation (for example /wgen regen) hits this a second time. Our handler may by then
        // sit inside another mod's wrapper - ChunkLOD's worldgen profiler wraps every handler in the
        // pass - where it cannot be swapped and need not be: it reads the generator on every call, so
        // the wrapped copy already runs the generator this initialisation built.
        if (_installedHandler != null)
        {
            int existing = terrainPass.IndexOf(_installedHandler);
            if (existing >= 0)
            {
                terrainPass[existing] = replacement;
                _installedHandler = replacement;
                return;
            }

            if (terrainPass.Exists(d => Holds(d, _installedHandler, MaxWrapperDepth)))
            {
                _api.Logger.VerboseDebug("[{0}] Terrain handler is still installed, inside another mod's wrapper",
                    DiffusionPaths.ModId);
                return;
            }
        }

        int vanillaIndex = terrainPass.FindIndex(d => d.Target is GenTerra);
        if (vanillaIndex < 0)
        {
            // Something else already took vanilla GenTerra's place. Running as well as it would fill
            // every column twice, with the union of two landscapes and only one set of heightmaps.
            throw DiffusionFailure.Fatal(_api.Logger,
                "Another mod has already replaced vanilla GenTerra, so two generators would fill " +
                "the same columns. Remove one of them.");
        }

        terrainPass[vanillaIndex] = replacement;
        _api.Logger.VerboseDebug("[{0}] Replaced vanilla GenTerra at terrain handler index {1}",
            DiffusionPaths.ModId, vanillaIndex);
        _installedHandler = replacement;
    }

    /// <summary>
    /// Whether <paramref name="handler"/> is <paramref name="target"/>, or wraps it: a closure or
    /// object whose fields hold it, to <paramref name="depth"/> wrappers deep.
    /// </summary>
    private static bool Holds(Delegate handler, Delegate target, int depth)
    {
        if (handler == null) return false;
        if (handler.Equals(target)) return true;
        if (depth <= 0 || handler.Target == null) return false;

        foreach (FieldInfo field in handler.Target.GetType().GetFields(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (typeof(Delegate).IsAssignableFrom(field.FieldType) &&
                Holds(field.GetValue(handler.Target) as Delegate, target, depth - 1))
                return true;
        }
        return false;
    }
}
