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

/// <summary>Column-level readouts for diagnostics and other mods: built surface heights and climate workings.</summary>
public sealed partial class TerrainDiffusionProvider
{
    /// <summary>
    /// One column's climate as the model produced it, before the world's own corrections. Only the
    /// diagnostic commands ask for this; a tile keeps the finished numbers and not the workings.
    /// </summary>
    public readonly struct ColumnDetail
    {
        /// <summary>Elevation in metres, as the pipeline reports it for the model pixel.</summary>
        public readonly float ElevationMeters;

        /// <summary>Mean annual temperature at the surface, straight from the model.</summary>
        public readonly float SurfaceTemperatureC;

        /// <summary>The same with the altitude taken back out: the column's sea-level baseline.</summary>
        public readonly float SeaLevelTemperatureC;

        /// <summary>The local lapse rate the model fitted here, in degrees per kilometre (positive).</summary>
        public readonly float LapseRateKPerKm;

        /// <summary>Bundles one column's climate workings.</summary>
        public ColumnDetail(float elevationMeters, float surfaceTemperatureC,
                            float seaLevelTemperatureC, float lapseRateKPerKm)
        {
            ElevationMeters = elevationMeters;
            SurfaceTemperatureC = surfaceTemperatureC;
            SeaLevelTemperatureC = seaLevelTemperatureC;
            LapseRateKPerKm = lapseRateKPerKm;
        }
    }

    /// <summary>
    /// Reads back the workings behind one column's temperature: what the model said at the surface,
    /// what that is with the altitude removed, and the lapse rate it used to get between them.
    ///
    /// Sea-level temperature is the number worth having when checking a latitude band, because the
    /// band is a statement about a belt and a mountain in that belt is legitimately colder than it.
    /// It costs a pipeline query rather than a tile lookup - the tile keeps the answer, not the
    /// arithmetic - which is affordable here because nothing but a typed command calls it, and the
    /// caches for a chunk somebody is standing in are already warm.
    /// </summary>
    /// <returns>Null if the pipeline could not be sampled.</returns>
    public ColumnDetail? GetColumnDetail(int worldBlockX, int worldBlockZ)
    {
        int scale = Math.Max(1, _settings.Scale);
        int i = FloorDiv(worldBlockZ - _settings.OriginBlockZ, scale);
        int j = FloorDiv(worldBlockX - _settings.OriginBlockX, scale);

        WorldPipeline.Sample sample;
        _scheduler.Enter();
        try
        {
            sample = _pipeline.Get(i, j, i + 1, j + 1, true);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_logger,
                $"The model failed while reading the column at ({worldBlockX}, {worldBlockZ}).", e);
        }
        finally
        {
            _scheduler.Exit();
        }

        if (sample.Climate == null) return null;

        // Channel 4 of the climate sample is the local lapse rate in degrees per metre, negative
        // going up, and the surface value was built as baseline + lapse * elevation.
        float elevation = sample.Elevation[0];
        float surface = sample.Climate[0];
        float lapse = sample.Climate[4];
        float seaLevel = surface - lapse * Math.Max(0f, elevation);

        return new ColumnDetail(elevation, surface, seaLevel, -lapse * 1000f);
    }

    /// <summary>
    /// The top solid block this mod builds a column to, for mods that predict terrain without
    /// generating it (see <see cref="TerrainSamplerCompat"/>): the model's surface, brought
    /// down into Rivers' valley and channel exactly as <see cref="GenDiffusionTerra"/> does. Only the
    /// smoothing against chunks that already exist is left out, since a prediction is of fresh
    /// terrain.
    ///
    /// <see cref="SurfaceSampleMode.Coarse"/> answers from the coarse model instead of the terrain
    /// tile: blurred to one coarse cell, 512 blocks at the default scale, but it never runs the base
    /// model or the decoder, which is what a tile costs for ground not yet generated.
    /// <see cref="SurfaceSampleMode.Stored"/> answers from the tile where one is in memory or the
    /// tile database, and from the coarse model where not, so it never generates a tile either.
    /// </summary>
    public int SampleSurfaceY(int blockX, int blockZ, SurfaceSampleMode mode)
    {
        TerrainTile tile = mode switch
        {
            SurfaceSampleMode.Full => GetTileAt(blockX, blockZ),
            SurfaceSampleMode.Stored => TryGetStoredTileAt(blockX, blockZ),
            _ => null
        };

        int y;
        if (tile == null)
        {
            double root;
            _scheduler.Enter();
            try
            {
                root = CoarseElevationRoot(_pipeline, blockX, blockZ);
            }
            finally
            {
                _scheduler.Exit();
            }
            y = _settings.ElevationToBlockY((float)(Math.Sign(root) * root * root));
        }
        else
        {
            y = tile.SurfaceY[tile.Index(blockX - tile.BlockX, blockZ - tile.BlockZ)];
        }

        return BuiltSurfaceY(y, blockX, blockZ);
    }

    /// <summary>
    /// The model's surface <paramref name="modelY"/> at a column, brought down into Rivers' valley
    /// and channel as <see cref="GenDiffusionTerra"/> builds it: the ground the world actually has
    /// there. The model surface itself outside a river valley, or without Rivers.
    /// </summary>
    public int BuiltSurfaceY(int modelY, int blockX, int blockZ) =>
        BuiltSurfaceY(modelY, blockX, blockZ, RiversCompat.Installed ? RiversCompat.SampleAt(blockX, blockZ) : null);

    /// <summary>
    /// <see cref="BuiltSurfaceY(int, int, int)"/> with Rivers' sample for the column already taken,
    /// or null where there is no river network.
    /// </summary>
    public int BuiltSurfaceY(int modelY, int blockX, int blockZ, RiversCompat.Sample? sample)
    {
        int y = modelY;
        int seaLevel = _settings.SeaLevel, mapSizeY = _settings.MapSizeY;
        if (RiversCompat.Installed && sample is { } river)
        {
            y = Math.Clamp(RiversCompat.ValleyHeight(y, river, blockX, blockZ, seaLevel), 1, mapSizeY - 2);
            if (river.Distance <= 0.0) y = Math.Min(y, RiversCompat.ChannelFloorY(river, seaLevel, mapSizeY));
        }
        return Math.Clamp(y, 1, mapSizeY - 2);
    }
}
