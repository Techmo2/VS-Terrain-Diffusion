using System;
using Vintagestory.API.Common;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Conditions the diffusion model on Vintage Story's own ocean map, so the world's "Land cover" and
/// "Ocean scale" settings decide where the sea goes and the model only decides what the coastline,
/// the shelf and the mountains behind them look like.
///
/// It reads <see cref="GenMaps.oceanGen"/> rather than any particular implementation of it, which
/// is the whole point: a mod that installs its own ocean map - Continental World, say - is honoured
/// on exactly the same terms as vanilla, with no knowledge of it here. The layer is a pure query
/// over world coordinates, so it can be sampled far outside the region currently being generated,
/// which is what the coarse stage needs.
///
/// Resolution is the limit worth knowing about. One coarse conditioning pixel spans 256 model
/// pixels, which is 512 blocks at the default diffusion resolution, so an ocean much smaller than
/// that cannot be expressed in the conditioning at all and the model will fill it in as land.
/// </summary>
public sealed class OceanMapLandmask : ILandmaskSource
{
    /// <summary>
    /// Longest side, in ocean-map pixels, of a single query. Four megabytes of ints, which is the
    /// whole of a coarse tile at the default resolution and a handful of queries at the finest.
    /// </summary>
    private const int MaxQuerySidePixels = 1024;

    private readonly Func<MapLayerBase> _resolveLayer;
    private readonly ILogger _logger;

    /// <summary>Ocean-map pixels spanned by one coarse conditioning pixel, along each axis.</summary>
    private readonly int _oceanPixelsPerCoarse;

    private readonly int _originOceanPixelX;
    private readonly int _originOceanPixelZ;

    /// <summary>Map layers keep mutable noise state, so only one thread may be inside one.</summary>
    private readonly object _gate = new();

    private MapLayerBase _layer;
    private bool _warned;
    private bool _disabled;

    /// <param name="resolveLayer">
    /// Produces the ocean map to read. Called late and only until it answers, so that a mod which
    /// installs its own layer after this mod initialises is still the one that gets honoured.
    /// </param>
    public OceanMapLandmask(Func<MapLayerBase> resolveLayer, DiffusionWorldSettings settings, ILogger logger)
    {
        _resolveLayer = resolveLayer;
        _logger = logger;

        // A coarse pixel is 256 model pixels across and a model pixel is Scale blocks, so it covers
        // 256 * Scale blocks - always a whole number of 32-block ocean-map pixels. The model origin
        // is rounded to a chunk boundary, so the two grids line up exactly and no rounding is
        // needed anywhere below.
        int blocksPerCoarse = 32 * WorldPipelineModelConfig.Instance.LatentCompression * settings.Scale;
        _oceanPixelsPerCoarse = Math.Max(1, blocksPerCoarse / TerraGenConfig.oceanMapScale);
        _originOceanPixelX = settings.OriginBlockX / TerraGenConfig.oceanMapScale;
        _originOceanPixelZ = settings.OriginBlockZ / TerraGenConfig.oceanMapScale;
    }

    public float[] SeaFraction(int x1, int y1, int x2, int y2)
    {
        MapLayerBase layer = ResolveLayer();
        if (layer == null) return null;

        int w = x2 - x1, h = y2 - y1;
        if (w <= 0 || h <= 0) return null;

        int pixels = _oceanPixelsPerCoarse;

        // Queries are square, and cover whole coarse pixels. Square because a map layer is well
        // within its rights to assume the shape worldgen actually asks it for, which is always a
        // square region - ContinentalWorld's blur, for one, indexes its rows by the width and
        // walks off the end of a strip - and whole pixels because it makes the averaging below a
        // straight walk with no bounds arithmetic in it.
        int perQuery = Math.Clamp(MaxQuerySidePixels / pixels, 1, Math.Max(w, h));
        int side = perQuery * pixels;

        int firstPixelX = _originOceanPixelX + x1 * pixels;
        int firstPixelZ = _originOceanPixelZ + y1 * pixels;

        // Vanilla's layer is a hard 0 or 255; averaging is what turns that into a coastline the
        // conditioning can express, and it keeps a layer that already returns intermediate values
        // intact.
        float perPixel = 1f / (255f * pixels * pixels);
        var totals = new float[w * h];

        lock (_gate)
        {
            try
            {
                for (int cz = 0; cz < h; cz += perQuery)
                {
                    for (int cx = 0; cx < w; cx += perQuery)
                    {
                        int[] map = layer.GenLayer(
                            firstPixelX + cx * pixels, firstPixelZ + cz * pixels, side, side);

                        // The last query along either axis overruns the window; the extra cells are
                        // simply not read.
                        int rows = Math.Min(perQuery, h - cz);
                        int columns = Math.Min(perQuery, w - cx);

                        for (int dz = 0; dz < rows; dz++)
                        {
                            for (int dx = 0; dx < columns; dx++)
                            {
                                long sum = 0;
                                for (int pz = 0; pz < pixels; pz++)
                                {
                                    int start = (dz * pixels + pz) * side + dx * pixels;
                                    for (int px = 0; px < pixels; px++) sum += map[start + px];
                                }
                                totals[(cz + dz) * w + cx + dx] =
                                    Math.Clamp(sum * perPixel, 0f, 1f);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Disable(layer, side, e);
                return null;
            }
        }

        return totals;
    }

    /// <summary>
    /// Gives up on an ocean map that will not answer, for the rest of the session.
    ///
    /// The alternative is letting the exception out, and it does not stay a worldgen error: it
    /// comes back up through the chunk thread and takes the server down with it. A world whose
    /// coastlines are the model's own is a worse world than the player asked for, but it is a
    /// world.
    /// </summary>
    private void Disable(MapLayerBase layer, int side, Exception e)
    {
        _disabled = true;
        _layer = null;
        _logger.Error(
            "[{0}] The world's ocean map ({1}) failed on a {2}x{2} pixel query, so the world's land " +
            "cover and ocean scale settings cannot be honoured; the model will decide where the " +
            "continents go instead. This is a fault in whichever mod supplies that layer - worldgen " +
            "only ever asks one for a square region, and so does this. {3}",
            DiffusionPaths.ModId, layer.GetType().Name, side, e);
    }

    /// <summary>
    /// Finds the ocean layer, late and once. Late because another mod may install its own after we
    /// initialise, and the last one to claim the field is the one the rest of world generation will
    /// read; once because the answer is then fixed for the life of the world, and a landmask that
    /// changed halfway through would leave the terrain disagreeing with itself.
    /// </summary>
    private MapLayerBase ResolveLayer()
    {
        if (_layer != null) return _layer;
        if (_disabled) return null;

        lock (_gate)
        {
            if (_layer != null) return _layer;
            if (_disabled) return null;

            MapLayerBase found = _resolveLayer();
            if (found == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    _logger.Warning(
                        "[{0}] No ocean map is installed, so the world's land cover and ocean scale " +
                        "settings cannot be honoured; the model will decide where the continents go.",
                        DiffusionPaths.ModId);
                }
                return null;
            }

            _layer = found;
            _logger.Notification("[{0}] Conditioning terrain on the world's ocean map ({1}).",
                DiffusionPaths.ModId, found.GetType().Name);
            return _layer;
        }
    }
}
