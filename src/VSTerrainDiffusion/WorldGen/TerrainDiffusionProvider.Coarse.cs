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

/// <summary>Queries answered by the coarse model alone, without generating a tile.</summary>
public sealed partial class TerrainDiffusionProvider
{
    /// <summary>The world seed, for the river-free pipeline.</summary>
    private readonly ulong _seed;

    /// <summary>The loaded models; the river-free pipeline shares all but the coarse one.</summary>
    private readonly PipelineModels _models;

    /// <summary>
    /// The ocean map conditioning, for the river-free pipeline; null when the model draws the coast.
    /// </summary>
    private readonly ILandmaskSource _landmask;

    /// <summary>
    /// Guards the river-free pipeline, which does not take the scheduler (see <see cref="IsCoarseSea"/>).
    /// </summary>
    private readonly object _seaGate = new();

    /// <summary>
    /// The coarse pipeline without river basins behind <see cref="IsCoarseSea"/>, built on first use.
    /// </summary>
    private WorldPipeline _riverFreePipeline;

    /// <summary>
    /// The river-free pipeline's own coarse model session, so it never waits on the main pipeline's.
    /// </summary>
    private IModelRunner _riverFreeCoarse;

    /// <summary>
    /// Converts a place in world blocks to coarse model pixels: I along Z, J along X, the grid
    /// <see cref="CoarseConditioningSite"/> works in. Pooling makes the model's grid finer than the map's.
    /// </summary>
    public static (float I, float J, float PerBlock) CoarseModelPixel(DiffusionWorldSettings settings, double blockX, double blockZ)
    {
        float perBlock = settings.CoarsePooling.Factor / (float)(Math.Max(1, settings.Scale) * CoarseCellNativePixels);
        return ((float)((blockZ - settings.OriginBlockZ) * perBlock), (float)((blockX - settings.OriginBlockX) * perBlock), perBlock);
    }

    /// <summary>
    /// Everything the coarse stage was told and everything it answered at one world position, for
    /// the debug map.
    ///
    /// Read in one go under a single turn on the device, because it is asked once per terrain tile
    /// and a coarse cell - 512 blocks at the default scale - is wider than a tile, so there is no
    /// detail below this to lose.
    /// </summary>
    public readonly struct CoarseProbe
    {
        /// <summary>What the model was asked for: elevation m, temperature C, BIO4, mm, BIO15.</summary>
        public readonly float[] Conditioning;

        /// <summary>What it produced, in the same order and units.</summary>
        public readonly float[] Output;

        /// <summary>How much of the cell the world's ocean map calls sea, or -1 without one.</summary>
        public readonly float SeaFraction;

        /// <summary>Bundles one probe's readings.</summary>
        /// <param name="conditioning">What the model was asked for.</param>
        /// <param name="output">What it produced.</param>
        /// <param name="seaFraction">Sea share of the cell, or -1.</param>
        public CoarseProbe(float[] conditioning, float[] output, float seaFraction)
        {
            Conditioning = conditioning;
            Output = output;
            SeaFraction = seaFraction;
        }
    }

    /// <summary>
    /// What the coarse stage was told and what it answered in the cell containing a block column.
    /// </summary>
    /// <param name="blockX">Column X.</param>
    /// <param name="blockZ">Column Z.</param>
    /// <returns>The probe, or null where the cell has no weight or the coarse stage failed.</returns>
    public CoarseProbe? ProbeCoarseAt(int blockX, int blockZ)
    {
        int coarseToNative = CoarseCellNativePixels;
        int scale = Math.Max(1, _settings.Scale);
        int i = FloorDiv((blockZ - _settings.OriginBlockZ) / scale, coarseToNative);
        int j = FloorDiv((blockX - _settings.OriginBlockX) / scale, coarseToNative);

        _scheduler.Enter();
        try
        {
            FloatTensor coarse = _pipeline.GetCoarseSlice(i, j, i + 1, j + 1);

            // The last channel is the blend weight every other channel has to be divided by.
            float weight = coarse.Data[_pipeline.CoarseSpec.WeightChannel];
            if (weight <= 1e-6f) return null;

            float elevation = coarse.Data[0] / weight;
            var output = new[]
            {
                Math.Sign(elevation) * elevation * elevation,   // signed square-root space
                coarse.Data[2] / weight,                        // BIO1 mean annual temperature
                coarse.Data[3] / weight,                        // BIO4 temperature seasonality
                coarse.Data[4] / weight,                        // BIO12 annual precipitation
                coarse.Data[5] / weight                         // BIO15 precipitation CV
            };

            return new CoarseProbe(
                _pipeline.CoarseConditioningAt(i, j), output, _pipeline.SeaFractionAt(i, j) ?? -1f);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _scheduler.Exit();
        }
    }

    /// <summary>The coarse model this world runs, and so which channels <see cref="CoarseChannelsAt"/> fills.</summary>
    public CoarseModelSpec CoarseSpec => _pipeline.CoarseSpec;

    /// <summary>
    /// Every channel of the coarse model at a block column, in the units of <see cref="CoarseChannel"/>
    /// except that elevation and its fifth percentile are metres. Bilinear between coarse cell
    /// centres, as the pipeline samples its climate. The coarse map is the model's raw output: none
    /// of the world's climate settings are in it (<see cref="TerrainTile"/> has those for the four
    /// annual channels). False on failure.
    /// </summary>
    public bool CoarseChannelsAt(double blockX, double blockZ, Span<float> channels)
    {
        CoarseCellAt(blockX, blockZ, out int i0, out int j0, out float fi, out float fj);
        int count = _pipeline.CoarseSpec.Channels;
        Span<float> corners = stackalloc float[4 * count];
        if (!CoarseCorners(i0, j0, corners)) return false;
        InterpolateCorners(corners, count, fi, fj, channels);
        return true;
    }

    /// <summary>
    /// The coarse cell whose centre is at or before a block column on both axes, and how far the
    /// column lies towards the next centres (0 to 1): the 2x2 cells <see cref="CoarseCorners"/>
    /// reads, and the weights to blend them with.
    /// </summary>
    public void CoarseCellAt(double blockX, double blockZ, out int i0, out int j0, out float fi, out float fj)
    {
        int scale = Math.Max(1, _settings.Scale);
        int cells = CoarseCellNativePixels;
        double ci = ((blockZ - _settings.OriginBlockZ) / scale + 0.5) / cells - 0.5;
        double cj = ((blockX - _settings.OriginBlockX) / scale + 0.5) / cells - 0.5;
        i0 = (int)Math.Floor(ci);
        j0 = (int)Math.Floor(cj);
        fi = (float)(ci - i0);
        fj = (float)(cj - j0);
    }

    /// <summary>
    /// The coarse channels of the 2x2 cells from (i0, j0), decoded to real units: corner k (0..3,
    /// row-major) of channel c at <c>c * 4 + k</c>. False if the coarse stage failed or a cell is
    /// missing.
    /// </summary>
    public bool CoarseCorners(int i0, int j0, Span<float> corners)
    {
        int count = _pipeline.CoarseSpec.Channels, weightBase = 4 * _pipeline.CoarseSpec.WeightChannel;
        float[] data;
        _scheduler.Enter();
        try
        {
            data = _pipeline.GetCoarseSlice(i0, j0, i0 + 2, j0 + 2).Data;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _scheduler.Exit();
        }

        for (int ch = 0; ch < count && 4 * ch + 3 < corners.Length; ch++)
        {
            for (int k = 0; k < 4; k++)
            {
                float weight = data[weightBase + k];
                if (weight <= 1e-6f) return false;
                float value = data[ch * 4 + k] / weight;
                if (ch == CoarseChannel.Elevation)
                {
                    value = Math.Sign(value) * value * value;
                }
                else if (ch == CoarseChannel.ElevationP5)
                {
                    // Stored as elevation minus the fifth percentile, both in square-root metres.
                    float root = data[k] / weight - value;
                    value = Math.Sign(root) * root * root;
                }
                corners[ch * 4 + k] = value;
            }
        }
        return true;
    }

    /// <summary>Blends <see cref="CoarseCorners"/> output at fractions (fi, fj) into one value per channel.</summary>
    public static void InterpolateCorners(ReadOnlySpan<float> corners, int count, float fi, float fj, Span<float> channels)
    {
        for (int ch = 0; ch < count && ch < channels.Length; ch++)
        {
            int c = ch * 4;
            channels[ch] = (1 - fi) * ((1 - fj) * corners[c] + fj * corners[c + 1])
                           + fi * ((1 - fj) * corners[c + 2] + fj * corners[c + 3]);
        }
    }

    /// <summary>Edge of one coarse cell in blocks: 512 at the default scale.</summary>
    public int CoarseCellBlocks => Math.Max(1, _settings.Scale) * CoarseCellNativePixels;

    /// <summary>Native pixels along one edge of a coarse cell.</summary>
    private static int CoarseCellNativePixels => 32 * WorldPipelineModelConfig.Instance.LatentCompression;

    /// <summary>
    /// Whether the coarse model puts sea at a block position, for Rivers to route from in place of
    /// the world's ocean map. The model follows that map only to within a coarse cell and sometimes
    /// overrides it outright, so rivers routed from the map end on the model's dry land.
    ///
    /// Answered by a second pipeline that is conditioned on everything the main one is except the
    /// river basins, because the basins come from the very network this is building. They only ever
    /// lower land towards sea level and never past it, so which side of the coast a place is on
    /// comes out the same. The pipeline has a coarse model of its own and a lock of its own, and
    /// does not take the scheduler: Rivers builds a region inside a lock while the main pipeline can
    /// be waiting on that region with the scheduler held, and a shared model or the scheduler here
    /// would deadlock the two. Its work is not abandoned by preemption either, since Rivers keeps a
    /// failure for the life of the region.
    ///
    /// The coarse elevation is interpolated between the four cells around the position, in the
    /// model's own square-root units, so each Rivers zone - half a cell - gets its own answer.
    /// </summary>
    public bool IsCoarseSea(int blockX, int blockZ)
    {
        lock (_seaGate)
        {
            using IDisposable noPreemption = InferencePreemption.Suspend();
            if (_riverFreePipeline == null)
            {
                _riverFreeCoarse = new OnnxModel(_models.Worker, ModelAssetManager.ResolveCoarsePath(_logger), "coarse", _logger);
                _riverFreePipeline = new WorldPipeline(_seed, _models.WithCoarse(_riverFreeCoarse), _landmask,
                                                       _settings.Climate, _settings.Latitude,
                                                       pooling: _settings.CoarsePooling,
                                                       baseRenoiseSigma: _settings.BaseRenoiseSigma,
                                                       coarseHeightNoise: _settings.CoarseHeightNoise,
                                                       altitudeCooling: _settings.AltitudeCooling,
                                                       conditioningSites: _conditioningSites,
                                                       coastFill: _settings.CoastFill)
                {
                    CoarseWindowStore = _store?.CoarseWindows(RiverFreeCoarseVariant)
                };
            }
            return CoarseElevationRoot(_riverFreePipeline, blockX, blockZ) < 0.0;
        }
    }

    /// <summary>
    /// A pipeline's coarse elevation at a block position, in the model's own signed-square-root
    /// units, interpolated between the four cells around it. The caller holds whatever guards
    /// <paramref name="pipeline"/>.
    /// </summary>
    private double CoarseElevationRoot(WorldPipeline pipeline, int blockX, int blockZ)
    {
        int scale = Math.Max(1, _settings.Scale);
        double ci = (double)(blockZ - _settings.OriginBlockZ) / scale / CoarseCellNativePixels - 0.5;
        double cj = (double)(blockX - _settings.OriginBlockX) / scale / CoarseCellNativePixels - 0.5;
        int i0 = (int)Math.Floor(ci), j0 = (int)Math.Floor(cj);
        double fi = ci - i0, fj = cj - j0;

        // Channel 0 is the blended elevation and the last channel the blend weight it is divided by.
        float[] data = pipeline.GetCoarseSlice(i0, j0, i0 + 2, j0 + 2).Data;
        int weight = 4 * pipeline.CoarseSpec.WeightChannel;
        double Cell(int r, int c)
        {
            int k = r * 2 + c;
            return data[weight + k] > 1e-6f ? data[k] / data[weight + k] : 0.0;
        }
        return (1 - fi) * ((1 - fj) * Cell(0, 0) + fj * Cell(0, 1)) +
               fi * ((1 - fj) * Cell(1, 0) + fj * Cell(1, 1));
    }
}
