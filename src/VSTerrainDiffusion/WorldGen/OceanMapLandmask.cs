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
///
/// Cost is the other. A conditioning pixel is <c>8 * Scale</c> ocean-map pixels across, and every
/// one of them is a few simplex noise evaluations, so reading them all costs 0.4 s a coarse tile at
/// scale 2 and 4 s at scale 6 - most of a new world's spawn search. Past
/// <see cref="DefaultSamplesPerAxis"/> pixels across, a world created now reads a lattice of that
/// many points per axis instead: 0.3 s a tile at any scale, and within 0.01 of the full average
/// (0.03 at the 99th percentile).
/// </summary>
public sealed class OceanMapLandmask : ILandmaskSource
{
    /// <summary>
    /// Longest side, in ocean-map pixels, of a single query. Four megabytes of ints, which is the
    /// whole of a coarse tile at the default resolution and a handful of queries at the finest.
    /// </summary>
    private const int MaxQuerySidePixels = 1024;

    /// <summary>Points read along each axis of a conditioning pixel by worlds created with sampling.</summary>
    public const int DefaultSamplesPerAxis = 16;

    private readonly Func<MapLayerBase> _resolveLayer;
    private readonly ILogger _logger;

    /// <summary>Ocean-map pixels spanned by one coarse conditioning pixel, along each axis.</summary>
    private readonly int _oceanPixelsPerCoarse;

    /// <summary>Points read along each axis of a conditioning pixel, or 0 to read every ocean-map pixel.</summary>
    private readonly int _samplesPerAxis;

    private readonly int _originOceanPixelX;
    private readonly int _originOceanPixelZ;

    /// <summary>Map layers keep mutable noise state, so only one thread may be inside one.</summary>
    private readonly object _gate = new();

    private MapLayerBase _layer;

    /// <param name="resolveLayer">
    /// Produces the ocean map to read. Called late and only until it answers, so that a mod which
    /// installs its own layer after this mod initialises is still the one that gets honoured.
    /// </param>
    /// <param name="samplesPerAxis">
    /// Points to read along each axis of a conditioning pixel wider than that, or 0 for every
    /// ocean-map pixel. Fixed per world: it moves the coastline slightly.
    /// </param>
    public OceanMapLandmask(Func<MapLayerBase> resolveLayer, DiffusionWorldSettings settings, int samplesPerAxis,
                            ILogger logger)
    {
        _resolveLayer = resolveLayer;
        _logger = logger;

        // A conditioning pixel is 256 model pixels across, divided by any coarse pooling, and a
        // model pixel is Scale blocks - a whole number of 32-block ocean-map pixels for every
        // setting the world offers. The model origin is rounded to a chunk boundary, so the two
        // grids line up exactly and no rounding is needed anywhere below.
        int blocksPerCoarse = settings.BlocksPerConditioningPixel;
        _oceanPixelsPerCoarse = Math.Max(1, blocksPerCoarse / TerraGenConfig.oceanMapScale);
        _originOceanPixelX = settings.OriginBlockX / TerraGenConfig.oceanMapScale;
        _originOceanPixelZ = settings.OriginBlockZ / TerraGenConfig.oceanMapScale;
        _samplesPerAxis = samplesPerAxis > 0 && samplesPerAxis < _oceanPixelsPerCoarse ? samplesPerAxis : 0;
    }

    public float[] SeaFraction(int x1, int y1, int x2, int y2)
    {
        MapLayerBase layer = ResolveLayer();
        if (layer == null) return null;

        int w = x2 - x1, h = y2 - y1;
        if (w <= 0 || h <= 0) return null;

        if (_samplesPerAxis > 0) return SampledSeaFraction(layer, x1, y1, w, h);

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
    /// The sea fraction from a lattice of single-pixel queries, one at the middle of each of
    /// <see cref="_samplesPerAxis"/> squared sub-cells of a conditioning pixel. Single points spread
    /// across the pixel come much closer to the full average than the same number of pixels read
    /// as a few small squares, because the coast crosses more of them. A one-pixel query is still
    /// square, and vanilla's blur passes it through unblurred, which averages out the same.
    /// </summary>
    private float[] SampledSeaFraction(MapLayerBase layer, int x1, int y1, int w, int h)
    {
        int pixels = _oceanPixelsPerCoarse, n = _samplesPerAxis;
        var offsets = new int[n];
        for (int s = 0; s < n; s++) offsets[s] = (2 * s + 1) * pixels / (2 * n);

        float perSample = 1f / (255f * n * n);
        var totals = new float[w * h];

        lock (_gate)
        {
            try
            {
                for (int cz = 0; cz < h; cz++)
                {
                    int pixelZ = _originOceanPixelZ + (y1 + cz) * pixels;
                    for (int cx = 0; cx < w; cx++)
                    {
                        int pixelX = _originOceanPixelX + (x1 + cx) * pixels;
                        long sum = 0;
                        foreach (int dz in offsets)
                        {
                            foreach (int dx in offsets) sum += layer.GenLayer(pixelX + dx, pixelZ + dz, 1, 1)[0];
                        }
                        totals[cz * w + cx] = Math.Clamp(sum * perSample, 0f, 1f);
                    }
                }
            }
            catch (Exception e)
            {
                Disable(layer, 1, e);
                return null;
            }
        }

        return totals;
    }

    /// <summary>
    /// Reports an ocean map that will not answer, and stops the game.
    ///
    /// Carrying on without it is not an option even though it sounds like the gentler one: the
    /// landmask is what the coarse stage is conditioned on, so a world that loses it mid-generation
    /// grows continents in different places from the ones already on disk.
    /// </summary>
    private void Disable(MapLayerBase layer, int side, Exception e)
    {
        throw DiffusionFailure.Fatal(_logger,
            $"The world's ocean map ({layer.GetType().Name}) failed on a {side}x{side} pixel query. " +
            "That is a fault in the mod supplying the layer; worldgen only ever asks for a square.", e);
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

        lock (_gate)
        {
            if (_layer != null) return _layer;

            MapLayerBase found = _resolveLayer()
                ?? throw DiffusionFailure.Fatal(_logger,
                    "No ocean map is installed, but worldGen.oceanMap is \"input\". Set it to " +
                    "\"output\" to let the model decide the coastline.");

            _layer = found;
            _logger.Notification("[{0}] Conditioning terrain on the world's ocean map ({1}).",
                DiffusionPaths.ModId, found.GetType().Name);
            return _layer;
        }
    }
}
