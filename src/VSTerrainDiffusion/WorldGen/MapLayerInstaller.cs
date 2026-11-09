using System;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Points GenMaps at model-backed climate, vegetation and ocean layers. All are plain map layers, so
/// the rest of world generation keeps reading them the way it always has.
/// </summary>
public static class MapLayerInstaller
{
    /// <summary>Seed offset of the climate layer from the world seed, as vanilla's GenMaps uses.</summary>
    private const int ClimateSeedOffset = 1;

    /// <summary>Seed offset of the forest layer.</summary>
    private const int ForestSeedOffset = 2;

    /// <summary>Seed offset of the shrub layer.</summary>
    private const int ShrubSeedOffset = 3;

    /// <summary>Seed offset of the ocean layer.</summary>
    private const int OceanSeedOffset = 1873;

    /// <summary>Installs the layers the world's settings call for.</summary>
    /// <param name="api">The server API.</param>
    /// <param name="provider">The world's terrain.</param>
    /// <param name="settings">The world's settings.</param>
    /// <returns>The forest and shrub layers installed, for the debug map; null when climate is vanilla's.</returns>
    public static (DiffusionForestMapLayer Forest, DiffusionForestMapLayer Shrubs) Install(
        ICoreServerAPI api, TerrainDiffusionProvider provider, DiffusionWorldSettings settings)
    {
        WorldMapLayers genMaps = WorldMapLayers.Resolve(api);
        if (genMaps == null)
        {
            throw DiffusionFailure.Fatal(api.Logger,
                "GenMaps is not loaded, so the model's climate and ocean maps cannot be installed.");
        }

        if (!genMaps.IsVanilla)
        {
            api.Logger.Notification("[{0}] The world's map layers belong to {1}; reading and writing those.",
                DiffusionPaths.ModId, genMaps.OwnerName);
        }

        int seed = api.WorldManager.Seed;
        InstallOcean(api, genMaps, seed, provider);

        if (settings.ClimateMode == DiffusionClimateMode.Off)
        {
            api.Logger.Notification("[{0}] Model climate disabled for this world; using vanilla climate maps.",
                DiffusionPaths.ModId);
            return (null, null);
        }

        InstallClimate(api, genMaps, seed, provider, settings);
        return InstallVegetation(genMaps, seed, provider, settings);
    }

    /// <summary>
    /// Replaces the ocean map in "output" mode, where the model invented the continents. In "input"
    /// mode the ocean map is what the terrain was conditioned on, so it already is the coastline that
    /// got generated, and overwriting it would throw away the settings - and any other mod's map - the
    /// terrain was built to honour.
    /// </summary>
    private static void InstallOcean(ICoreServerAPI api, WorldMapLayers genMaps, int seed, TerrainDiffusionProvider provider)
    {
        if (DiffusionConfig.Instance.WorldGen.OceanMap != "output") return;

        genMaps.Ocean = new DiffusionOceanMapLayer(seed + OceanSeedOffset, provider);
        api.Logger.Notification(
            "[{0}] The model decides the coastline; the world's ocean map has been replaced to match it.",
            DiffusionPaths.ModId);
    }

    /// <summary>
    /// Wraps the vanilla climate layer rather than replacing it: its geologic activity byte has nothing
    /// to do with climate and is still wanted. On re-initialisation GenMaps rebuilds the layer, but it
    /// is unwrapped defensively anyway.
    /// </summary>
    private static void InstallClimate(ICoreServerAPI api, WorldMapLayers genMaps, int seed,
        TerrainDiffusionProvider provider, DiffusionWorldSettings settings)
    {
        MapLayerBase vanillaClimate = genMaps.Climate is DiffusionClimateMapLayer alreadyWrapped
            ? alreadyWrapped.Baseline
            : genMaps.Climate;

        if (vanillaClimate == null)
        {
            throw DiffusionFailure.Fatal(api.Logger,
                "The world has no climate map layer to build the model's climate on.");
        }

        genMaps.Climate = new DiffusionClimateMapLayer(seed + ClimateSeedOffset, vanillaClimate, provider, settings);
        ReportClimate(api, settings);
    }

    /// <summary>
    /// Says what the world's climate settings do. Worth saying, because it is not what they do in
    /// vanilla: they are part of the world the model draws rather than a scaling of its numbers.
    /// </summary>
    private static void ReportClimate(ICoreServerAPI api, DiffusionWorldSettings settings)
    {
        if (!settings.Climate.IsNeutral)
        {
            api.Logger.Notification(
                "[{0}] The model is drawing a world {1}.{2}", DiffusionPaths.ModId, settings.Climate,
                IsCorrected(settings)
                    ? $" What it cannot reach is scaled onto its output afterwards " +
                      $"({settings.TemperatureCorrection:0.##}x temperature, " +
                      $"{settings.RainfallCorrection:0.##}x rainfall)."
                    : string.Empty);
        }

        api.Logger.Notification(
            settings.Latitude.IsNeutral
                ? $"[{DiffusionPaths.ModId}] Latitude bands {settings.Latitude.Status}; the model's climate " +
                  "keeps its cold places wherever it drew them."
                : $"[{DiffusionPaths.ModId}] Latitude bands {settings.Latitude.Status}. Heading north or " +
                  "south now changes the climate, and the equator sits where the world's starting " +
                  "climate put it.");
    }

    /// <summary>
    /// Wraps the forest and shrub layers. Vanilla's stay on as the source of clearings, and on
    /// re-initialisation may already be wrapped.
    /// </summary>
    private static (DiffusionForestMapLayer Forest, DiffusionForestMapLayer Shrubs) InstallVegetation(
        WorldMapLayers genMaps, int seed, TerrainDiffusionProvider provider, DiffusionWorldSettings settings)
    {
        WorldGenConfig worldGen = DiffusionConfig.Instance.WorldGen;

        MapLayerBase vanillaForest = genMaps.Forest is DiffusionForestMapLayer wrappedForest
            ? wrappedForest.Vanilla
            : genMaps.Forest;
        MapLayerBase vanillaShrubs = genMaps.Bush is DiffusionForestMapLayer wrappedShrubs
            ? wrappedShrubs.Vanilla
            : genMaps.Bush;

        var forest = new DiffusionForestMapLayer(
            seed + ForestSeedOffset, provider, TerraGenConfig.forestMapScale, false,
            settings.ForestDensity, vanillaForest, worldGen.ForestClearings);
        var shrubs = new DiffusionForestMapLayer(
            seed + ShrubSeedOffset, provider, TerraGenConfig.shrubMapScale, true,
            settings.ShrubDensity, vanillaShrubs, worldGen.ShrubClearings);
        genMaps.Forest = forest;
        genMaps.Bush = shrubs;
        return (forest, shrubs);
    }

    /// <summary>True when some of the world's climate settings had to be left to the output.</summary>
    private static bool IsCorrected(DiffusionWorldSettings settings)
        => Math.Abs(settings.TemperatureCorrection - 1f) > 0.01f
           || Math.Abs(settings.RainfallCorrection - 1f) > 0.01f;
}
