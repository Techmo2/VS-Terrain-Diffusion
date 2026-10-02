using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Moves each tree and shrub's altitude band to this world, the way <see cref="BlockLayerAltitude"/>
/// moves the soil bands. Vanilla picks a species partly on <c>y / MapSizeY</c> against a band from
/// <c>treengenproperties.json</c> written as fractions of the world height, which with the sea
/// moved, or the terrain stretched, no longer means the height above the sea the band was
/// written for.
///
/// The bands live on <c>WgenTreeSupplier</c>, which loads them afresh each time world generation
/// initialises, so a postfix on its loader rewrites them as they arrive. That happens in vanilla's
/// own world generator set-up, which may run before this mod's, so the patch goes in when the
/// server starts; until this world's settings exist the bands follow the sea level alone, and
/// every supplier seen is moved again once they do.
/// </summary>
public static class TreeHeightFrame
{
    private const string HarmonyId = "vsterraindiffusion.treeheights";

    private static Harmony _harmony;
    private static FieldInfo _properties;
    private static DiffusionWorldSettings _settings;
    private static bool _exaggerated;

    private static readonly ConditionalWeakTable<WgenTreeSupplier, object> Suppliers = new();
    private static readonly ConditionalWeakTable<TreeVariant, float[]> Originals = new();

    /// <summary>Sets this world's frame and moves the bands of every supplier already loaded.</summary>
    public static void Use(DiffusionWorldSettings settings, bool exaggerated, ILogger logger)
    {
        _settings = settings;
        _exaggerated = exaggerated;
        Install();

        int moved = 0;
        foreach (KeyValuePair<WgenTreeSupplier, object> supplier in Suppliers) moved += Move(supplier.Key);
        if (moved > 0)
        {
            logger.Notification("[{0}] Moved the altitude bands of {1} tree and shrub types to this world ({2}).",
                DiffusionPaths.ModId, moved, settings.Heights);
        }
    }

    /// <summary>Patches the loader. Called as the server starts, before any world generator is set up.</summary>
    public static void Install()
    {
        if (_harmony != null) return;
        MethodInfo load = AccessTools.Method(typeof(WgenTreeSupplier), "LoadTrees");
        _properties = AccessTools.Field(typeof(WgenTreeSupplier), "treeGenProps");
        if (load == null || _properties == null)
            throw new MissingMemberException("WgenTreeSupplier.LoadTrees or treeGenProps");

        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(load, postfix: new HarmonyMethod(typeof(TreeHeightFrame), nameof(AfterLoadTrees)));
    }

    public static void Uninstall()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        _settings = null;
    }

    private static void AfterLoadTrees(WgenTreeSupplier __instance)
    {
        Suppliers.AddOrUpdate(__instance, null);
        Move(__instance);
    }

    /// <summary>Moves one supplier's bands from their originals. Returns how many moved.</summary>
    private static int Move(WgenTreeSupplier supplier)
    {
        if (_properties?.GetValue(supplier) is not TreeGenProperties properties) return 0;

        DiffusionWorldSettings settings = _settings;
        HeightFrame? frame = settings?.Heights ?? SeaLevelOverride.Current;
        if (frame is not HeightFrame heights) return 0;
        int moved = 0;
        System.Func<double, double> rescale = settings != null && _exaggerated
            ? above => settings.RescaleLayerHeight((float)above)
            : null;

        foreach (TreeVariant[] variants in new[] { properties.TreeGens, properties.ShrubGens })
        {
            foreach (TreeVariant variant in variants ?? Array.Empty<TreeVariant>())
            {
                if (variant == null) continue;
                float[] original = Originals.GetValue(variant, v => new[] { v.MinHeight, v.MaxHeight });
                float min = heights.VanillaFractionToFraction(original[0], rescale);
                float max = heights.VanillaFractionToFraction(original[1], rescale);

                // As vanilla derives them when the file is read, including a zero range meaning any height.
                variant.HeightMid = (min + max) / 2;
                variant.HeightRange = (max - min) / 2;
                if (variant.HeightRange == 0) variant.HeightRange = 1;
                if (min != original[0] || max != original[1]) moved++;
            }
        }
        return moved;
    }
}
