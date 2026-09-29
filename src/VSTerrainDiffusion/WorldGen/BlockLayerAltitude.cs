using System.Collections.Generic;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Puts vanilla's surface block layers back at the altitudes they were written for.
///
/// Every band in <c>blocklayers.json</c>, and the lake and ocean beds with it, is a fraction of
/// the world height: bare mountain gravel starts at 0.66, sand stops at 0.7, topsoil stops at
/// 0.91. That only means a height above the sea while the sea sits where vanilla puts it and a
/// block of height is about a metre of altitude. Neither need hold here.
///
/// So each fraction goes through <see cref="HeightFrame.VanillaFractionToFraction"/>: to the same
/// number of blocks above the sea as in a vanilla world with this much room above its sea, and
/// below the sea to the same share of its depth. With exaggerated terrain the blocks above the
/// sea are then moved through the same mapping the terrain was, which keeps each band at the
/// real-world elevation it describes - a world stretched 4x has no real mountains, and the
/// bare-rock bands rise out of it accordingly.
/// </summary>
public static class BlockLayerAltitude
{
    /// <summary>Original thresholds, so re-initialising the world generator does not compound.</summary>
    private static readonly Dictionary<object, (float MinY, float MaxY)> Originals = new();

    /// <summary>
    /// Moves every band for this world, always from the original values; with nothing to move it
    /// puts back whatever the last world moved.
    /// </summary>
    /// <param name="exaggerated">Also follow the terrain's vertical scale, not only its sea level.</param>
    public static void Apply(ICoreServerAPI api, DiffusionWorldSettings settings, bool exaggerated)
    {
        BlockLayerConfig config = BlockLayerConfig.GetInstance(api);
        if (config == null || settings.MapSizeY <= 0) return;

        HeightFrame heights = settings.Heights;
        System.Func<double, double> rescale = exaggerated ? above => settings.RescaleLayerHeight((float)above) : null;
        float Move(float fraction) => heights.VanillaFractionToFraction(fraction, rescale);

        int changed = 0;
        foreach (BlockLayer layer in config.Blocklayers ?? System.Array.Empty<BlockLayer>())
        {
            if (layer == null) continue;
            if (Set(layer, layer.MinY, layer.MaxY, Move, (min, max) => { layer.MinY = min; layer.MaxY = max; })) changed++;

            foreach (BlockLayerCodeByMin entry in layer.BlockCodeByMin ?? System.Array.Empty<BlockLayerCodeByMin>())
            {
                if (entry != null) Set(entry, entry.MinY, entry.MaxY, Move, (min, max) => { entry.MinY = min; entry.MaxY = max; });
            }
        }

        foreach (LakeBedLayerProperties bed in new[] { config.LakeBedLayer, config.OceanBedLayer })
        {
            foreach (LakeBedBlockCodeByMin entry in bed?.BlockCodeByMin ?? System.Array.Empty<LakeBedBlockCodeByMin>())
            {
                if (entry != null && Set(entry, entry.MinY, entry.MaxY, Move, (min, max) => { entry.MinY = min; entry.MaxY = max; })) changed++;
            }
        }

        if (changed > 0)
        {
            api.Logger.Notification(
                "[{0}] Moved the altitude bands of {1} surface and bed layers to this world ({2}; {3}), so they " +
                "keep their height above the sea.", DiffusionPaths.ModId, changed, heights, settings.DescribeHeight());
        }
    }

    /// <summary>Sets one band from its original, recording the original on first sight. True if it moved.</summary>
    private static bool Set(object band, float minY, float maxY, System.Func<float, float> move,
                            System.Action<float, float> assign)
    {
        if (!Originals.TryGetValue(band, out (float MinY, float MaxY) original))
        {
            original = (minY, maxY);
            Originals[band] = original;
        }

        float min = move(original.MinY), max = move(original.MaxY);
        assign(min, max);
        return min != original.MinY || max != original.MaxY;
    }
}
