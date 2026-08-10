using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;
using VSTerrainDiffusion.Tensors;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// One square of generated terrain, aligned to the tile grid. Rows run along Z, columns along X.
/// </summary>
public sealed class TerrainTile
{
    public readonly int BlockX;
    public readonly int BlockZ;
    public readonly int Size;

    /// <summary>Elevation in metres, including the small-scale slope noise.</summary>
    public readonly float[] ElevationMeters;

    /// <summary>Surface block Y for each column.</summary>
    public readonly int[] SurfaceY;

    /// <summary>Mean annual temperature in degrees Celsius at the surface.</summary>
    public readonly float[] TemperatureC;

    /// <summary>Sea-level baseline temperature: surface temperature with the lapse rate removed.</summary>
    public readonly float[] SeaLevelTemperatureC;

    /// <summary>Annual precipitation in millimetres.</summary>
    public readonly float[] PrecipitationMm;

    /// <summary>Terrain slope as a rise-over-run ratio.</summary>
    public readonly float[] Slope;

    /// <summary>Number of per-column arrays held by a tile, used to size the cache.</summary>
    private const int ArraysPerTile = 6;

    public TerrainTile(int blockX, int blockZ, int size)
    {
        BlockX = blockX;
        BlockZ = blockZ;
        Size = size;
        int n = size * size;
        ElevationMeters = new float[n];
        SurfaceY = new int[n];
        TemperatureC = new float[n];
        SeaLevelTemperatureC = new float[n];
        PrecipitationMm = new float[n];
        Slope = new float[n];
    }

    public int Index(int localX, int localZ) => localZ * Size + localX;

    /// <summary>Approximate heap cost of one tile of the given edge length, in bytes.</summary>
    public static long EstimateBytes(int size) => (long)size * size * ArraysPerTile * sizeof(float);
}

/// <summary>
/// Turns the diffusion pipeline into block-space terrain tiles, with an LRU cache and a single
/// inference thread so that the pipeline's tile store is never touched concurrently.
/// </summary>
public sealed class TerrainDiffusionProvider : IDisposable
{
    private static readonly FastNoiseLite ElevationNoiseCoarse = MakeNoise(99999, 1f / 24f, 3, 2f, 0.5f);
    private static readonly FastNoiseLite ElevationNoiseFine = MakeNoise(88888, 1f / 6f, 2, 2f, 0.6f);

    private static FastNoiseLite MakeNoise(int seed, float frequency, int octaves, float lacunarity, float gain)
    {
        var fnl = new FastNoiseLite(seed);
        fnl.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
        fnl.SetFrequency(frequency);
        fnl.SetFractalType(FastNoiseLite.FractalType.FBm);
        fnl.SetFractalOctaves(octaves);
        fnl.SetFractalLacunarity(lacunarity);
        fnl.SetFractalGain(gain);
        return fnl;
    }

    private readonly WorldPipeline _pipeline;
    private readonly DiffusionWorldSettings _settings;
    private readonly ILogger _logger;
    private readonly int _tileSize;
    private readonly int _maxCachedTiles;

    private readonly ConcurrentDictionary<long, Lazy<TerrainTile>> _tiles = new();

    /// <summary>Monotonic access stamp per cached tile, used to pick eviction victims.</summary>
    private readonly ConcurrentDictionary<long, long> _lastAccess = new();

    private readonly object _evictionLock = new();
    private long _accessClock;

    /// <summary>Serialises all pipeline work; the tile store is not thread safe.</summary>
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);

    private long _tilesGenerated;
    private long _totalInferenceMillis;

    public TerrainDiffusionProvider(ulong seed, PipelineModels models, DiffusionWorldSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
        _tileSize = DiffusionConfig.Instance.TerrainTileSizeBlocks;
        _pipeline = new WorldPipeline(seed, models);

        long budget = (long)DiffusionConfig.Instance.TerrainTileCacheMegabytes * 1024 * 1024;
        _maxCachedTiles = (int)Math.Max(16, budget / TerrainTile.EstimateBytes(_tileSize));
        logger.Notification("[{0}] Terrain tile cache: up to {1} tiles of {2}x{2} blocks ({3} MB budget)",
            DiffusionPaths.ModId, _maxCachedTiles, _tileSize, DiffusionConfig.Instance.TerrainTileCacheMegabytes);
    }

    public DiffusionWorldSettings Settings => _settings;

    public long TilesGenerated => Interlocked.Read(ref _tilesGenerated);

    public long AverageTileMillis
    {
        get
        {
            long tiles = Interlocked.Read(ref _tilesGenerated);
            return tiles == 0 ? 0 : Interlocked.Read(ref _totalInferenceMillis) / tiles;
        }
    }

    public int TileSize => _tileSize;

    /// <summary>Returns the tile covering the given block position, generating it if needed.</summary>
    public TerrainTile GetTileAt(int blockX, int blockZ)
    {
        int tileX = FloorDiv(blockX, _tileSize);
        int tileZ = FloorDiv(blockZ, _tileSize);
        return GetTile(tileX, tileZ);
    }

    /// <summary>
    /// Returns the tile covering a position, reusing <paramref name="current"/> when it already
    /// covers it. Callers that walk a chunk or a run of map pixels should thread the same variable
    /// through, so a whole chunk costs one cache lookup instead of one per block.
    /// </summary>
    public TerrainTile GetTileAt(int blockX, int blockZ, ref TerrainTile current)
    {
        if (current != null &&
            blockX >= current.BlockX && blockX < current.BlockX + current.Size &&
            blockZ >= current.BlockZ && blockZ < current.BlockZ + current.Size)
        {
            return current;
        }

        current = GetTileAt(blockX, blockZ);
        return current;
    }

    public TerrainTile GetTile(int tileX, int tileZ)
    {
        long key = ((long)tileX << 32) ^ (uint)tileZ;
        Lazy<TerrainTile> lazy = _tiles.GetOrAdd(key, _ =>
            new Lazy<TerrainTile>(() => GenerateTile(tileX, tileZ), LazyThreadSafetyMode.ExecutionAndPublication));

        _lastAccess[key] = Interlocked.Increment(ref _accessClock);
        TerrainTile tile = lazy.Value;
        EvictLeastRecentlyUsed(key);
        return tile;
    }

    /// <summary>
    /// Drops the least recently used tiles until the cache is back within budget. The key that was
    /// just handed out is never a candidate: evicting the tile currently being read would make the
    /// caller regenerate it on its very next block column.
    /// </summary>
    private void EvictLeastRecentlyUsed(long protectedKey)
    {
        if (_tiles.Count <= _maxCachedTiles) return;

        lock (_evictionLock)
        {
            while (_tiles.Count > _maxCachedTiles)
            {
                long oldestKey = 0;
                long oldestAccess = long.MaxValue;
                bool found = false;

                foreach (KeyValuePair<long, long> entry in _lastAccess)
                {
                    if (entry.Key == protectedKey || entry.Value >= oldestAccess) continue;
                    oldestKey = entry.Key;
                    oldestAccess = entry.Value;
                    found = true;
                }

                if (!found) return;
                _tiles.TryRemove(oldestKey, out _);
                _lastAccess.TryRemove(oldestKey, out _);
            }
        }
    }

    private TerrainTile GenerateTile(int tileX, int tileZ)
    {
        _inferenceGate.Wait();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            TerrainTile tile = GenerateTileUnsynchronized(tileX, tileZ);
            stopwatch.Stop();

            Interlocked.Increment(ref _tilesGenerated);
            Interlocked.Add(ref _totalInferenceMillis, stopwatch.ElapsedMilliseconds);

            // A tile that takes real model time is worth a log line; a fast one means the pipeline
            // cache absorbed it, and logging every one of those buries the interesting entries.
            bool interesting = stopwatch.ElapsedMilliseconds >= 100 || DiffusionConfig.Instance.VerboseInference;
            string message = "[{0}] Generated terrain tile ({1}, {2}) covering blocks ({3}, {4})-({5}, {6}) in {7} ms";
            object[] fields =
            {
                DiffusionPaths.ModId, tileX, tileZ,
                tileX * _tileSize, tileZ * _tileSize,
                (tileX + 1) * _tileSize, (tileZ + 1) * _tileSize,
                stopwatch.ElapsedMilliseconds
            };

            if (interesting) _logger.Notification(message, fields);
            else _logger.VerboseDebug(message, fields);

            WarnIfThrashing(tileX, tileZ);
            return tile;
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    /// <summary>
    /// Ring buffer of the most recently generated tiles. Regenerating the same tile several times
    /// in a row means it is being evicted while still in use, which quietly multiplies the cost of
    /// world generation, so say so loudly rather than letting it hide in the timings.
    /// </summary>
    private readonly long[] _recentlyGenerated = new long[32];
    private int _recentlyGeneratedCursor;
    private bool _thrashWarned;

    private void WarnIfThrashing(int tileX, int tileZ)
    {
        if (_thrashWarned) return;

        long key = ((long)tileX << 32) ^ (uint)tileZ;
        int repeats = 0;
        lock (_recentlyGenerated)
        {
            _recentlyGenerated[_recentlyGeneratedCursor] = key;
            _recentlyGeneratedCursor = (_recentlyGeneratedCursor + 1) % _recentlyGenerated.Length;
            foreach (long recent in _recentlyGenerated) if (recent == key) repeats++;
        }

        if (repeats < 8) return;
        _thrashWarned = true;
        _logger.Warning(
            "[{0}] Terrain tile ({1}, {2}) has been regenerated {3} times in the last {4} generations. " +
            "The tile cache is too small for the area being generated; raise terrainTileSizeBlocks or " +
            "report this. World generation will be much slower than it should be.",
            DiffusionPaths.ModId, tileX, tileZ, repeats, _recentlyGenerated.Length);
    }

    private TerrainTile GenerateTileUnsynchronized(int tileX, int tileZ)
    {
        int size = _tileSize;
        int worldBlockX = tileX * size;
        int worldBlockZ = tileZ * size;
        int scale = _settings.Scale;
        var tile = new TerrainTile(worldBlockX, worldBlockZ, size);

        // The model is sampled relative to its own origin, which sits at the centre of the world.
        // Pipeline coordinates: i runs along Z, j along X, in native model pixels.
        int blockX = worldBlockX - _settings.OriginBlockX;
        int blockZ = worldBlockZ - _settings.OriginBlockZ;
        float pixelSizeMeters = _settings.MetersPerBlock;

        float[] elevation;
        float[] elevationPadded;
        float[] climate;

        if (scale <= 1)
        {
            WorldPipeline.Sample padded = _pipeline.Get(blockZ - 1, blockX - 1, blockZ + size + 1, blockX + size + 1, false);
            WorldPipeline.Sample sample = _pipeline.Get(blockZ, blockX, blockZ + size, blockX + size, true);
            elevation = sample.Elevation;
            elevationPadded = padded.Elevation;
            climate = sample.Climate;
        }
        else
        {
            // Sample at native resolution with two pixels of padding (one for the bilinear filter,
            // one for the slope kernel), then upsample to block resolution.
            int i1n = FloorDiv(blockZ, scale);
            int j1n = FloorDiv(blockX, scale);
            int i2n = -FloorDiv(-(blockZ + size), scale);
            int j2n = -FloorDiv(-(blockX + size), scale);

            int i1p = i1n - 2, j1p = j1n - 2;
            int i2p = i2n + 2, j2p = j2n + 2;
            int nh = i2p - i1p, nw = j2p - j1p;

            WorldPipeline.Sample sample = _pipeline.Get(i1p, j1p, i2p, j2p, true);

            float[][] elevationNative = WorldPipeline.To2D(sample.Elevation, nh, nw);
            float[][] elevationUp = LaplacianUtils.BilinearResize(elevationNative, nh * scale, nw * scale);

            int padUp = 2 * scale;
            int cropI = padUp + (blockZ - i1n * scale);
            int cropJ = padUp + (blockX - j1n * scale);

            elevation = CropFlat(elevationUp, cropI, cropJ, size, size, nh * scale, nw * scale);
            elevationPadded = CropFlat(elevationUp, cropI - 1, cropJ - 1, size + 2, size + 2, nh * scale, nw * scale);
            climate = UpsampleClimate(sample.Climate, nh, nw, cropI, cropJ, size, size, nh * scale, nw * scale);
        }

        float[] slope = SobelSlope(elevationPadded, size, size, pixelSizeMeters);
        AddSlopeNoise(elevation, slope, blockX, blockZ, size, pixelSizeMeters, _settings.SlopeDetailStrength);

        int plane = size * size;
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                int idx = r * size + c;
                float meters = elevation[idx];
                tile.ElevationMeters[idx] = meters;
                tile.SurfaceY[idx] = _settings.ElevationToBlockY(meters);
                tile.Slope[idx] = slope[idx];

                if (climate != null)
                {
                    float temperature = climate[idx];
                    float lapse = climate[4 * plane + idx];
                    tile.TemperatureC[idx] = temperature;
                    tile.SeaLevelTemperatureC[idx] = temperature - lapse * Math.Max(0f, meters);
                    tile.PrecipitationMm[idx] = Math.Max(0f, climate[2 * plane + idx]);
                }
            }
        }

        return tile;
    }

    /// <summary>
    /// Adds two octaves of Perlin detail on sloped land. The model resolves features down to about
    /// 30 m, so without this, hillsides upsampled to block resolution look glassy.
    /// </summary>
    private static void AddSlopeNoise(float[] elevation, float[] slope, int blockX, int blockZ,
                                      int size, float pixelSizeMeters, float detailStrength)
    {
        if (detailStrength <= 0f) return;

        const float nativeResolution = 30f;
        float normFactor = 40f * pixelSizeMeters / nativeResolution;
        float amplitudeCoarse = 100f * pixelSizeMeters / nativeResolution * detailStrength;
        float amplitudeFine = 70f * pixelSizeMeters / nativeResolution * detailStrength;

        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                int idx = r * size + c;
                float e = elevation[idx];
                if (e < 0f) continue;

                // slope[] is a ratio; the original pipeline works with the raw gradient here.
                float gradient = slope[idx] * pixelSizeMeters;
                float strength = Math.Min(1f, gradient / normFactor);
                strength = strength * strength * (float)Math.Sqrt(strength);

                float nx = blockX + c, ny = blockZ + r;
                elevation[idx] = e
                    + ElevationNoiseCoarse.GetNoise(nx, ny) * amplitudeCoarse * strength
                    + ElevationNoiseFine.GetNoise(nx, ny) * amplitudeFine * strength;
            }
        }
    }

    private static float[] SobelSlope(float[] padded, int h, int w, float pixelSizeMeters)
    {
        int pw = w + 2;
        var slope = new float[h * w];
        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                float tl = padded[r * pw + c], tc = padded[r * pw + c + 1], tr = padded[r * pw + c + 2];
                float ml = padded[(r + 1) * pw + c], mr = padded[(r + 1) * pw + c + 2];
                float bl = padded[(r + 2) * pw + c], bc = padded[(r + 2) * pw + c + 1], br = padded[(r + 2) * pw + c + 2];

                float dx = (-tl + tr - 2 * ml + 2 * mr - bl + br) / 8f;
                float dy = (-tl - 2 * tc - tr + bl + 2 * bc + br) / 8f;
                slope[r * w + c] = (float)Math.Sqrt(dx * dx + dy * dy) / pixelSizeMeters;
            }
        }
        return slope;
    }

    private static float[] UpsampleClimate(float[] climateNative, int nh, int nw,
                                           int cropI, int cropJ, int h, int w, int upH, int upW)
    {
        if (climateNative == null) return null;

        const int channels = 5;
        var result = new float[channels * h * w];
        int nativePlane = nh * nw;
        for (int ch = 0; ch < channels; ch++)
        {
            var native = new float[nh][];
            for (int r = 0; r < nh; r++)
            {
                native[r] = new float[nw];
                Array.Copy(climateNative, ch * nativePlane + r * nw, native[r], 0, nw);
            }
            float[][] up = LaplacianUtils.BilinearResize(native, upH, upW);
            for (int r = 0; r < h; r++)
            {
                float[] row = up[Math.Clamp(cropI + r, 0, upH - 1)];
                for (int c = 0; c < w; c++)
                    result[ch * h * w + r * w + c] = row[Math.Clamp(cropJ + c, 0, upW - 1)];
            }
        }
        return result;
    }

    private static float[] CropFlat(float[][] source, int r0, int c0, int h, int w, int srcH, int srcW)
    {
        var output = new float[h * w];
        for (int r = 0; r < h; r++)
        {
            float[] row = source[Math.Clamp(r0 + r, 0, srcH - 1)];
            for (int c = 0; c < w; c++)
                output[r * w + c] = row[Math.Clamp(c0 + c, 0, srcW - 1)];
        }
        return output;
    }

    /// <summary>Native pixels along one edge of a coarse cell.</summary>
    private static int CoarseCellNativePixels => 32 * WorldPipelineModelConfig.Instance.LatentCompression;

    /// <summary>Below this many land cells the coarse survey is too small to say anything useful.</summary>
    private const int MinimumSurveyedLandCells = 16;

    /// <summary>
    /// Measures how tall the terrain around spawn actually gets, so the metre-to-block mapping can
    /// be fitted to it. Two steps: the cheap coarse stage surveys the play area, then a handful of
    /// full-detail probes on its tallest cells recover the summits that kilometre-scale averaging
    /// hides. The result is a property of the seed, not of the machine, so it is stable across
    /// restarts and worth caching in the save.
    /// </summary>
    /// <param name="centerBlockX">World block X the survey is centred on, usually the spawn.</param>
    /// <param name="centerBlockZ">World block Z the survey is centred on.</param>
    /// <returns>Peak elevation in metres, or null if the survey failed or found no land.</returns>
    public float? MeasurePeakElevation(int centerBlockX, int centerBlockZ)
    {
        int coarseToNative = CoarseCellNativePixels;
        int scale = Math.Max(1, _settings.Scale);
        int nativeRadius = Math.Max(coarseToNative, _settings.CalibrationRadiusBlocks / scale);

        // Survey coordinates are coarse cells relative to the model origin.
        int centerI = FloorDiv((centerBlockZ - _settings.OriginBlockZ) / scale, coarseToNative);
        int centerJ = FloorDiv((centerBlockX - _settings.OriginBlockX) / scale, coarseToNative);

        int half = Math.Clamp(nativeRadius / coarseToNative, 2, 128);
        List<(float Meters, int Row, int Col)> cells = null;

        // An island or a stretch of coast can leave the requested box with barely any land in it,
        // and a handful of cells is not a distribution. Widen until there is something to measure.
        while (true)
        {
            cells = SurveyLandCells(centerI, centerJ, half);
            if (cells == null) return null;
            if (cells.Count >= MinimumSurveyedLandCells || half >= 128) break;

            half = Math.Min(128, half * 2);
            _logger.VerboseDebug("[{0}] Only {1} land cells in the terrain height survey; widening it.",
                DiffusionPaths.ModId, cells.Count);
        }

        if (cells.Count == 0)
        {
            _logger.Warning("[{0}] Terrain height survey found no land near ({1}, {2}); leaving terrain at true scale.",
                DiffusionPaths.ModId, centerBlockX, centerBlockZ);
            return null;
        }

        cells.Sort((a, b) => b.Meters.CompareTo(a.Meters));
        float tail = 1f - _settings.PeakQuantile;
        float surveyValue = cells[TailIndex(cells.Count, tail)].Meters;
        int surveyedBlocks = 2 * half * coarseToNative * scale;

        // The survey only sees terrain averaged over whole coarse cells several kilometres wide, so
        // summits are smoothed away and have to be probed at full detail. The probed cells cover a
        // known fraction of the land, so the elevation only `tail` of the whole region exceeds is
        // the corresponding upper quantile of the probed pixels.
        int poolCells = Math.Clamp(
            (int)Math.Ceiling(2.0 * tail * cells.Count), Math.Min(3, cells.Count), cells.Count);
        float poolFraction = poolCells / (float)cells.Count;
        int probeCount = Math.Min(_settings.CalibrationProbes, poolCells);

        if (probeCount > 0)
        {
            var pool = new List<float>(probeCount * ProbeNativePixels * ProbeNativePixels);
            int probesRun = 0;

            for (int i = 0; i < probeCount; i++)
            {
                // Spread the probes across the pool rather than clustering them on the single
                // tallest massif, so one exceptional mountain cannot set the scale for the world.
                (float _, int row, int col) = cells[i * poolCells / probeCount];
                float[] probe = Probe(
                    (row - half + centerI) * coarseToNative + coarseToNative / 2,
                    (col - half + centerJ) * coarseToNative + coarseToNative / 2);
                if (probe == null) continue;

                probesRun++;
                foreach (float meters in probe) if (meters > 0f) pool.Add(meters);
            }

            if (pool.Count > 0)
            {
                pool.Sort((a, b) => b.CompareTo(a));
                float peak = pool[TailIndex(pool.Count, Math.Min(1f, tail / poolFraction))];
                _logger.Notification(
                    "[{0}] Terrain height survey: {1} land cells over {2} blocks, {3:0} m at the {4:0.###} " +
                    "quantile of the coarse map; {5} full-detail probes put the peak at {6:0} m.",
                    DiffusionPaths.ModId, cells.Count, surveyedBlocks, surveyValue, _settings.PeakQuantile,
                    probesRun, peak);
                return peak;
            }

            _logger.Warning("[{0}] Every terrain height probe failed; falling back to the coarse survey.",
                DiffusionPaths.ModId);
        }

        float estimate = surveyValue * _settings.ReliefFactor;
        _logger.Notification(
            "[{0}] Terrain height survey: {1} land cells over {2} blocks, {3:0} m at the {4:0.###} quantile, " +
            "estimated peak {5:0} m without probing.",
            DiffusionPaths.ModId, cells.Count, surveyedBlocks, surveyValue, _settings.PeakQuantile, estimate);
        return estimate;
    }

    /// <summary>
    /// Reads the coarse map over a box of cells and returns the land ones with their mean
    /// elevation in metres, or null if the coarse stage could not be sampled at all.
    /// </summary>
    private List<(float Meters, int Row, int Col)> SurveyLandCells(int centerI, int centerJ, int half)
    {
        FloatTensor coarse;
        _inferenceGate.Wait();
        try
        {
            coarse = _pipeline.GetCoarseSlice(centerI - half, centerJ - half, centerI + half, centerJ + half);
        }
        catch (Exception e)
        {
            _logger.Warning("[{0}] Terrain height survey failed: {1}", DiffusionPaths.ModId, e.Message);
            return null;
        }
        finally
        {
            _inferenceGate.Release();
        }

        int size = 2 * half;
        int plane = size * size;

        // Channel 0 is elevation in signed square-root space; only land contributes.
        var cells = new List<(float Meters, int Row, int Col)>(plane);
        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++)
            {
                int index = row * size + col;
                float weight = coarse.Data[6 * plane + index];
                if (weight <= 1e-6f) continue;

                float value = coarse.Data[index] / weight;
                if (value <= 0f) continue;
                cells.Add((value * value, row, col));
            }
        }
        return cells;
    }

    /// <summary>Native pixels along one edge of a full-detail calibration probe.</summary>
    private const int ProbeNativePixels = 128;

    /// <summary>Runs the full pipeline over a small window, or null if it could not be sampled.</summary>
    private float[] Probe(int centerI, int centerJ)
    {
        int halfWindow = ProbeNativePixels / 2;

        _inferenceGate.Wait();
        try
        {
            return _pipeline.Get(
                centerI - halfWindow, centerJ - halfWindow,
                centerI + halfWindow, centerJ + halfWindow, false).Elevation;
        }
        catch (Exception e)
        {
            _logger.VerboseDebug("[{0}] Terrain height probe at native ({1}, {2}) failed: {3}",
                DiffusionPaths.ModId, centerJ, centerI, e.Message);
            return null;
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    /// <summary>
    /// Index, in a list sorted from highest to lowest, of the value that only
    /// <paramref name="fraction"/> of the list exceeds.
    /// </summary>
    private static int TailIndex(int count, float fraction)
        => Math.Clamp((int)(fraction * count), 0, count - 1);

    /// <summary>
    /// Searches the cheap coarse stage for a solidly-inland spot near the model origin, expanding
    /// the search box until one is found. Used to place the world spawn, since the model decides
    /// where the continents are and the map centre is just as likely to be open ocean.
    /// </summary>
    /// <returns>World block coordinates of a land column, or null if the search found only sea.</returns>
    public (int X, int Z)? FindLandNearOrigin(int initialSizeCoarsePixels = 16, int maxSizeCoarsePixels = 128)
    {
        // One coarse pixel spans 32 * latentCompression native pixels.
        int coarseToNative = 32 * WorldPipelineModelConfig.Instance.LatentCompression;

        for (int boxSize = initialSizeCoarsePixels; boxSize <= maxSizeCoarsePixels; boxSize += 8)
        {
            int half = boxSize / 2;
            FloatTensor coarse;

            _inferenceGate.Wait();
            try
            {
                coarse = _pipeline.GetCoarseSlice(-half, -half, half, half);
            }
            catch (Exception e)
            {
                _logger.Warning("[{0}] Coarse map query failed while looking for a spawn: {1}",
                    DiffusionPaths.ModId, e.Message);
                return null;
            }
            finally
            {
                _inferenceGate.Release();
            }

            int h = boxSize, w = boxSize;
            int plane = h * w;
            (int Row, int Col)? best = null;
            int bestDistance = int.MaxValue;

            for (int r = 1; r < h - 1; r++)
            {
                for (int c = 1; c < w - 1; c++)
                {
                    if (!IsLandWithLandNeighbours(coarse, plane, w, r, c)) continue;

                    int dr = r - half, dc = c - half;
                    int distance = dr * dr + dc * dc;
                    if (distance >= bestDistance) continue;

                    bestDistance = distance;
                    best = (r, c);
                }
            }

            if (best == null)
            {
                _logger.VerboseDebug("[{0}] No land in a {1}x{1} coarse box; widening the spawn search.",
                    DiffusionPaths.ModId, boxSize);
                continue;
            }

            // Centre of the chosen coarse pixel, in native pixels, then in world blocks.
            int nativeZ = (best.Value.Row - half) * coarseToNative + coarseToNative / 2;
            int nativeX = (best.Value.Col - half) * coarseToNative + coarseToNative / 2;
            int blockX = nativeX * _settings.Scale + _settings.OriginBlockX;
            int blockZ = nativeZ * _settings.Scale + _settings.OriginBlockZ;

            _logger.Notification("[{0}] Spawn search found land at ({1}, {2}) after scanning a {3}x{3} coarse box.",
                DiffusionPaths.ModId, blockX, blockZ, boxSize);
            return (blockX, blockZ);
        }

        return null;
    }

    /// <summary>A coarse pixel counts as land only if all eight neighbours are above sea level too.</summary>
    private static bool IsLandWithLandNeighbours(FloatTensor coarse, int plane, int width, int row, int col)
    {
        for (int dr = -1; dr <= 1; dr++)
        {
            for (int dc = -1; dc <= 1; dc++)
            {
                int index = (row + dr) * width + (col + dc);
                float weight = coarse.Data[6 * plane + index];
                if (weight <= 1e-6f) return false;
                // Channel 0 is elevation in signed-sqrt space, so its sign is the sign of elevation.
                if (coarse.Data[index] / weight <= 0f) return false;
            }
        }
        return true;
    }

    internal static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }

    public void Dispose()
    {
        _tiles.Clear();
        _inferenceGate.Dispose();
    }
}
