using System.Collections.Generic;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Realigns vanilla's surface block layers with real-world elevation.
///
/// Every band in <c>blocklayers.json</c> is expressed as a fraction of the world height:
/// bare mountain gravel starts at 0.66, sand stops at 0.7, topsoil stops at 0.91. Those numbers
/// encode an assumption that a block of height is about a metre of altitude, which is true in
/// vanilla and false here — a world calibrated to 4x stretch puts a 200 m hill at the height
/// vanilla reserves for bare alpine rock, and the hill comes out as a gravel heap.
///
/// So the bands are moved through the same mapping the terrain was, which keeps each one at the
/// real-world elevation it was written for. At 1x nothing changes; the more the terrain is
/// exaggerated, the further up the bare-rock bands move, until in a heavily stretched world they
/// stop applying at all — correctly, because such a world contains no real mountains.
/// </summary>
public static class BlockLayerAltitude
{
    /// <summary>Original thresholds, so re-initialising the world generator does not compound.</summary>
    private static readonly Dictionary<BlockLayer, (float MinY, float MaxY)> LayerOriginals = new();

    private static readonly Dictionary<BlockLayerCodeByMin, (float MinY, float MaxY)> EntryOriginals = new();

    /// <summary>Applies the stretch. Safe to call repeatedly; it always works from the original values.</summary>
    public static void Apply(ICoreServerAPI api, DiffusionWorldSettings settings)
    {
        BlockLayerConfig config = BlockLayerConfig.GetInstance(api);
        if (config?.Blocklayers == null) return;

        int mapHeight = settings.MapSizeY;
        int seaLevel = settings.SeaLevel;
        if (mapHeight <= 0) return;

        int changed = 0;

        foreach (BlockLayer layer in config.Blocklayers)
        {
            if (layer == null) continue;

            if (!LayerOriginals.TryGetValue(layer, out (float MinY, float MaxY) original))
            {
                original = (layer.MinY, layer.MaxY);
                LayerOriginals[layer] = original;
            }

            layer.MinY = Stretch(original.MinY, mapHeight, seaLevel, settings);
            layer.MaxY = Stretch(original.MaxY, mapHeight, seaLevel, settings);
            if (layer.MinY != original.MinY || layer.MaxY != original.MaxY) changed++;

            if (layer.BlockCodeByMin == null) continue;
            foreach (BlockLayerCodeByMin entry in layer.BlockCodeByMin)
            {
                if (entry == null) continue;

                if (!EntryOriginals.TryGetValue(entry, out (float MinY, float MaxY) entryOriginal))
                {
                    entryOriginal = (entry.MinY, entry.MaxY);
                    EntryOriginals[entry] = entryOriginal;
                }

                entry.MinY = Stretch(entryOriginal.MinY, mapHeight, seaLevel, settings);
                entry.MaxY = Stretch(entryOriginal.MaxY, mapHeight, seaLevel, settings);
            }
        }

        if (changed > 0)
        {
            api.Logger.Notification(
                "[{0}] Moved the altitude bands of {1} surface block layers to match the terrain height " +
                "({2}), so hills are not treated as bare mountaintops.",
                DiffusionPaths.ModId, changed, settings.DescribeHeight());
        }
    }

    /// <summary>
    /// Moves a threshold expressed as a fraction of world height so that it stays at the same
    /// real-world elevation once terrain has been stretched. Depths below sea level are left
    /// alone: the sea floor has its own mapping and none of these bands describe it.
    /// </summary>
    private static float Stretch(float fraction, int mapHeight, int seaLevel, DiffusionWorldSettings settings)
    {
        float aboveSea = fraction * mapHeight - seaLevel;
        if (aboveSea <= 0f) return fraction;

        float stretched = (seaLevel + settings.RescaleLayerHeight(aboveSea)) / mapHeight;
        return stretched > 1f ? 1f : stretched;
    }
}
