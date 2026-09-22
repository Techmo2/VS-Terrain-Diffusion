using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Tells the coarse model which of its cells a river system runs through, so it puts low ground
/// there instead of a ridge.
///
/// The rivers are worked out first and the landscape second, which is the right way round and is
/// only possible because Rivers routes from the ocean map rather than from terrain - there is no
/// circularity in letting it decide where the valleys go. Without this the channels are cut into
/// whatever the model happened to produce, which is how a river ends up crossing a mountain.
///
/// It describes a basin, not a channel. One coarse cell is 512 blocks at the default scale against
/// a river tens of blocks wide, and nothing finer can be conditioned - so what the model is told is
/// "a river system runs through this half kilometre", and what comes back is ground low enough to
/// have carried one. The channel is still carved afterwards, in <see cref="GenDiffusionTerra"/>.
/// </summary>
public sealed class RiverBasinMap : IRiverBasinSource
{
    private readonly int _blocksPerCoarse;
    private readonly int _originBlockX;
    private readonly int _originBlockZ;
    private readonly ILogger _logger;
    private bool _warned;

    /// <summary>
    /// Samples per cell on each axis, taken at the quarter points.
    ///
    /// Rivers indexes its segments with <c>riverPaddingBlocks</c> (128) of slack, so one sample
    /// sees a river within about that distance. Four samples at the quarter points of a 512-block
    /// cell are 128 from each edge, which covers the whole cell; a 3x3 grid was covering it twice
    /// over for more than twice the work, and this is the most expensive thing that happens while
    /// the inference scheduler is held.
    /// </summary>
    private const int SamplesPerAxis = 2;

    public RiverBasinMap(DiffusionWorldSettings settings, ILogger logger)
    {
        _blocksPerCoarse = 32 * WorldPipelineModelConfig.Instance.LatentCompression * settings.Scale;
        _originBlockX = settings.OriginBlockX;
        _originBlockZ = settings.OriginBlockZ;
        _logger = logger;
    }

    public float[] BasinStrength(int x1, int y1, int x2, int y2)
    {
        if (!RiversCompat.CanSampleNetwork) return null;

        int w = x2 - x1, h = y2 - y1;
        if (w <= 0 || h <= 0) return null;

        var result = new float[w * h];
        int step = Math.Max(1, _blocksPerCoarse / SamplesPerAxis);

        // One resolve per chunk instead of one per sample: each is an R-tree search that allocates,
        // and the first touch of a plate generates a whole river region.
        var contexts = new Dictionary<long, object>();

        // A river anywhere within a cell of the centre counts, falling off to nothing a cell out.
        // Wider than the cell because a valley does not stop at a conditioning boundary, and the
        // model reads neighbouring cells together anyway.
        double reach = _blocksPerCoarse;

        for (int r = 0; r < h; r++)
        {
            int cellZ = _originBlockZ + (y1 + r) * _blocksPerCoarse;
            for (int c = 0; c < w; c++)
            {
                int cellX = _originBlockX + (x1 + c) * _blocksPerCoarse;

                double nearest = double.MaxValue;
                for (int sz = 0; sz < SamplesPerAxis; sz++)
                {
                    for (int sx = 0; sx < SamplesPerAxis; sx++)
                    {
                        int sampleX = cellX + sx * step + step / 2;
                        int sampleZ = cellZ + sz * step + step / 2;

                        long chunkKey = ((long)(sampleX >> 5) << 32) ^ (uint)(sampleZ >> 5);
                        if (!contexts.TryGetValue(chunkKey, out object context))
                        {
                            context = RiversCompat.ChunkContext(sampleX >> 5, sampleZ >> 5);
                            contexts[chunkKey] = context;
                        }

                        double distance = RiversCompat.DistanceToRiver(context, sampleX, sampleZ);
                        if (distance < 0.0) continue;
                        if (distance < nearest) nearest = distance;
                    }
                }

                if (nearest == double.MaxValue)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        _logger?.Warning(
                            "[{0}] The river network could not be read while shaping the land for it; " +
                            "rivers will be cut into terrain that does not expect them.",
                            DiffusionPaths.ModId);
                    }
                    continue;
                }

                // Inside a channel is full strength; the valley fades out from there.
                double strength = nearest <= 0.0 ? 1.0 : 1.0 - Math.Min(1.0, nearest / reach);
                result[r * w + c] = (float)strength;
            }
        }

        return result;
    }
}
