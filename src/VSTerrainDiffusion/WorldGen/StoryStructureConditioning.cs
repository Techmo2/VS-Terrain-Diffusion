using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Turns story structures' forced climates and landforms (<c>forceTemperature</c>, <c>forceRain</c>,
/// <c>requireLandform</c> in <c>storystructures.json</c>) into <see cref="CoarseConditioningSite"/>s, so
/// the model draws the climate and ground a structure asks for and blends it into its surroundings,
/// instead of vanilla overwriting the climate map in a disc.
///
/// Read after <see cref="GenStoryStructures"/> has chosen its locations, which happens in its own
/// InitWorldGenerator handler, before this mod's.
/// </summary>
public static class StoryStructureConditioning
{
    /// <summary>Vanilla fades a forced climate over this many blocks beyond the structure's radius.</summary>
    private const float VanillaTransitionBlocks = 300f;

    /// <summary>
    /// The coldest and warmest annual means the coarse model saw on land in any number (60°S–60°N).
    /// The base model reads the edited climate, so targets stay inside what it was trained on.
    /// </summary>
    private const float ColdestTrainedC = -10f, WarmestTrainedC = 30f;

    /// <summary>
    /// Median temperature seasonality (BIO4) and precipitation CV of the training set's land cells at
    /// each temperature, from the coarse training catalogue: what a place that warm or cold is usually like.
    /// </summary>
    private static readonly float[][] Typical =
    {
        new[] { -12f, 1534f, 82f }, new[] { -10f, 1753f, 77f }, new[] { -8f, 1579f, 84f }, new[] { -6f, 1353f, 69f },
        new[] { -4f, 1341f, 66f }, new[] { -2f, 1339f, 55f }, new[] { 0f, 1291f, 53f }, new[] { 2f, 1220f, 49f },
        new[] { 4f, 1109f, 36f }, new[] { 6f, 967f, 33f }, new[] { 8f, 915f, 39f }, new[] { 10f, 854f, 37f },
        new[] { 12f, 841f, 50f }, new[] { 14f, 745f, 56f }, new[] { 16f, 635f, 57f }, new[] { 18f, 545f, 59f },
        new[] { 20f, 482f, 73f }, new[] { 22f, 390f, 72f }, new[] { 24f, 203f, 72f }, new[] { 26f, 126f, 72f },
        new[] { 28f, 287f, 110f }, new[] { 30f, 386f, 142f }
    };

    /// <summary>
    /// A desert at each temperature: annual precipitation (mm), BIO4 and precipitation CV, the medians
    /// of the catalogue's BWh/BWk cells. The training set has no desert below -4 C, where dry land is
    /// tundra; there it is the driest land seen (fifth percentile) with that temperature's typical
    /// seasonality.
    /// </summary>
    private static readonly float[][] Desert =
    {
        new[] { -12f, 76f, 1534f, 82f }, new[] { -10f, 85f, 1753f, 77f }, new[] { -8f, 75f, 1579f, 84f },
        new[] { -6f, 102f, 1353f, 69f }, new[] { -4f, 60f, 942f, 110f }, new[] { -2f, 83f, 929f, 108f },
        new[] { 0f, 101f, 1209f, 105f }, new[] { 2f, 104f, 1343f, 106f }, new[] { 4f, 107f, 1328f, 105f },
        new[] { 6f, 92f, 1272f, 100f }, new[] { 8f, 117f, 1247f, 87f }, new[] { 10f, 119f, 1269f, 52f },
        new[] { 12f, 73f, 1230f, 65f }, new[] { 14f, 125f, 1016f, 59f }, new[] { 16f, 139f, 806f, 71f },
        new[] { 18f, 164f, 581f, 66f }, new[] { 20f, 146f, 605f, 65f }, new[] { 22f, 76f, 667f, 53f },
        new[] { 24f, 74f, 655f, 68f }, new[] { 26f, 89f, 603f, 95f }, new[] { 28f, 84f, 541f, 118f },
        new[] { 30f, 146f, 405f, 142f }
    };

    public static List<CoarseConditioningSite> Read(ICoreServerAPI api, DiffusionWorldSettings settings, ILogger logger)
    {
        var sites = new List<CoarseConditioningSite>();
        GenStoryStructures story = api.ModLoader.GetModSystem<GenStoryStructures>();
        WorldGenStoryStructure[] configs = story?.scfg?.Structures;
        if (configs == null || story.Structures == null) return sites;

        RainfallScale rainfall = RainfallScale.FromConfig(DiffusionConfig.Instance.WorldGen);
        var forcedCentres = new HashSet<(int X, int Z)>();
        foreach (KeyValuePair<string, StoryStructureLocation> entry in story.Structures)
        {
            WorldGenStoryStructure config = configs.FirstOrDefault(c => c.Code == entry.Key);
            StoryStructureLocation location = entry.Value;
            if (config == null || location?.CenterPos == null) continue;

            bool climate = config.ForceTemperature != null || config.ForceRain != null;
            WorldGenConfig worldGen = DiffusionConfig.Instance.WorldGen;
            bool flatGround = FlattensGround(config.RequireLandform);
            bool flatten = worldGen.StructureFlatten && flatGround;
            bool basin = worldGen.StructureBasinDepth > 0f && flatGround;
            if (!climate && !flatten && !basin) continue;

            (float i, float j, float perBlock) = TerrainDiffusionProvider.CoarseModelPixel(
                settings, location.CenterPos.X, location.CenterPos.Z);
            var site = new CoarseConditioningSite
            {
                I = i,
                J = j,
                // At least the coarse pixel the structure stands in, however small its radius.
                Radius = Math.Max(0.75f, location.LandformRadius * perBlock),
                Fade = Math.Max(1f, VanillaTransitionBlocks * perBlock),
                Flatten = flatten,
                BasinDepth = basin ? Math.Clamp(worldGen.StructureBasinDepth, 0f, 1f) : null,
                ElevationNoise = basin && worldGen.StructureElevationNoise > 0f ? worldGen.StructureElevationNoise : null,
                Label = entry.Key
            };

            float targetC = 10f;
            if (config.ForceTemperature is int forced)
            {
                // What the game should show there, so the world's own temperature settings, which
                // apply after the model, are taken off first; then kept to what the base model knows.
                targetC = Math.Clamp(ModelTemperatureFor(settings, forced, location.CenterPos.Z),
                    ColdestTrainedC, WarmestTrainedC);
                float[] typical = At(Typical, targetC);
                site.TemperatureC = targetC;
                site.Seasonality = typical[1];
                site.PrecipitationCv = typical[2];
            }

            // Vanilla writes rain 0 where a structure gives a temperature alone, so that, like an
            // explicit 0, makes a desert of that temperature.
            int? rain = config.ForceRain ?? (config.ForceTemperature != null ? 0 : null);
            if (rain == 0)
            {
                float[] desert = At(Desert, targetC);
                site.PrecipitationMm = desert[1];
                site.Seasonality = desert[2];
                site.PrecipitationCv = desert[3];
            }
            else if (rain is int rainByte)
            {
                site.PrecipitationMm = PrecipitationFor(rainfall, rainByte, targetC, site.Seasonality ?? 854f, site.PrecipitationCv ?? 37f);
            }

            sites.Add(site);

            // Vanilla puts a "surface" structure at sea level plus its offset, which only meets the
            // ground on vanilla's own flat landform. The basin makes the ground level but not sea
            // level, so place it on the ground there instead, as vanilla's own useWorldgenHeight does.
            if (basin && config.Placement == EnumStructurePlacement.Surface && !config.UseWorldgenHeight)
            {
                config.UseWorldgenHeight = true;
                logger.Notification("[{0}] Story structure '{1}' is placed on the ground rather than at sea level.",
                    DiffusionPaths.ModId, entry.Key);
            }
            if (climate) forcedCentres.Add((location.CenterPos.X, location.CenterPos.Z));
            logger.Notification(
                "[{0}] Story structure '{1}' at ({2}, {3}): {4}{5}{6} within {7} blocks, fading over {8}.",
                DiffusionPaths.ModId, entry.Key, location.CenterPos.X, location.CenterPos.Z,
                site.TemperatureC is float t ? $"{t:0.#} C (forced {config.ForceTemperature} C) " : "",
                site.PrecipitationMm is float p ? $"{p:0} mm (rain {rain}) " : "",
                (flatten ? $"flat relief ({config.RequireLandform}) " : "") + (basin ? $"basin {worldGen.StructureBasinDepth} ({config.RequireLandform})" : ""),
                (int)(site.Radius / perBlock), (int)(site.Fade / perBlock));
        }
        RemoveVanillaForcing(api, forcedCentres, logger);
        return sites;
    }

    /// <summary>
    /// Takes the structures handled here out of vanilla's forced climates, which would otherwise
    /// overwrite the climate map in a hard-edged disc on top of the model's. Everything that reads the
    /// climate map - block layers, vegetation, weather, seasons - then sees the model's climate.
    /// A structure moved later by command is registered with vanilla afresh and keeps its forcing.
    /// </summary>
    private static void RemoveVanillaForcing(ICoreServerAPI api, HashSet<(int X, int Z)> centres, ILogger logger)
    {
        if (centres.Count == 0) return;
        GenMaps genMaps = api.ModLoader.GetModSystem<GenMaps>();
        if (AccessTools.Field(typeof(GenMaps), "forceClimate")?.GetValue(genMaps) is not List<ForceClimate> forced)
        {
            logger.Warning("[{0}] Could not reach vanilla's forced climates; story structures will get vanilla's climate disc on top of the model's.",
                DiffusionPaths.ModId);
            return;
        }
        int removed = forced.RemoveAll(f => f.CenterPos != null && centres.Contains((f.CenterPos.X, f.CenterPos.Z)));
        logger.Notification("[{0}] Removed {1} of vanilla's forced story structure climates; the model's climate applies there instead.",
            DiffusionPaths.ModId, removed);
    }

    /// <summary>Landforms whose point is a flat site: vanilla's flats and the glacier plateau.</summary>
    private static bool FlattensGround(string landform) =>
        landform != null && (landform.Contains("flat", StringComparison.OrdinalIgnoreCase)
                             || landform.Contains("glacier", StringComparison.OrdinalIgnoreCase));

    /// <summary>The model temperature that the world's temperature settings turn into <paramref name="worldC"/>.</summary>
    private static float ModelTemperatureFor(DiffusionWorldSettings settings, float worldC, int blockZ)
    {
        float lo = -60f, hi = 60f;
        for (int k = 0; k < 40; k++)
        {
            float mid = (lo + hi) / 2f;
            if (settings.WorldTemperature(mid, blockZ) < worldC) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2f;
    }

    /// <summary>A row of a table keyed by temperature in its first column, interpolated linearly and held at the ends.</summary>
    private static float[] At(float[][] table, float tempC)
    {
        if (tempC <= table[0][0]) return table[0];
        for (int k = 1; k < table.Length; k++)
        {
            if (tempC > table[k][0]) continue;
            float f = (tempC - table[k - 1][0]) / (table[k][0] - table[k - 1][0]);
            return table[k].Select((v, c) => table[k - 1][c] + f * (v - table[k - 1][c])).ToArray();
        }
        return table[^1];
    }

    /// <summary>The annual rainfall that the world's rainfall mapping turns into the given 0-255 rain byte.</summary>
    private static float PrecipitationFor(RainfallScale rainfall, int rainByte, float tempC, float seasonality, float cv)
    {
        for (float mm = 1f; mm < 8000f; mm *= 1.02f)
            if (rainfall.ToRainfall(new Bioclim(tempC, seasonality, mm, cv)) >= rainByte) return mm;
        return 8000f;
    }
}
