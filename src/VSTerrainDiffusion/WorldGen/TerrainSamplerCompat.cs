using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Answers Algernon's Terrain Sampler from the model.
///
/// The sampler predicts a column's height for other mods without generating it, by running its own
/// copy of vanilla's terrain noise - so in a world this mod generates, every height it gave was
/// vanilla's landscape, not the one being built. The only generator it knows to defer to is
/// Watersheds. Its climate, rainfall and vegetation already come out right, because it reads them
/// from the world's map layers, which this mod replaces.
///
/// So only the height is taken over, at the two places the sampler computes one:
/// <c>TerrainSamplerGenTerra.GetBlockColumnHeight</c>, and <c>SampleColumn</c> when it has not been
/// handed a height already. The answer is <see cref="TerrainDiffusionProvider.SampleSurfaceY"/>,
/// from the terrain tile or, with <c>terrainSamplerHeight: "coarse"</c>, from the coarse model.
/// With Watersheds installed the sampler asks it first, and this mod answers that already.
///
/// A full answer builds the model's terrain tile around the position, which is a model run for any
/// ground not generated yet, and the caller waits for it.
/// </summary>
public static class TerrainSamplerCompat
{
    private const string SamplerModId = "algernonsterrainsampler";
    private const string HarmonyId = "vsterraindiffusion.terrainsampler";

    private static Harmony _harmony;
    private static TerrainDiffusionProvider _provider;
    private static PropertyInfo _x, _z;

    /// <summary>Whether the sampler's heights are coming from the model.</summary>
    public static bool Installed => _harmony != null && _provider != null;

    public static void Install(ICoreServerAPI api, TerrainDiffusionProvider provider)
    {
        if (!api.ModLoader.IsModEnabled(SamplerModId)) return;

        // The provider is per world; the patches are per process.
        _provider = provider;
        if (_harmony != null) return;

        try
        {
            Type genTerra = AccessTools.TypeByName("AlgernonsTerrainSampler.TerrainSamplerGenTerra");
            Type coordinate = AccessTools.TypeByName("AlgernonsTerrainSampler.WorldMapCoordinate");
            MethodInfo height = genTerra == null || coordinate == null
                ? null
                : AccessTools.Method(genTerra, "GetBlockColumnHeight", new[] { coordinate });
            MethodInfo sample = genTerra == null || coordinate == null
                ? null
                : AccessTools.Method(genTerra, "SampleColumn", new[] { coordinate, typeof(int?) });
            _x = coordinate == null ? null : AccessTools.Property(coordinate, "X");
            _z = coordinate == null ? null : AccessTools.Property(coordinate, "Z");
            if (height == null || sample == null || _x == null || _z == null)
                throw new MissingMethodException("TerrainSamplerGenTerra.GetBlockColumnHeight or SampleColumn");

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(height, prefix: new HarmonyMethod(typeof(TerrainSamplerCompat), nameof(BeforeGetBlockColumnHeight)));
            _harmony.Patch(sample, prefix: new HarmonyMethod(typeof(TerrainSamplerCompat), nameof(BeforeSampleColumn)));

            api.Logger.Notification(
                "[{0}] Algernon's Terrain Sampler is installed; its heights now come from the model ({1}).",
                DiffusionPaths.ModId, DiffusionConfig.Instance.TerrainSamplerHeight);
        }
        catch (Exception e)
        {
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;
            api.Logger.Warning(
                "[{0}] Algernon's Terrain Sampler could not be patched, so it will keep reporting vanilla terrain " +
                "heights rather than this world's: {1}", DiffusionPaths.ModId, e.Message);
        }
    }

    public static void Uninstall()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        _provider = null;
    }

    private static int Height(object coordinate) =>
        _provider.SampleSurfaceY((int)_x.GetValue(coordinate), (int)_z.GetValue(coordinate),
            DiffusionConfig.Instance.TerrainSamplerHeight == "coarse");

    /// <summary>Harmony prefix: the whole answer, so the sampler's vanilla noise never runs.</summary>
    private static bool BeforeGetBlockColumnHeight(object[] __args, ref int __result)
    {
        if (_provider == null) return true;
        __result = Height(__args[0]);
        return false;
    }

    /// <summary>
    /// Harmony prefix: hands <c>SampleColumn</c> the height as if the caller had overridden it,
    /// which is the sampler's own path for another mod's terrain. It then reads climate and
    /// vegetation at that height as usual, and skips its vanilla sea-level adjustment.
    /// </summary>
    private static void BeforeSampleColumn(object[] __args)
    {
        if (_provider == null || __args[1] != null) return;
        __args[1] = (int?)Height(__args[0]);
    }
}
