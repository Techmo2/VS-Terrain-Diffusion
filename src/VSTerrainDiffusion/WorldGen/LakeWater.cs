using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Fills the world's closed depressions with fresh water, region by region (<see cref="WaterRegion"/>),
/// and tells the terrain pass each chunk's lake water once it is final.
///
/// Heights are the terrain tiles' surface blocks, raised to the sea's flush row where the sea
/// stands; the sea and the edge of each region are outlets. A chunk whose water is not final yet
/// waits for the rest of its region: the tiles already in memory or the tile database are added
/// first, then the region's missing tiles are generated, nearest the chunk first, until it is. The
/// result depends only on a region's heights, never on the order tiles came in, so a region dropped
/// from memory and rebuilt later releases the same water.
/// </summary>
public sealed class LakeWater
{
    private const int ChunkSize = WaterRegion.ChunkSize;

    /// <summary>Regions kept in memory before the least recently used are dropped.</summary>
    private const long BudgetBytes = 256L * 1024 * 1024;

    private readonly TerrainDiffusionProvider _provider;
    private readonly ILogger _logger;
    private readonly int _tileSize;
    private readonly int _regionTiles;
    private readonly int _seaLevel;
    private readonly int _maxY;

    private readonly ConcurrentDictionary<long, Entry> _regions = new();
    private long _clock;

    private long _tilesForced;
    private long _forcedMillis;
    private long _regionsCompleted;
    private long _regionsFailed;
    private long _regionsDropped;

    private sealed class Entry
    {
        public readonly WaterRegion Region;
        public long LastUse;
        public Entry(WaterRegion region) { Region = region; }
    }

    public LakeWater(TerrainDiffusionProvider provider, DiffusionWorldSettings settings, ILogger logger)
    {
        _provider = provider;
        _logger = logger;
        _tileSize = provider.TileSize;
        _regionTiles = Math.Max(1, settings.WaterRegionBlocks / _tileSize);
        _seaLevel = settings.SeaLevel;
        _maxY = settings.MapSizeY - 2;
    }

    /// <summary>Region edge in tiles.</summary>
    public int RegionTiles => _regionTiles;

    /// <summary>Region edge in blocks: the requested size rounded down to whole tiles.</summary>
    public int RegionBlocks => _regionTiles * _tileSize;

    /// <summary>
    /// Lake water of chunk column (chunkX, chunkZ): <see cref="WaterRegion.DryChunk"/>, or the top
    /// water block of each column (index lz * 32 + lx; 0 where dry). With <paramref name="settle"/>
    /// this waits until the water is final, generating terrain if need be; without, it returns what
    /// is known now, for throwaway chunks such as a translocator's peek.
    /// </summary>
    public short[] ChunkLakeTop(int chunkX, int chunkZ, bool settle)
    {
        int blockX = chunkX * ChunkSize, blockZ = chunkZ * ChunkSize;
        int tileX = TerrainDiffusionProvider.FloorDiv(blockX, _tileSize);
        int tileZ = TerrainDiffusionProvider.FloorDiv(blockZ, _tileSize);
        int regionX = TerrainDiffusionProvider.FloorDiv(tileX, _regionTiles);
        int regionZ = TerrainDiffusionProvider.FloorDiv(tileZ, _regionTiles);
        int chunksPerRegion = _regionTiles * _tileSize / ChunkSize;
        int localChunkX = chunkX - regionX * chunksPerRegion;
        int localChunkZ = chunkZ - regionZ * chunksPerRegion;

        WaterRegion region = RegionFor(regionX, regionZ);
        lock (region)
        {
            if (region.TryGetChunk(localChunkX, localChunkZ, out short[] released)) return released;
        }

        // Its own tile, then whatever of the region can be had without the model.
        AddTile(region, regionX, regionZ, tileX, tileZ, _provider.GetTile(tileX, tileZ));
        foreach ((int x, int z) in MissingTiles(region, regionX, regionZ, blockX, blockZ))
        {
            TerrainTile stored = _provider.TryGetStoredTileAt(x * _tileSize, z * _tileSize);
            if (stored != null) AddTile(region, regionX, regionZ, x, z, stored);
        }

        lock (region)
        {
            if (region.TryGetChunk(localChunkX, localChunkZ, out short[] released)) return released;
            if (!settle) return region.EstimateChunk(localChunkX, localChunkZ) ?? WaterRegion.DryChunk;
        }

        // Generate the rest of the region, nearest first, until this chunk is final. All of it at
        // the most, at which point every chunk is.
        var stopwatch = Stopwatch.StartNew();
        int forced = 0;
        try
        {
            foreach ((int x, int z) in MissingTiles(region, regionX, regionZ, blockX, blockZ))
            {
                lock (region)
                {
                    if (region.TryGetChunk(localChunkX, localChunkZ, out short[] released)) return released;
                }
                AddTile(region, regionX, regionZ, x, z, _provider.GetTile(x, z));
                forced++;
            }

            lock (region)
            {
                if (region.TryGetChunk(localChunkX, localChunkZ, out short[] released)) return released;
            }
        }
        finally
        {
            Interlocked.Add(ref _tilesForced, forced);
            Interlocked.Add(ref _forcedMillis, stopwatch.ElapsedMilliseconds);
            if (forced > 0)
            {
                _logger.VerboseDebug("[{0}] Lake water for chunk ({1}, {2}) needed {3} more tile(s) of its region ({4} ms).",
                    DiffusionPaths.ModId, chunkX, chunkZ, forced, stopwatch.ElapsedMilliseconds);
            }
        }

        throw new InvalidOperationException(
            $"Lake water for chunk ({chunkX}, {chunkZ}) is not final with its whole region generated.");
    }

    /// <summary>Whether the column at a block position is under lake water. Waits for it to be final.</summary>
    public bool IsLake(int blockX, int blockZ)
    {
        int chunkX = TerrainDiffusionProvider.FloorDiv(blockX, ChunkSize);
        int chunkZ = TerrainDiffusionProvider.FloorDiv(blockZ, ChunkSize);
        short[] top = ChunkLakeTop(chunkX, chunkZ, settle: true);
        return top.Length > 0 && top[(blockZ - chunkZ * ChunkSize) * ChunkSize + blockX - chunkX * ChunkSize] > 0;
    }

    public string Describe()
    {
        int regions = 0, complete = 0;
        long bytes = 0;
        foreach (Entry entry in _regions.Values)
        {
            regions++;
            if (entry.Region.Complete) complete++;
            bytes += entry.Region.ApproxBytes;
        }

        long forced = Interlocked.Read(ref _tilesForced);
        string text = $"regions of {RegionBlocks} blocks; {regions} in memory ({complete} complete, " +
                      $"{bytes / 1048576.0:0.#} MB); {forced} tile(s) generated early for lakes";
        if (forced > 0) text += $" ({Interlocked.Read(ref _forcedMillis) / 1000.0:0.#} s waited)";
        long dropped = Interlocked.Read(ref _regionsDropped);
        if (dropped > 0) text += $"; {dropped} region(s) dropped to stay within {BudgetBytes / 1048576} MB";
        long failed = Interlocked.Read(ref _regionsFailed);
        if (failed > 0) text += $"; {failed} region(s) FAILED their check";
        return text;
    }

    /// <summary>Tiles of the region not added yet, nearest the given block first.</summary>
    private List<(int X, int Z)> MissingTiles(WaterRegion region, int regionX, int regionZ, int blockX, int blockZ)
    {
        var missing = new List<(int X, int Z)>();
        lock (region)
        {
            for (int z = 0; z < _regionTiles; z++)
            for (int x = 0; x < _regionTiles; x++)
            {
                if (!region.HasTile(x, z)) missing.Add((regionX * _regionTiles + x, regionZ * _regionTiles + z));
            }
        }

        long Distance((int X, int Z) tile)
        {
            long dx = tile.X * (long)_tileSize + _tileSize / 2 - blockX - ChunkSize / 2;
            long dz = tile.Z * (long)_tileSize + _tileSize / 2 - blockZ - ChunkSize / 2;
            return dx * dx + dz * dz;
        }

        missing.Sort((a, b) =>
        {
            int byDistance = Distance(a).CompareTo(Distance(b));
            return byDistance != 0 ? byDistance : a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X);
        });
        return missing;
    }

    private void AddTile(WaterRegion region, int regionX, int regionZ, int tileX, int tileZ, TerrainTile tile)
    {
        int localX = tileX - regionX * _regionTiles, localZ = tileZ - regionZ * _regionTiles;
        lock (region)
        {
            if (region.HasTile(localX, localZ)) return;
        }

        // Outside the lock: a tile's heights take a moment to copy and need nothing of the region.
        int n = _tileSize * _tileSize;
        var heights = new short[n];
        int floor = _seaLevel - 1;
        for (int i = 0; i < n; i++)
        {
            int y = Math.Clamp(tile.SurfaceY[i], 1, _maxY);
            heights[i] = (short)Math.Max(y, floor);
        }

        lock (region)
        {
            if (!region.AddTile(localX, localZ, heights) || !region.Complete) return;
        }

        Interlocked.Increment(ref _regionsCompleted);
        if (region.Verified == true)
        {
            _logger.VerboseDebug("[{0}] Lake region ({1}, {2}) complete; its water matches a fill of the whole region.",
                DiffusionPaths.ModId, regionX, regionZ);
        }
        else
        {
            Interlocked.Increment(ref _regionsFailed);
            _logger.Error("[{0}] Lake region ({1}, {2}) is complete but {3} column(s) of its water differ from a fill of " +
                          "the whole region. Please report this.", DiffusionPaths.ModId, regionX, regionZ,
                region.VerifyMismatches);
        }
    }

    private WaterRegion RegionFor(int regionX, int regionZ)
    {
        long key = ((long)regionX << 32) ^ (uint)regionZ;
        bool created = false;
        Entry entry = _regions.GetOrAdd(key, _ =>
        {
            created = true;
            return new Entry(new WaterRegion(_tileSize, _regionTiles, _seaLevel));
        });
        entry.LastUse = Interlocked.Increment(ref _clock);
        if (created) Trim(key);
        return entry.Region;
    }

    /// <summary>
    /// Drops the least recently used regions while over budget. A dropped region is rebuilt, with
    /// the same water, if one of its chunks is asked for again.
    /// </summary>
    private void Trim(long keep)
    {
        long bytes = 0;
        foreach (Entry entry in _regions.Values) bytes += entry.Region.ApproxBytes;
        if (bytes <= BudgetBytes) return;

        var byAge = new List<KeyValuePair<long, Entry>>(_regions);
        byAge.Sort((a, b) => a.Value.LastUse.CompareTo(b.Value.LastUse));
        foreach (KeyValuePair<long, Entry> old in byAge)
        {
            if (bytes <= BudgetBytes * 9 / 10) break;
            if (old.Key == keep) continue;
            if (!_regions.TryRemove(old.Key, out Entry removed)) continue;
            bytes -= removed.Region.ApproxBytes;
            Interlocked.Increment(ref _regionsDropped);
        }
    }
}
