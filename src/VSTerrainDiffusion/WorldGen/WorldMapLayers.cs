using System;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Finds whichever object actually owns the world's ocean, climate and vegetation map layers.
///
/// Vanilla keeps them in public fields on <see cref="GenMaps"/>, and reading them straight off that
/// instance is right for every world where vanilla still generates the maps - including one where
/// another mod has swapped a layer for its own, which is the case this mod most wants to honour.
///
/// It is not right when a mod replaces the map generator wholesale. Algernon's Watersheds patches
/// <c>GenMaps.StartServerSide</c> into a no-op and keeps the live layers on a mod system of its
/// own, so the vanilla instance survives with every field still null: reading it finds nothing, and
/// writing to it is thrown away. The field names are identical on the replacement, which is all
/// that is needed to find the real owner and talk to it without knowing anything about the mod.
/// </summary>
public sealed class WorldMapLayers
{
    private const BindingFlags FieldFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly object _owner;
    private readonly FieldInfo _ocean;
    private readonly FieldInfo _climate;
    private readonly FieldInfo _forest;
    private readonly FieldInfo _bush;

    private WorldMapLayers(object owner, FieldInfo ocean, FieldInfo climate, FieldInfo forest, FieldInfo bush)
    {
        _owner = owner;
        _ocean = ocean;
        _climate = climate;
        _forest = forest;
        _bush = bush;
    }

    /// <summary>Type name of the mod system the layers were found on, for logging.</summary>
    public string OwnerName => _owner.GetType().Name;

    /// <summary>True when the layers are vanilla's own, rather than a replacement generator's.</summary>
    public bool IsVanilla => _owner is GenMaps;

    public MapLayerBase Ocean
    {
        get => Get(_ocean);
        set => Set(_ocean, value);
    }

    public MapLayerBase Climate
    {
        get => Get(_climate);
        set => Set(_climate, value);
    }

    public MapLayerBase Forest
    {
        get => Get(_forest);
        set => Set(_forest, value);
    }

    public MapLayerBase Bush
    {
        get => Get(_bush);
        set => Set(_bush, value);
    }

    /// <summary>
    /// Picks the map generator to work with: the vanilla one when it has been started, otherwise
    /// whichever mod system carries the same fields and has actually filled them in. Returns null
    /// when nothing in the world has a climate map, which means map generation is somebody else's
    /// entirely and there is nothing here to hook.
    /// </summary>
    public static WorldMapLayers Resolve(ICoreServerAPI api)
    {
        WorldMapLayers vanilla = For(api.ModLoader.GetModSystem<GenMaps>());
        if (vanilla != null && vanilla.Climate != null) return vanilla;

        WorldMapLayers fallback = null;
        foreach (ModSystem system in api.ModLoader.Systems)
        {
            if (system is GenMaps) continue;

            WorldMapLayers candidate = For(system);
            if (candidate == null) continue;

            // A started generator has its layers; an unstarted one is just a lookalike.
            if (candidate.Climate != null) return candidate;
            fallback ??= candidate;
        }

        return vanilla ?? fallback;
    }

    /// <summary>Wraps an object if it carries the map layer fields, or null if it does not.</summary>
    private static WorldMapLayers For(object candidate)
    {
        if (candidate == null) return null;

        FieldInfo climate = LayerField(candidate, "climateGen");
        if (climate == null) return null;

        return new WorldMapLayers(
            candidate,
            LayerField(candidate, "oceanGen"),
            climate,
            LayerField(candidate, "forestGen"),
            LayerField(candidate, "bushGen"));
    }

    private static FieldInfo LayerField(object owner, string name)
    {
        FieldInfo field = owner.GetType().GetField(name, FieldFlags);
        return field != null && typeof(MapLayerBase).IsAssignableFrom(field.FieldType) ? field : null;
    }

    private MapLayerBase Get(FieldInfo field) => field?.GetValue(_owner) as MapLayerBase;

    private void Set(FieldInfo field, MapLayerBase value) => field?.SetValue(_owner, value);
}
