using System;
using System.Collections.Generic;
using System.Threading;
using TerrainDiffusion.Water;
using PlayerDistance = System.Func<int, int, long>;
using static VSTerrainDiffusion.Core.IntMath;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// The river graph itself: building water tiles from terrain tiles, adding them, and forgetting far
/// ones to stay within the memory budget.
/// </summary>
public sealed partial class WorldWater
{
    /// <summary>
    /// The heights and runoff of a water tile from the terrain tiles covering it, or false if
    /// <paramref name="terrain"/> has one of them missing (returns null).
    /// </summary>
    private bool TryBuild(TileCoord tile, System.Func<int, int, TerrainTile> terrain, out float[] heights, out float[] runoff)
    {
        heights = null;
        runoff = null;
        int x0 = tile.Column * TileBlocks, z0 = tile.Row * TileBlocks;
        var h = new float[Cells];
        var r = new float[Cells];

        for (int tz = FloorDiv(z0, _terrainTile); tz <= FloorDiv(z0 + TileBlocks - 1, _terrainTile); tz++)
        for (int tx = FloorDiv(x0, _terrainTile); tx <= FloorDiv(x0 + TileBlocks - 1, _terrainTile); tx++)
        {
            TerrainTile part = terrain(tx, tz);
            if (part == null) return false;

            int fromX = Math.Max(x0, part.BlockX), toX = Math.Min(x0 + TileBlocks, part.BlockX + part.Size);
            int fromZ = Math.Max(z0, part.BlockZ), toZ = Math.Min(z0 + TileBlocks, part.BlockZ + part.Size);
            for (int z = fromZ; z < toZ; z++)
            {
                int target = (z - z0) * TileBlocks + fromX - x0;
                int source = part.Index(fromX - part.BlockX, z - part.BlockZ);
                for (int x = fromX; x < toX; x++, target++, source++)
                {
                    h[target] = part.ElevationMeters[source];
                    r[target] = RiverGeometry.Runoff(part.PrecipitationMm[source], part.TemperatureC[source]);
                }
            }
        }

        heights = h;
        runoff = r;
        return true;
    }

    /// <summary>Whether every terrain tile under a water tile can be had without the model.</summary>
    private bool PartsAvailable(TileCoord tile)
    {
        int x0 = tile.Column * TileBlocks, z0 = tile.Row * TileBlocks;
        return _provider.HasTilesCovering(x0, z0, x0 + TileBlocks, z0 + TileBlocks);
    }

    /// <summary>Adds the tiles not held yet and copies out every chunk they release. Returns how many went in.</summary>
    private int Add(List<(TileCoord Tile, float[] Heights, float[] Runoff)> built)
    {
        if (built.Count == 0) return 0;
        lock (_graphLock)
        {
            var coords = new List<TileCoord>(built.Count);
            var fresh = new List<(TileCoord Tile, float[] Heights, float[] Runoff)>(built.Count);
            var seen = new HashSet<TileCoord>();
            foreach (var entry in built)
            {
                if (_graph.Contains(entry.Tile) || !seen.Add(entry.Tile)) continue;
                coords.Add(entry.Tile);
                fresh.Add(entry);
            }
            if (fresh.Count == 0) return 0;

            var heights = new float[fresh.Count * Cells];
            for (int i = 0; i < fresh.Count; i++)
            {
                Array.Copy(fresh[i].Heights, 0, heights, i * Cells, Cells);
                _runoff[fresh[i].Tile] = fresh[i].Runoff;
            }

            TileUpdate update = _graph.AddTiles(coords.ToArray(), heights);
            foreach (ReleasedChunk released in update.ReleasedChunks)
            {
                Store(released.Chunk.Column, released.Chunk.Row, Extract(released.Chunk.Column, released.Chunk.Row));
            }

            ForgetFarTiles();
            DropFarChunks();
            return fresh.Count;
        }
    }

    /// <summary>
    /// Over budget, forgets the farthest tiles that no player is near, that weren't just added, and
    /// whose water and their 8 neighbours' is all released (under the lock).
    /// </summary>
    private void ForgetFarTiles()
    {
        RiverGraphStatistics stats = _graph.GetStatistics();
        long perTile = Cells * sizeof(float);
        long bytes = stats.Bytes + _runoff.Count * perTile;
        if (bytes <= BudgetBytes || _runoff.Count == 0) return;

        long average = bytes / _runoff.Count;
        int recent = _graph.Step - KeepRecentArrivals;
        var candidates = new List<(TileCoord Tile, long Distance)>();
        foreach (TileCoord tile in _runoff.Keys)
        {
            long distance = _distanceToPlayers(tile.Column * TileBlocks + TileBlocks / 2, tile.Row * TileBlocks + TileBlocks / 2);
            if (distance <= (long)KeepTilesNearPlayers * TileBlocks) continue;
            if (!_graph.TryGetTile(tile, out TileWater water) || water.ArrivalStep >= recent) continue;

            bool released = true;
            for (int dz = -1; dz <= 1 && released; dz++)
            for (int dx = -1; dx <= 1 && released; dx++)
                released = _graph.TryGetTile(tile.Offset(dz, dx), out TileWater around) && around.IsFullyReleased;
            if (released) candidates.Add((tile, distance));
        }

        candidates.Sort((a, b) => b.Distance != a.Distance ? b.Distance.CompareTo(a.Distance) : a.Tile.CompareTo(b.Tile));
        foreach ((TileCoord tile, _) in candidates)
        {
            if (bytes <= BudgetBytes * 9 / 10) break;
            if (!_graph.ForgetTile(tile)) continue;
            _runoff.Remove(tile);
            bytes -= average;
            Interlocked.Increment(ref _tilesForgotten);
        }
    }
}
