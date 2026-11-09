using System;
using System.Collections.Generic;
using System.Threading;
using TerrainDiffusion.Water;
using VSTerrainDiffusion.Core;
using static VSTerrainDiffusion.Core.IntMath;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Feeding the graph in the background: terrain tiles queued as they are generated and added in
/// batches on the water thread.
/// </summary>
public sealed partial class WorldWater
{
    /// <summary>On the generating thread: only queue it.</summary>
    private void OnTileGenerated(TerrainTile tile)
    {
        // A peek's terrain reaches thousands of blocks out, where no chunk needs water yet.
        if (_stopping || ChunkPeekScope.Active) return;
        try
        {
            _arrivals.TryAdd(tile);
        }
        catch (InvalidOperationException)
        {
            // Completed while stopping.
        }
    }

    /// <summary>
    /// The water thread: takes queued terrain tiles in batches of up to 64 and feeds them to the graph until
    /// stopped. A batch that fails is logged and counted, and the thread carries on.
    /// </summary>
    private void Run()
    {
        try
        {
            foreach (TerrainTile first in _arrivals.GetConsumingEnumerable())
            {
                var batch = new List<TerrainTile> { first };
                while (batch.Count < 64 && _arrivals.TryTake(out TerrainTile more)) batch.Add(more);
                Interlocked.Add(ref _inFlight, batch.Count);
                try
                {
                    Feed(batch);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _backgroundFailures);
                    _logger.Error("[{0}] Adding terrain to the water failed: {1}", DiffusionPaths.ModId, e);
                }
                finally
                {
                    Interlocked.Add(ref _inFlight, -batch.Count);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Adds the water tiles a batch of terrain tiles completes, from those tiles and the ones already stored.</summary>
    private void Feed(List<TerrainTile> arrived)
    {
        var parts = new Dictionary<(int, int), TerrainTile>();
        var tiles = new SortedSet<TileCoord>();
        foreach (TerrainTile terrain in arrived)
        {
            if (terrain.Size != _terrainTile) continue;
            parts[(FloorDiv(terrain.BlockX, _terrainTile), FloorDiv(terrain.BlockZ, _terrainTile))] = terrain;
            for (int z = FloorDiv(terrain.BlockZ, TileBlocks); z <= FloorDiv(terrain.BlockZ + terrain.Size - 1, TileBlocks); z++)
            for (int x = FloorDiv(terrain.BlockX, TileBlocks); x <= FloorDiv(terrain.BlockX + terrain.Size - 1, TileBlocks); x++)
                tiles.Add(new TileCoord(z, x));
        }

        lock (_graphLock)
        {
            tiles.RemoveWhere(_graph.Contains);
        }

        var built = new List<(TileCoord, float[], float[])>();
        foreach (TileCoord tile in tiles)
        {
            if (_stopping) return;
            if (TryBuild(tile, (x, z) => parts.TryGetValue((x, z), out TerrainTile t)
                    ? t
                    : _provider.TryGetStoredTileAt(x * _terrainTile, z * _terrainTile),
                out float[] heights, out float[] runoff))
            {
                built.Add((tile, heights, runoff));
            }
            if (built.Count == BackgroundBatch)
            {
                Add(built);
                built.Clear();
            }
        }
        Add(built);
    }
}
