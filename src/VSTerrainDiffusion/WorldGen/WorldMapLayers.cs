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
    /// <summary>
    /// The layer fields are public on <see cref="GenMaps"/> but may not be on a replacement.
    /// </summary>
    private const BindingFlags FieldFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>The object holding the layers.</summary>
    private readonly object _owner;

    /// <summary>Its <c>oceanGen</c> field, or null.</summary>
    private readonly FieldInfo _ocean;

    /// <summary>Its <c>climateGen</c> field.</summary>
    private readonly FieldInfo _climate;

    /// <summary>Its <c>forestGen</c> field, or null.</summary>
    private readonly FieldInfo _forest;

    /// <summary>Its <c>bushGen</c> field, or null.</summary>
    private readonly FieldInfo _bush;

    /// <summary>Built by <see cref="For"/>.</summary>
    /// <param name="owner">See <see cref="_owner"/>.</param>
    /// <param name="ocean">See <see cref="_ocean"/>.</param>
    /// <param name="climate">See <see cref="_climate"/>.</param>
    /// <param name="forest">See <see cref="_forest"/>.</param>
    /// <param name="bush">See <see cref="_bush"/>.</param>
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

    /// <summary>The ocean map layer; null when the owner has none. Setting it replaces the owner's.</summary>
    public MapLayerBase Ocean
    {
        get => Get(_ocean);
        set => Set(_ocean, value);
    }

    /// <summary>The climate map layer. Setting it replaces the owner's.</summary>
    public MapLayerBase Climate
    {
        get => Get(_climate);
        set => Set(_climate, value);
    }

    /// <summary>
    /// The forest map layer; null when the owner has none. Setting it replaces the owner's.
    /// </summary>
    public MapLayerBase Forest
    {
        get => Get(_forest);
        set => Set(_forest, value);
    }

    /// <summary>The shrub map layer; null when the owner has none. Setting it replaces the owner's.</summary>
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

    /// <summary>The named field of <paramref name="owner"/>'s type, if it holds a map layer.</summary>
    private static FieldInfo LayerField(object owner, string name)
    {
        FieldInfo field = owner.GetType().GetField(name, FieldFlags);
        return field != null && typeof(MapLayerBase).IsAssignableFrom(field.FieldType) ? field : null;
    }

    /// <summary>Reads a layer field; null when the field is missing.</summary>
    private MapLayerBase Get(FieldInfo field) => field?.GetValue(_owner) as MapLayerBase;

    /// <summary>Writes a layer field; nothing when the field is missing.</summary>
    private void Set(FieldInfo field, MapLayerBase value) => field?.SetValue(_owner, value);
}
