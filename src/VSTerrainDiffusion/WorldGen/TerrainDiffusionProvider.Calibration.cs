using System;
using System.Collections.Generic;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Tensors;
using static VSTerrainDiffusion.Core.IntMath;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>The terrain height survey behind <see cref="HeightCalibration"/>.</summary>
public sealed partial class TerrainDiffusionProvider
{
    /// <summary>Below this many land cells the coarse survey is too small to say anything useful.</summary>
    private const int MinimumSurveyedLandCells = 16;

    /// <summary>Native pixels along one edge of a full-detail probe.</summary>
    private const int ProbeNativePixels = 128;

    /// <summary>
    /// Measures how tall the terrain around spawn actually gets, so the metre-to-block mapping can
    /// be fitted to it: a cheap survey of the coarse stage over the play area, then a handful of
    /// full-detail probes on its tallest cells to recover the summits that kilometre-scale
    /// averaging hides.
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
        List<(float Meters, int Row, int Col)> cells;

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
                    (col - half + centerJ) * coarseToNative + coarseToNative / 2,
                    ProbeNativePixels);
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
        _scheduler.Enter();
        try
        {
            coarse = _pipeline.GetCoarseSlice(centerI - half, centerJ - half, centerI + half, centerJ + half);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_logger,
                "The coarse stage failed during the terrain height survey.", e);
        }
        finally
        {
            _scheduler.Exit();
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
                float weight = coarse.Data[_pipeline.CoarseSpec.WeightChannel * plane + index];
                if (weight <= 1e-6f) continue;

                float value = coarse.Data[index] / weight;
                if (value <= 0f) continue;
                cells.Add((value * value, row, col));
            }
        }
        return cells;
    }

    /// <summary>Runs the full pipeline over a small window, or null if it could not be sampled.</summary>
    private float[] Probe(int centerI, int centerJ, int windowPixels)
    {
        int half = windowPixels / 2;

        _scheduler.Enter();
        try
        {
            return _pipeline.Get(centerI - half, centerJ - half, centerI + half, centerJ + half, false).Elevation;
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_logger,
                $"The model failed on the terrain probe at native ({centerJ}, {centerI}).", e);
        }
        finally
        {
            _scheduler.Exit();
        }
    }

    /// <summary>
    /// Index, in a list sorted from highest to lowest, of the value that only
    /// <paramref name="fraction"/> of the list exceeds.
    /// </summary>
    private static int TailIndex(int count, float fraction)
        => Math.Clamp((int)(fraction * count), 0, count - 1);
}
