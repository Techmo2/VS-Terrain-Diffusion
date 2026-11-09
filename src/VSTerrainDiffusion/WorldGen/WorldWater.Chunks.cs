using System;
using System.Collections.Generic;
using System.Threading;
using TerrainDiffusion.Water;
using PlayerDistance = System.Func<int, int, long>;
using VSTerrainDiffusion.Core;
using static VSTerrainDiffusion.Core.IntMath;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Released chunks: copied out of the graph into ChunkWater, kept for the terrain pass, and dropped
/// when far from every player.
/// </summary>
public sealed partial class WorldWater
{
    /// <summary>
    /// The chunk and, with rivers, its 8 neighbours, if all are released. Copies released chunks out
    /// of the graph when <paramref name="fromGraph"/> (under the lock).
    /// </summary>
    private bool TryAssemble(int chunkX, int chunkZ, int halo, bool fromGraph, out SettledWater water)
    {
        water = null;
        ChunkWater centre = null;
        List<RiverCell> rivers = null;
        for (int dz = -halo; dz <= halo; dz++)
        for (int dx = -halo; dx <= halo; dx++)
        {
            ChunkWater chunk = Stored(chunkX + dx, chunkZ + dz, fromGraph);
            if (chunk == null) return false;
            if (dx == 0 && dz == 0) centre = chunk;
            if (chunk.Rivers.Length > 0) (rivers ??= new List<RiverCell>()).AddRange(chunk.Rivers);
        }

        water = new SettledWater(centre.LakeTop, centre.Wetland, rivers?.ToArray() ?? Array.Empty<RiverCell>());
        return true;
    }

    /// <summary>
    /// A released chunk's water from the store, or, when <paramref name="fromGraph"/>, copied out of the
    /// graph again if its tile is still held (under the lock).
    /// </summary>
    /// <param name="chunkX">Chunk column.</param>
    /// <param name="chunkZ">Chunk row.</param>
    /// <param name="fromGraph">Whether the lock is held and the graph may be read.</param>
    /// <returns>The water, or null if the chunk is not released.</returns>
    private ChunkWater Stored(int chunkX, int chunkZ, bool fromGraph)
    {
        if (_chunks.TryGetValue(ChunkKey(chunkX, chunkZ), out ChunkWater water)) return water;
        if (!fromGraph) return null;

        // Dropped from the store while its tile is still held: released chunks never change, so it
        // can be copied out again.
        var tile = new TileCoord(FloorDiv(chunkZ, ChunksPerTile), FloorDiv(chunkX, ChunksPerTile));
        if (!_graph.TryGetTile(tile, out TileWater held)) return null;
        int local = (chunkZ - tile.Row * ChunksPerTile) * ChunksPerTile + chunkX - tile.Column * ChunksPerTile;
        if (held.ChunkReleaseSteps[local] < 0) return null;

        water = Extract(chunkX, chunkZ);
        Store(chunkX, chunkZ, water);
        return water;
    }

    /// <summary>
    /// Keeps a released chunk's water. A chunk released a second time (its tile forgotten and added back)
    /// must come out the same; a difference is counted and reported.
    /// </summary>
    /// <param name="chunkX">Chunk column.</param>
    /// <param name="chunkZ">Chunk row.</param>
    /// <param name="water">The water.</param>
    private void Store(int chunkX, int chunkZ, ChunkWater water)
    {
        long key = ChunkKey(chunkX, chunkZ);
        if (_chunks.TryAdd(key, water))
        {
            Interlocked.Increment(ref _storedChunks);
            return;
        }

        // A forgotten tile added back releases its chunks again, with the same water.
        Interlocked.Increment(ref _releasedTwice);
        if (_chunks.TryGetValue(key, out ChunkWater before) && !before.SameAs(water))
        {
            Interlocked.Increment(ref _mismatches);
            _logger.Warning("[{0}] Chunk ({1}, {2}) was released again with different water. Please report this.",
                DiffusionPaths.ModId, chunkX, chunkZ);
        }
    }

    /// <summary>Copies a released chunk's lakes and rivers out of the graph (under the lock).</summary>
    private ChunkWater Extract(int chunkX, int chunkZ)
    {
        var tile = new TileCoord(FloorDiv(chunkZ, ChunksPerTile), FloorDiv(chunkX, ChunksPerTile));
        _graph.TryGetTile(tile, out TileWater water);
        float[] runoff = _runoff[tile];
        int localX = chunkX - tile.Column * ChunksPerTile, localZ = chunkZ - tile.Row * ChunksPerTile;

        ReadOnlySpan<float> level = water.Level, elevation = water.Elevation, discharge = water.Discharge;
        short[] lakeTop = null;
        byte[] wetland = null;
        List<RiverCell> rivers = null;

        for (int lz = 0; lz < ChunkBlocks; lz++)
        for (int lx = 0; lx < ChunkBlocks; lx++)
        {
            int row = localZ * ChunkBlocks + lz, column = localX * ChunkBlocks + lx;
            int cell = row * TileBlocks + column;
            int index = lz * ChunkBlocks + lx;
            float e = elevation[cell];
            int ground = _settings.ElevationToBlockY(e);
            int top = ground;

            if (level[cell] > e)
            {
                (top, bool lake, byte wetness) = LakeColumn(level[cell], e, ground);
                if (lake)
                {
                    (lakeTop ??= new short[ChunkBlocks * ChunkBlocks])[index] = (short)top;
                    continue;
                }
                if (wetness > 0) (wetland ??= new byte[ChunkBlocks * ChunkBlocks])[index] = wetness;
            }

            // Ground at or above the water, wetland included, may carry a river; its surface is no higher than the lake's.
            if (!HasRivers || !(e > 0f) || !(discharge[cell] >= _riverThreshold)) continue;

            (rivers ??= new List<RiverCell>()).Add(RiverGeometry.Cell(water, runoff, cell, tile.Column * TileBlocks,
                tile.Row * TileBlocks, Math.Min(top, ground), _settings, Settings.DischargeCap, _riverWidth));
        }

        if (lakeTop == null && wetland == null && rivers == null) return ChunkWater.Dry;
        return new ChunkWater(lakeTop, wetland, rivers?.ToArray() ?? Array.Empty<RiverCell>());
    }

    /// <summary>
    /// A column under a filled level: the lake stands <see cref="_wetlandDepth"/> blocks below its
    /// spill block, so columns that deep or shallower are wetland, saturated ground at the lake's
    /// waterline. Returns the water surface block, whether the column holds lake water, and its
    /// wetness (1 to 255, how much of the dropped water stood over it; 0 if neither).
    /// </summary>
    private (int Top, bool Lake, byte Wetness) LakeColumn(float level, float elevation, int ground)
    {
        int spill = _settings.ElevationToBlockY(level);
        if (spill <= ground || spill < _seaLevel) return (ground, false, 0);

        int top = spill - _wetlandDepth;
        if (top > ground && top >= _seaLevel) return (top, true, 0);

        double vertical = _settings.MetersPerBlockVertical;
        double share = vertical > 0.0 ? (level - elevation) / (vertical * Math.Max(1, _wetlandDepth)) : 1.0;
        return (Math.Max(top, _seaLevel - 1), false, (byte)Math.Clamp((int)Math.Round(share * 255.0), 1, 255));
    }

    /// <summary>Lake tops of a chunk from a filled tile's current levels (for peeks; may not be final).</summary>
    private short[] LakeTops(in TileWater water, int localX, int localZ)
    {
        ReadOnlySpan<float> level = water.Level, elevation = water.Elevation;
        short[] lakeTop = null;
        for (int lz = 0; lz < ChunkBlocks; lz++)
        for (int lx = 0; lx < ChunkBlocks; lx++)
        {
            int cell = (localZ * ChunkBlocks + lz) * TileBlocks + localX * ChunkBlocks + lx;
            if (!(level[cell] > elevation[cell])) continue;
            (int top, bool lake, _) = LakeColumn(level[cell], elevation[cell], _settings.ElevationToBlockY(elevation[cell]));
            if (lake) (lakeTop ??= new short[ChunkBlocks * ChunkBlocks])[lz * ChunkBlocks + lx] = (short)top;
        }
        return lakeTop;
    }

    /// <summary>Drops stored chunks far from every player once there are too many.</summary>
    private void DropFarChunks()
    {
        if (Volatile.Read(ref _storedChunks) <= MaxStoredChunks) return;
        foreach (KeyValuePair<long, ChunkWater> entry in _chunks)
        {
            int chunkX = (int)(entry.Key >> 32), chunkZ = (int)entry.Key;
            long distance = _distanceToPlayers(chunkX * ChunkBlocks + ChunkBlocks / 2, chunkZ * ChunkBlocks + ChunkBlocks / 2);
            if (distance <= KeepChunksNearPlayersBlocks) continue;
            // A held tile's chunks stay: they would need its neighbours to be copied out again.
            if (_graph.Contains(new TileCoord(FloorDiv(chunkZ, ChunksPerTile), FloorDiv(chunkX, ChunksPerTile)))) continue;
            if (_chunks.TryRemove(entry.Key, out _))
                Interlocked.Decrement(ref _storedChunks);
        }
    }
}
