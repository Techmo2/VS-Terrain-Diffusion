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

/// <summary>The search for a new world's spawn: inland, near sea level, and in the starting climate.</summary>
public sealed partial class TerrainDiffusionProvider
{
    /// <summary>What the spawn search settled on, for the caller to report.</summary>
    public readonly struct SpawnCandidate
    {
        /// <summary>World block X of the spawn column.</summary>
        public readonly int BlockX;

        /// <summary>World block Z of the spawn column.</summary>
        public readonly int BlockZ;

        /// <summary>Temperature of that column, as the world will read it.</summary>
        public readonly float TemperatureC;

        /// <summary>True when a starting climate was asked for and this spot is inside its band.</summary>
        public readonly bool MatchedClimate;

        /// <summary>Bundles a spawn column and how well it met the starting climate.</summary>
        public SpawnCandidate(int blockX, int blockZ, float temperatureC, bool matchedClimate)
        {
            BlockX = blockX;
            BlockZ = blockZ;
            TemperatureC = temperatureC;
            MatchedClimate = matchedClimate;
        }
    }

    /// <summary>
    /// Searches for a solidly-inland spot to put the world spawn on, since the model decides where
    /// the continents are and the map centre is just as likely to be open ocean.
    ///
    /// When the world asks for a starting climate the search also wants land in that temperature
    /// band, and keeps widening until it finds some. That is the mod's answer to a setting it
    /// otherwise breaks: vanilla honours "Starting climate" by sliding its climate map until the
    /// chosen band covers the origin, which is not available here because the model predicts one
    /// particular world rather than a climate field that can be shifted about.
    ///
    /// The survey is coarse - one cell is several kilometres across, and its temperature is the
    /// average over all of it at its average elevation. In hill country the column at the middle of
    /// a cell can be three degrees off that, which is enough to land a "temperate" spawn in the
    /// cool band, so the cells the survey likes are then checked at full resolution and the spawn
    /// is placed on a column that really is in the band.
    /// </summary>
    /// <returns>Where to put the spawn, or null if the search found only sea.</returns>
    public SpawnCandidate? FindSpawn(int initialSizeCoarsePixels = 16)
    {
        StartingClimate? band = _settings.StartingClimate;
        int coarseToNative = CoarseCellNativePixels;
        int blocksPerCoarseCell = Math.Max(1, coarseToNative * _settings.Scale);

        // Without a climate to hunt for there is no reason to look past the first land, so the
        // search keeps its old, much smaller ceiling.
        int maxBox = band == null
            ? 128
            : Math.Clamp(
                2 * DiffusionConfig.Instance.WorldGen.StartingClimateSearchRadiusBlocks / blocksPerCoarseCell,
                initialSizeCoarsePixels, 1024);

        // Best near miss seen at any box size, so a world with no land in the band still gets the
        // closest thing to it rather than nothing.
        (int Row, int Col, int Half, float TemperatureC)? fallback = null;
        float fallbackMiss = float.MaxValue;
        double fallbackCost = double.MaxValue;

        float northSouthCost = band == null ? 1f : DiffusionConfig.Instance.WorldGen.StartingClimateNorthSouthCost;

        // Doubling rather than creeping outwards: each box contains the last, so the nearest match
        // in a bigger box is never further away than the nearest match in a smaller one, and a
        // search that may have to cross a continent cannot afford a hundred passes to get there.
        for (int boxSize = initialSizeCoarsePixels; ; boxSize = Math.Min(maxBox, boxSize * 2))
        {
            int half = boxSize / 2;
            FloatTensor coarse;

            _scheduler.Enter();
            try
            {
                coarse = _pipeline.GetCoarseSlice(-half, -half, half, half);
            }
            catch (Exception e)
            {
                throw DiffusionFailure.Fatal(_logger,
                    "The coarse stage failed while looking for a spawn.", e);
            }
            finally
            {
                _scheduler.Exit();
            }

            int h = boxSize, w = boxSize;
            int plane = h * w;

            // The cheapest few in-band cells rather than only the cheapest, because the survey is
            // an average and the winner may turn out not to hold an in-band column at all.
            var shortlist = new List<(double Cost, int Row, int Col, float TemperatureC)>();

            for (int r = 1; r < h - 1; r++)
            {
                for (int c = 1; c < w - 1; c++)
                {
                    // Land, with ground near sea level somewhere in it.
                    if (!IsLowlandCell(coarse, plane, w, r, c)) continue;

                    int dr = r - half, dc = c - half;
                    if (!InsideWorld(dr, dc, coarseToNative)) continue;

                    double cost = SpawnCost(dr, dc, northSouthCost);
                    int cellBlockZ = _settings.OriginBlockZ + dr * blocksPerCoarseCell;
                    int cellBlockX = _settings.OriginBlockX + dc * blocksPerCoarseCell;
                    float temperature = CellArrivalTemperature(coarse, plane, w, r, c, cellBlockX, cellBlockZ);

                    if (band == null)
                    {
                        if (shortlist.Count > 0 && cost >= shortlist[0].Cost) continue;
                        shortlist.Clear();
                        shortlist.Add((cost, r, c, temperature));
                        continue;
                    }

                    float miss = band.Value.Miss(temperature);
                    if (miss <= 0f)
                    {
                        shortlist.Add((cost, r, c, temperature));
                    }
                    else if (miss < fallbackMiss || (miss == fallbackMiss && cost < fallbackCost))
                    {
                        fallbackMiss = miss;
                        fallbackCost = cost;
                        fallback = (r, c, half, temperature);
                    }
                }
            }

            if (shortlist.Count > 0)
            {
                _logger.Notification("[{0}] Spawn search scanned a {1}x{1} coarse box ({2} blocks across).",
                    DiffusionPaths.ModId, boxSize, boxSize * blocksPerCoarseCell);

                shortlist.Sort((a, b) => a.Cost.CompareTo(b.Cost));
                SpawnCandidate? refined = RefineWithinCells(shortlist, half, band, northSouthCost);
                if (refined != null) return refined;

                // Every cell the survey liked turned out to be wrong about itself. Widening will
                // not help - the next box contains these same cells - so take the survey's word.
                _logger.VerboseDebug("[{0}] No column in the surveyed cells was actually in band.",
                    DiffusionPaths.ModId);
                return ToCandidate(shortlist[0].Row, shortlist[0].Col, half, shortlist[0].TemperatureC, false);
            }

            if (boxSize >= maxBox) break;
            _logger.VerboseDebug("[{0}] Nothing suitable in a {1}x{1} coarse box; widening the spawn search.",
                DiffusionPaths.ModId, boxSize);
        }

        if (fallback == null) return null;

        // Every box has been searched and no land in the band exists within reach. The nearest
        // temperature to it is still a far better spawn than dropping the player at the origin;
        // still on low ground, the column of that cell closest to the map centre.
        _logger.Notification(
            "[{0}] Spawn search covered {1} blocks around the map centre without finding {2}.",
            DiffusionPaths.ModId, maxBox * blocksPerCoarseCell, band?.ToString() ?? "land");
        var nearest = new List<(double Cost, int Row, int Col, float TemperatureC)>
            { (0, fallback.Value.Row, fallback.Value.Col, fallback.Value.TemperatureC) };
        SpawnCandidate? low = RefineWithinCells(nearest, fallback.Value.Half, null, northSouthCost);
        return low is { } found
            ? new SpawnCandidate(found.BlockX, found.BlockZ, found.TemperatureC, false)
            : ToCandidate(fallback.Value.Row, fallback.Value.Col, fallback.Value.Half, fallback.Value.TemperatureC, false);
    }

    /// <summary>
    /// How many of the surveyed cells are checked at full resolution before the search gives up and
    /// trusts the survey. Usually the first one is right, and the tile it generates is one the
    /// spawn needs anyway, so the common case costs nothing; the rest are insurance against a cell
    /// whose average hides its own terrain.
    /// </summary>
    private const int MaxRefinedCells = 8;

    /// <summary>
    /// Looks inside the surveyed cells at full resolution and returns the nearest land column that
    /// really is in the band, or null if none of them holds one.
    /// </summary>
    private SpawnCandidate? RefineWithinCells(
        List<(double Cost, int Row, int Col, float TemperatureC)> shortlist, int half,
        StartingClimate? band, float northSouthCost)
    {
        int examined = 0;
        foreach ((double _, int row, int col, float _) in shortlist)
        {
            if (examined++ >= MaxRefinedCells) break;

            SpawnCandidate centre = ToCandidate(row, col, half, 0f, false);
            TerrainTile tile;
            try
            {
                tile = GetTileAt(centre.BlockX, centre.BlockZ);
            }
            catch (Exception e)
            {
                throw DiffusionFailure.Fatal(_logger,
                    $"The model failed on the spawn search climate check at " +
                    $"({centre.BlockX}, {centre.BlockZ}).", e);
            }

            SpawnCandidate? best = BestColumnInTile(tile, band, northSouthCost);
            if (best != null) return best;
        }
        return null;
    }

    /// <summary>
    /// The land column in a tile that stands near sea level, is in the band (any, without one) on
    /// the day a new world starts, and costs least to walk to from the map centre; null if none.
    /// </summary>
    private SpawnCandidate? BestColumnInTile(TerrainTile tile, StartingClimate? band, float northSouthCost)
    {
        int seaLevel = _settings.SeaLevel;
        int maxAbove = DiffusionConfig.Instance.WorldGen.SpawnMaxBlocksAboveSea;
        float lapsePerBlock = ClimateScale.ReferenceLapseCPerKm * _settings.AltitudeCooling / 1000f
                              * _settings.MetersPerBlockVertical;
        // The year's shape varies over kilometres, so one reading of it serves the tile.
        SeasonalCycleMap.Sample? cycle = ArrivalCycle(tile.BlockX + tile.Size / 2, tile.BlockZ + tile.Size / 2);

        SpawnCandidate? best = null;
        double bestCost = double.MaxValue;
        for (int z = 0; z < tile.Size; z++)
        {
            int blockZ = tile.BlockZ + z;
            for (int x = 0; x < tile.Size; x++)
            {
                int index = tile.Index(x, z);
                if (tile.ElevationMeters[index] <= 0f) continue;
                int blockX = tile.BlockX + x;
                if (!_settings.IsInsideWorld(blockX, blockZ)) continue;

                int surfaceY = BuiltSurfaceY(tile.SurfaceY[index], blockX, blockZ);
                int above = surfaceY - seaLevel;
                if (above < 0 || above > maxAbove) continue;

                double cost = SpawnCost(blockZ - _settings.OriginBlockZ, blockX - _settings.OriginBlockX,
                                        northSouthCost);
                if (cost >= bestCost) continue;

                // What the game reads at sea level here on the first day: the column's temperature
                // taken down at the game's own lapse rate, then swung into the season.
                float seaLevelC = tile.TemperatureC[index] + lapsePerBlock * above;
                float temperature = ArrivalTemperature(seaLevelC, tile.TemperatureSeasonality[index] / 100f, blockZ, cycle);
                if (band != null && !band.Value.Contains(temperature)) continue;

                bestCost = cost;
                best = new SpawnCandidate(blockX, blockZ, temperature, band != null);
            }
        }
        return best;
    }

    /// <summary>
    /// When a new world starts, set by the mod system before the spawn search (it knows the
    /// calendar): the month, 0 to 12, of the first day's middle; which way round the year runs at a
    /// block Z; and how strongly the seasons swing. Null judges places by their annual mean.
    /// </summary>
    public (double Month, System.Func<int, bool> Southern, float Strength, System.Func<int, double> WinterGain)? ArrivalSeason { get; set; }

    /// <summary>The seasonal model's year shape at a column, or null (annual model, or no reading).</summary>
    private SeasonalCycleMap.Sample? ArrivalCycle(int blockX, int blockZ)
    {
        if (!_pipeline.CoarseSpec.HasSeasonalCycle) return null;
        Span<float> channels = stackalloc float[_pipeline.CoarseSpec.Channels];
        if (!CoarseChannelsAt(blockX, blockZ, channels)) return null;
        Span<float> shape = stackalloc float[8];
        shape[0] = channels[CoarseChannel.TemperatureShapeCos];
        shape[1] = channels[CoarseChannel.TemperatureShapeSin];
        return new SeasonalCycleMap.Sample(shape);
    }

    /// <summary>
    /// A sea-level annual temperature as a player arriving on a new world's first day reads it (the
    /// day's mean, before the hour and the weather): what the starting climate is judged by.
    /// </summary>
    private float ArrivalTemperature(float seaLevelAnnualC, float sigmaC, int blockZ, SeasonalCycleMap.Sample? cycle)
    {
        if (ArrivalSeason is not { } season) return seaLevelAnnualC;
        return seaLevelAnnualC + (float)DiffusionSeasons.SeasonalDeviation(
            season.Month, season.Southern(blockZ), sigmaC, season.Strength, cycle, season.WinterGain(blockZ));
    }

    /// <summary>
    /// How reluctant the search is to use a cell this far from the origin. North-south distance
    /// costs more than east-west: in Vintage Story the Z coordinate alone sets latitude, and past
    /// the world's polar distance that means midnight sun in summer and no dawn at all in winter,
    /// while travelling east is free. Only applied when hunting for a climate, since the plain
    /// land search never goes far enough for it to matter.
    /// </summary>
    private static double SpawnCost(int dr, int dc, float northSouthCost)
    {
        double northSouth = dr * northSouthCost;
        return northSouth * northSouth + (double)dc * dc;
    }

    /// <summary>
    /// Whether a coarse cell is land with ground near sea level somewhere in it: its mean above
    /// sea, and its fifth percentile within twice the spawn's height limit plus a little (the
    /// percentile is itself an average over the cell).
    /// </summary>
    private bool IsLowlandCell(FloatTensor coarse, int plane, int width, int row, int col)
    {
        int index = row * width + col;
        float weight = coarse.Data[_pipeline.CoarseSpec.WeightChannel * plane + index];
        if (weight <= 1e-6f) return false;
        float meanRoot = coarse.Data[index] / weight;
        if (meanRoot <= 0f) return false;
        float p5Root = meanRoot - coarse.Data[plane + index] / weight;
        float p5 = Math.Sign(p5Root) * p5Root * p5Root;
        float limit = (DiffusionConfig.Instance.WorldGen.SpawnMaxBlocksAboveSea * 2 + 4) * _settings.MetersPerBlockVertical;
        return p5 <= limit;
    }

    /// <summary>
    /// A coarse cell's temperature as a player arriving on the first day would read it at sea
    /// level: the cell's mean, carried down from its mean elevation at the game's lapse rate,
    /// through the world's settings, and swung into the season.
    /// </summary>
    private float CellArrivalTemperature(FloatTensor coarse, int plane, int width, int row, int col, int blockX, int blockZ)
    {
        int index = row * width + col;
        float weight = coarse.Data[_pipeline.CoarseSpec.WeightChannel * plane + index];
        if (weight <= 1e-6f) return 0f;
        float root = Math.Max(0f, coarse.Data[index] / weight);
        float seaLevel = _settings.WorldTemperature(CellTemperature(coarse, plane, width, row, col), blockZ)
                         + ClimateScale.ReferenceLapseCPerKm * _settings.AltitudeCooling / 1000f * root * root;
        float sigmaC = coarse.Data[CoarseChannel.TemperatureSeasonality * plane + index] / weight / 100f;
        SeasonalCycleMap.Sample? cycle = null;
        if (_pipeline.CoarseSpec.HasSeasonalCycle)
        {
            Span<float> shape = stackalloc float[8];
            shape[0] = coarse.Data[CoarseChannel.TemperatureShapeCos * plane + index] / weight;
            shape[1] = coarse.Data[CoarseChannel.TemperatureShapeSin * plane + index] / weight;
            cycle = new SeasonalCycleMap.Sample(shape);
        }
        return ArrivalTemperature(seaLevel, sigmaC, blockZ, cycle);
    }

    /// <summary>Mean temperature of a coarse cell, in degrees, straight from the model.</summary>
    private float CellTemperature(FloatTensor coarse, int plane, int width, int row, int col)
    {
        int index = row * width + col;
        float weight = coarse.Data[_pipeline.CoarseSpec.WeightChannel * plane + index];
        if (weight <= 1e-6f) return 0f;

        // The coarse model's temperature is at its own ground, on the model's lapse rate; altitude
        // cooling steepens that, which the survey takes at the reference rate rather than fitting it.
        float temperature = coarse.Data[2 * plane + index] / weight;
        float elevation = Math.Max(0f, coarse.Data[index] / weight);
        return temperature - (_settings.AltitudeCooling - 1f) * ClimateScale.ReferenceLapseCPerKm / 1000f
                             * elevation * elevation;
    }

    /// <summary>Whether a coarse cell at this offset from the origin is inside the world's bounds.</summary>
    private bool InsideWorld(int dr, int dc, int coarseToNative)
    {
        (int x, int z) = ToWorldBlocks(dr * coarseToNative + coarseToNative / 2,
                                       dc * coarseToNative + coarseToNative / 2);
        return _settings.IsInsideWorld(x, z);
    }

    /// <summary>The world column at the centre of a cell of a coarse box, as a spawn candidate.</summary>
    /// <param name="row">Cell row in the box.</param>
    /// <param name="col">Cell column in the box.</param>
    /// <param name="half">Half the box's edge: the origin's row and column.</param>
    /// <param name="temperatureC">Temperature to report.</param>
    /// <param name="matchedClimate">Whether it is in the starting climate band.</param>
    private SpawnCandidate ToCandidate(int row, int col, int half, float temperatureC, bool matchedClimate)
    {
        // Centre of the chosen coarse cell, in native pixels, then in world blocks.
        int coarseToNative = CoarseCellNativePixels;
        int nativeZ = (row - half) * coarseToNative + coarseToNative / 2;
        int nativeX = (col - half) * coarseToNative + coarseToNative / 2;

        (int x, int z) = ToWorldBlocks(nativeZ, nativeX);
        return new SpawnCandidate(x, z, temperatureC, matchedClimate);
    }

    /// <summary>Converts a native model pixel to the world block column it covers.</summary>
    private (int X, int Z) ToWorldBlocks(int nativeI, int nativeJ) =>
        (nativeJ * _settings.Scale + _settings.OriginBlockX,
         nativeI * _settings.Scale + _settings.OriginBlockZ);
}
