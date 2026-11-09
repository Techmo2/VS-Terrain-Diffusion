using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VSTerrainDiffusion.Pipeline;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.WorldGen;

namespace VSTerrainDiffusion.Debug;

/// <summary>
/// Recording: each finished tile reduced to a thumbnail of every layer, one quantised byte per
/// cell, and the history kept within its budget.
/// </summary>
public sealed partial class DebugMapServer
{
    /// <summary>Downsamples one finished tile and files it under the next sequence number.</summary>
    private void Record(TerrainTile tile)
    {
        try
        {
            RecordedTile recorded = Downsample(tile);
            long key = ((long)recorded.X << 32) ^ (uint)recorded.Z;
            _tiles[key] = recorded;
            EvictOldest();
        }
        catch (Exception e)
        {
            // Never let the debug view break world generation.
            _log.Warning("[{0}] Debug map could not record a tile: {1}", DiffusionPaths.ModId, e.Message);
        }
    }

    /// <summary>
    /// What the model was told about rivers at a position, on the same 0 to 1 scale the
    /// conditioning uses. Zero where this world has no river network.
    /// </summary>
    private float BasinAt(int blockX, int blockZ)
    {
        if (_riverBasins == null) return 0f;

        try
        {
            int blocksPerCell = 32 * WorldPipelineModelConfig.Instance.LatentCompression
                                * Math.Max(1, _settings.Scale);
            int j = (int)Math.Floor((blockX - (double)_settings.OriginBlockX) / blocksPerCell);
            int i = (int)Math.Floor((blockZ - (double)_settings.OriginBlockZ) / blocksPerCell);

            // x then y, the same order the landmask and the synthetic map use.
            float[] strength = _riverBasins.BasinStrength(j, i, j + 1, i + 1);
            return strength is { Length: > 0 } ? strength[0] : 0f;
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    /// <summary>The map pixel under a block of a tile, from <see cref="DiffusionForestMapLayer.CoverWithin"/>.</summary>
    private static float MapPixel(int[] map, DiffusionForestMapLayer layer, int blockX, int blockZ, int tileSize)
    {
        if (map == null) return 0f;
        int per = layer.BlocksPerMapPixel;
        int side = Math.Max(1, tileSize / per);
        return map[Math.Min(side - 1, blockZ / per) * side + Math.Min(side - 1, blockX / per)];
    }

    /// <summary>
    /// The ground as the world builds it at one column of a tile, Rivers' valleys and channels
    /// included: the cell's centre column rather than its mean, because a valley narrower than a
    /// cell would average away, and a mean of 64 river samples a cell costs more than a map is worth.
    /// </summary>
    private float BuiltSurfaceAt(TerrainTile tile, int x, int z, Dictionary<long, object> riverContexts)
    {
        int modelY = tile.SurfaceY[z * tile.Size + x];
        if (!RiversCompat.Installed) return modelY;

        try
        {
            int blockX = tile.BlockX + x, blockZ = tile.BlockZ + z;
            long chunk = ((long)(blockX >> 5) << 32) ^ (uint)(blockZ >> 5);
            if (!riverContexts.TryGetValue(chunk, out object context))
            {
                context = RiversCompat.ChunkContext(blockX >> 5, blockZ >> 5);
                riverContexts[chunk] = context;
            }
            return _provider.BuiltSurfaceY(modelY, blockX, blockZ, RiversCompat.SampleAt(context, blockX, blockZ));
        }
        catch (Exception e)
        {
            if (!_builtSurfaceWarned)
            {
                _builtSurfaceWarned = true;
                _log.Warning("[{0}] Debug map shows the model's height without river valleys: {1}", DiffusionPaths.ModId, e.Message);
            }
            return modelY;
        }
    }

    /// <summary>
    /// Reduces a tile to <see cref="ThumbSize"/>² cells per layer: block means for the full-resolution
    /// climate, the centre column for the built surface, the map pixel for the vegetation maps, and one
    /// coarse reading for the whole tile.
    /// </summary>
    /// <param name="tile">The tile just generated.</param>
    /// <returns>The quantised thumbnail, under the next sequence number.</returns>
    private RecordedTile Downsample(TerrainTile tile)
    {
        int size = tile.Size;
        int step = Math.Max(1, size / ThumbSize);
        int cells = ThumbSize * ThumbSize;
        int layerCount = _layers.Length;

        var values = new float[layerCount * cells];
        RainfallScale rainfall = RainfallScale.FromConfig(DiffusionConfig.Instance.WorldGen);

        // One reading for the whole tile: a coarse cell is wider than a tile, so there is no detail
        // below this to lose.
        int centreX = tile.BlockX + size / 2, centreZ = tile.BlockZ + size / 2;
        TerrainDiffusionProvider.CoarseProbe? probe = _provider.ProbeCoarseAt(centreX, centreZ);
        float basin = BasinAt(centreX, centreZ);

        // Seven input layers then five coarse outputs, all flat across the tile, in the order of
        // CoarseLayerIds.
        var coarseValues = new float[CoarseLayerIds.Length];
        if (probe != null)
        {
            TerrainDiffusionProvider.CoarseProbe p = probe.Value;
            for (int c = 0; c < 5; c++) coarseValues[c] = p.Conditioning[c];
            coarseValues[5] = p.SeaFraction < 0f ? 0f : p.SeaFraction;
            coarseValues[6] = basin;
            for (int c = 0; c < 5; c++) coarseValues[7 + c] = p.Output[c];
        }
        else
        {
            coarseValues[6] = basin;
        }

        // The map pixels over this tile at each stage - the model's cover, vanilla's map and the two
        // combined as the game reads them: 32 blocks a pixel for forest and 16 for shrubs, each
        // coarser than a thumbnail cell, so a cell takes its pixel.
        (DiffusionForestMapLayer forestLayer, DiffusionForestMapLayer shrubLayer) = _vegetation?.Invoke() ?? default;
        (int[] Model, int[] Vanilla, int[] Applied) forest = forestLayer?.CoverWithin(tile) ?? default;
        (int[] Model, int[] Vanilla, int[] Applied) shrub = shrubLayer?.CoverWithin(tile) ?? default;

        int Off(string id) => _slot[id] * cells;
        int surfaceOff = Off("surfaceY"), elevationOff = Off("elevation"), slopeOff = Off("slope");
        int temperatureOff = Off("temperature"), seasonalityOff = Off("tempSeasonality");
        int precipitationOff = Off("precipitation"), cvOff = Off("precipCv"), rainfallOff = Off("rainfall");
        int koppenOff = Off("koppen"), diurnalOff = Off("diurnalRange");

        // The seasonal cycle and diurnal range, once per tile: like the coarse layers, they vary only
        // over the model's 7.68 km cells. An annual-model world approximates the cycle per cell instead.
        bool seasonal = _provider.CoarseSpec.HasSeasonalCycle;
        Span<float> cycle = stackalloc float[6];
        float diurnalRange = float.NaN;
        if (seasonal)
        {
            Span<float> channels = stackalloc float[Pipeline.CoarseChannel.SeasonalCount];
            if (_provider.CoarseChannelsAt(centreX, centreZ, channels))
            {
                channels.Slice(Pipeline.CoarseChannel.TemperatureShapeCos, 6).CopyTo(cycle);
                diurnalRange = channels[Pipeline.CoarseChannel.DiurnalRange];
            }
            else
            {
                seasonal = false;
            }
        }
        (string Id, int[] Map, DiffusionForestMapLayer Layer)[] covers =
        {
            ("forestModel", forest.Model, forestLayer), ("forest", forest.Applied, forestLayer),
            ("vanillaForest", forest.Vanilla, forestLayer),
            ("shrubModel", shrub.Model, shrubLayer), ("shrub", shrub.Applied, shrubLayer),
            ("vanillaShrub", shrub.Vanilla, shrubLayer)
        };
        var coverOffsets = covers.Select(c => Off(c.Id)).ToArray();

        // Rivers' network, resolved once per chunk: a tile is 64 of them against 1024 samples.
        var riverContexts = new Dictionary<long, object>();
        var coarseOffsets = CoarseLayerIds.Select(Off).ToArray();

        for (int v = 0; v < ThumbSize; v++)
        {
            for (int u = 0; u < ThumbSize; u++)
            {
                int cell = v * ThumbSize + u;
                for (int k = 0; k < covers.Length; k++)
                    values[coverOffsets[k] + cell] = MapPixel(covers[k].Map, covers[k].Layer, u * step, v * step, size);
                int x0 = u * step, z0 = v * step;
                int x1 = Math.Min(size, x0 + step), z1 = Math.Min(size, z0 + step);

                double elevation = 0, slope = 0;
                double temperature = 0, tempSeasonality = 0, precipitation = 0, precipCv = 0;
                int n = 0;

                for (int z = z0; z < z1; z++)
                {
                    int row = z * size;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = row + x;
                        elevation += tile.ElevationMeters[i];
                        slope += tile.Slope[i];
                        temperature += tile.TemperatureC[i];
                        tempSeasonality += tile.TemperatureSeasonality[i];
                        precipitation += tile.PrecipitationMm[i];
                        precipCv += tile.PrecipitationCv[i];
                        n++;
                    }
                }

                if (n == 0) continue;

                float meanTemperature = (float)(temperature / n);
                float meanSeasonality = (float)(tempSeasonality / n);
                float meanPrecipitation = (float)(precipitation / n);
                float meanCv = (float)(precipCv / n);

                values[surfaceOff + cell] = BuiltSurfaceAt(tile, (x0 + x1) / 2, (z0 + z1) / 2, riverContexts);
                values[elevationOff + cell] = (float)(elevation / n);
                values[slopeOff + cell] = (float)(slope / n);
                values[temperatureOff + cell] = meanTemperature;
                values[seasonalityOff + cell] = meanSeasonality;
                values[precipitationOff + cell] = meanPrecipitation;
                values[cvOff + cell] = meanCv;

                // Derived from the cell's averaged climate rather than per column: the quantile map
                // costs a log and an error function, and this is an overview.
                values[rainfallOff + cell] = rainfall.ToRainfall(
                    new Bioclim(meanTemperature, meanSeasonality, meanPrecipitation, meanCv));

                for (int c = 0; c < coarseValues.Length; c++) values[coarseOffsets[c] + cell] = coarseValues[c];

                // Köppen from the cell's mean climate, as /tdiff climate and the climate API classify a column.
                if (!seasonal) Api.ClimateRecord.ApproximateCycle(meanPrecipitation, meanCv, cycle);
                var record = new Api.ClimateRecord(meanTemperature, meanSeasonality, meanPrecipitation, meanCv,
                    (float)(elevation / n), seasonal, cycle, diurnalRange);
                values[koppenOff + cell] = record.KoppenClass;
                values[diurnalOff + cell] = float.IsNaN(diurnalRange) ? 0f : diurnalRange;
            }
        }

        (float[] min, float[] max, byte[] data) = Quantise(values, cells);

        return new RecordedTile
        {
            X = tile.BlockX / tile.Size,
            Z = tile.BlockZ / tile.Size,
            Seq = Interlocked.Increment(ref _sequence),
            Min = min,
            Max = max,
            Data = data
        };
    }

    /// <summary>
    /// Packs a tile's layers into one byte per cell, each layer against its own range, which goes
    /// alongside so the page can recover real values. A categorical layer keeps its class index as
    /// the byte. NaN and infinities become zero.
    /// </summary>
    /// <param name="values">Every layer's cells, layer-major; non-finite values are zeroed in place.</param>
    /// <param name="cells">Cells per layer.</param>
    /// <returns>Each layer's minimum and maximum, and the bytes, layer-major.</returns>
    private (float[] Min, float[] Max, byte[] Data) Quantise(float[] values, int cells)
    {
        int layerCount = _layers.Length;
        var min = new float[layerCount];
        var max = new float[layerCount];
        var data = new byte[layerCount * cells];

        for (int layer = 0; layer < layerCount; layer++)
        {
            int off = layer * cells;
            if (_layers[layer].Categorical)
            {
                min[layer] = 0f;
                max[layer] = 255f;
                for (int i = 0; i < cells; i++) data[off + i] = (byte)Math.Clamp((int)values[off + i], 0, 255);
                continue;
            }
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < cells; i++)
            {
                float value = values[off + i];
                if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
                values[off + i] = value;
                if (value < lo) lo = value;
                if (value > hi) hi = value;
            }

            min[layer] = lo;
            max[layer] = hi;

            float span = hi - lo;
            if (span <= 0f)
            {
                // Flat cell: every byte zero, and the client reads min == max and paints the floor.
                continue;
            }

            for (int i = 0; i < cells; i++)
            {
                data[off + i] = (byte)Math.Clamp((int)Math.Round((values[off + i] - lo) / span * 255f), 0, 255);
            }
        }

        return (min, max, data);
    }

    /// <summary>
    /// Keeps the history inside its budget, dropping the tiles recorded longest ago. A tile that is
    /// regenerated is re-recorded under a new sequence number, so a busy area stays.
    /// </summary>
    private void EvictOldest()
    {
        if (_tiles.Count <= _historyLimit) return;

        foreach (KeyValuePair<long, RecordedTile> entry in _tiles.OrderBy(e => e.Value.Seq))
        {
            if (_tiles.Count <= _historyLimit) return;
            _tiles.TryRemove(entry.Key, out _);
        }
    }
}
