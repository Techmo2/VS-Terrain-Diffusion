using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;
using VSTerrainDiffusion.Pipeline;
using VSTerrainDiffusion.Tensors;
using static VSTerrainDiffusion.Core.IntMath;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>Building one tile from the pipeline, and reporting what that cost.</summary>
public sealed partial class TerrainDiffusionProvider
{
    /// <summary>
    /// Generates a tile through the scheduler, at the urgency of its distance to the nearest player.
    /// </summary>
    private TerrainTile GenerateTile(int tileX, int tileZ)
        => _scheduler.Run(UrgencyOf(tileX, tileZ), () => GenerateTileMeasured(tileX, tileZ));

    /// <summary>
    /// One attempt at a tile. May be abandoned part way through for something closer to a player,
    /// in which case the scheduler calls it again; nothing is published until a window is whole, so
    /// an abandoned attempt leaves only work to redo.
    /// </summary>
    private TerrainTile GenerateTileMeasured(int tileX, int tileZ)
    {
        {
            var stopwatch = Stopwatch.StartNew();
            TerrainTile tile = GenerateTileUnsynchronized(tileX, tileZ);
            stopwatch.Stop();

            long elapsed = stopwatch.ElapsedMilliseconds;

            // Read the running totals before this tile joins them, so a tile is never compared
            // against a mean it is itself part of.
            long priorTiles = Interlocked.Read(ref _tilesGenerated);
            long priorMillis = Interlocked.Read(ref _totalInferenceMillis);

            Interlocked.Increment(ref _tilesGenerated);
            Interlocked.Add(ref _totalInferenceMillis, elapsed);

            string message = "[{0}] Generated terrain tile ({1}, {2}) covering blocks ({3}, {4})-({5}, {6}) in {7} ms";
            object[] fields =
            {
                DiffusionPaths.ModId, tileX, tileZ,
                tileX * _tileSize, tileZ * _tileSize,
                (tileX + 1) * _tileSize, (tileZ + 1) * _tileSize,
                elapsed
            };

            if (DiffusionConfig.Instance.VerboseInference || IsWorthReporting(elapsed, priorTiles, priorMillis))
            {
                _logger.Notification(message, fields);
            }
            else
            {
                _logger.VerboseDebug(message, fields);
            }

            WarnIfThrashing(tileX, tileZ);
            TileGenerated?.Invoke(tile);
            return tile;
        }
    }

    /// <summary>A tile below this is never worth a line, however far out of step it is.</summary>
    private const long ReportFloorMillis = 1000;

    /// <summary>How many times the running mean a tile has to cost before it counts as a stall.</summary>
    private const long ReportRatio = 4;

    /// <summary>Tiles needed before the mean means anything.</summary>
    private const long ReportMinSamples = 8;

    /// <summary>
    /// Whether one tile's time is worth interrupting the log for when verbose logging is off.
    ///
    /// This used to be a flat 100 ms, on the reasoning that a fast tile had been absorbed by a
    /// cache and a slow one had really run the model. That bar stopped separating anything: a tile
    /// that runs the model costs 140-180 ms on the hardware this has been measured on, so every
    /// real tile cleared it and exploring a world wrote a line per tile no matter how the mod was
    /// configured. Doing the work it exists to do is not news.
    ///
    /// What is worth a line is a tile far out of step with the rest of this session's - a stall
    /// that wants explaining rather than the model doing its job. Measuring that against the
    /// running mean also makes it self-damping: if generation degrades across the board the mean
    /// follows it up and this goes quiet again, leaving the thrash warning to say so once, in
    /// terms that are actually actionable.
    /// </summary>
    private static bool IsWorthReporting(long millis, long priorTiles, long priorMillis)
    {
        if (millis < ReportFloorMillis) return false;

        // No baseline yet. The first tiles of a session pay for cold caches and first-touch
        // allocation, and one line saying how much is worth having.
        if (priorTiles < ReportMinSamples) return true;

        return millis * priorTiles >= ReportRatio * priorMillis;
    }

    /// <summary>
    /// Ring buffer of the most recently generated tiles. Regenerating the same tile several times
    /// in a row means it is being evicted while still in use, which quietly multiplies the cost of
    /// world generation, so say so loudly rather than letting it hide in the timings.
    /// </summary>
    private readonly long[] _recentlyGenerated = new long[32];

    /// <summary>Next slot to write in <see cref="_recentlyGenerated"/>.</summary>
    private int _recentlyGeneratedCursor;

    /// <summary>Set once the thrash warning has been logged, so it is said only once per world.</summary>
    private bool _thrashWarned;

    /// <summary>Records a generated tile and warns, once, if the same tile keeps being regenerated.</summary>
    private void WarnIfThrashing(int tileX, int tileZ)
    {
        if (_thrashWarned) return;

        // A chunk peek generates terrain nowhere near the player and throws it away. Counting that
        // as thrashing would tell them to raise a cache size that is not the problem.
        if (ChunkPeekScope.Active || _pregenerating) return;

        long key = TileKey(tileX, tileZ);
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
            "The tile cache is too small for the area being generated; raise terrainTileCacheMegabytes or " +
            "report this. World generation will be much slower than it should be.",
            DiffusionPaths.ModId, tileX, tileZ, repeats, _recentlyGenerated.Length);
    }

    /// <summary>
    /// Builds one tile: samples the pipeline over it (upsampling at scales above 1), adds slope and slope
    /// detail, and converts elevation and climate to the world's blocks and settings. The caller holds the
    /// scheduler.
    /// </summary>
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

            float[][] elevationNative = TileRaster.To2D(sample.Elevation, nh, nw);
            float[][] elevationUp = LaplacianUtils.BilinearResize(elevationNative, nh * scale, nw * scale);

            int padUp = 2 * scale;
            int cropI = padUp + (blockZ - i1n * scale);
            int cropJ = padUp + (blockX - j1n * scale);

            elevation = TileRaster.CropFlat(elevationUp, cropI, cropJ, size, size, nh * scale, nw * scale);
            elevationPadded = TileRaster.CropFlat(elevationUp, cropI - 1, cropJ - 1, size + 2, size + 2, nh * scale, nw * scale);
            climate = TileRaster.UpsampleClimate(sample.Climate, nh, nw, cropI, cropJ, size, size, nh * scale, nw * scale);
        }

        float[] slope = TileRaster.SobelSlope(elevationPadded, size, size, pixelSizeMeters);
        float[] modelElevation = climate == null ? null : (float[])elevation.Clone();
        TileRaster.AddSlopeNoise(elevation, slope, blockX, blockZ, size, pixelSizeMeters,
            WorldPipelineModelConfig.Instance.NativeResolution, _settings.SlopeDetailStrength);

        int plane = size * size;
        for (int index = 0; index < plane; index++)
        {
            float meters = elevation[index];
            tile.ElevationMeters[index] = meters;
            tile.SurfaceY[index] = _settings.ElevationToBlockY(meters);
            tile.Slope[index] = slope[index];

            if (climate == null) continue;

            // Whatever the world's global climate settings and its latitude bands did not get from
            // the model is applied here rather than at the climate map, so that everything reading
            // a tile - the map, the freeze line, the surface rules, the seasons - sees one
            // consistent climate. Rows run along Z, which is the axis latitude is measured on.
            //
            // The model's temperature is for its own ground, sea-level baseline plus the local lapse
            // rate times elevation; the slope detail moved the ground since, so it moves with it.
            int columnZ = tile.BlockZ + index / size;
            float temperature = climate[index]
                + climate[4 * plane + index] * (Math.Max(0f, meters) - Math.Max(0f, modelElevation[index]));
            tile.TemperatureC[index] = _settings.WorldTemperature(temperature, columnZ);
            tile.TemperatureSeasonality[index] = climate[plane + index];
            tile.PrecipitationMm[index] =
                Math.Max(0f, _settings.WorldPrecipitation(climate[2 * plane + index], columnZ));
            tile.PrecipitationCv[index] = Math.Max(0f, climate[3 * plane + index]);
        }

        return tile;
    }
}
