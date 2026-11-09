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

/// <summary>The tile database side of the provider: reading, writing and pregenerating stored tiles.</summary>
public sealed partial class TerrainDiffusionProvider
{
    /// <summary>This world's tiles on disk, when it keeps them; see <see cref="AttachStore"/>.</summary>
    private TerrainTileStore _store;

    /// <summary>
    /// Tiles looked for in the tile database and not found, so a caller that only wants stored
    /// terrain - a sampler query per column, a translocator candidate - asks the file once per tile.
    /// A tile leaves the set when it is generated.
    /// </summary>
    private readonly ConcurrentDictionary<long, byte> _absent = new();

    /// <summary>Tiles generated so far, so a lookup that raced one does not record it as absent.</summary>
    private long _generatedForStore;

    /// <summary>Set on the thread running <see cref="Pregenerate"/>.</summary>
    [ThreadStatic] private static bool _pregenerating;

    /// <summary>
    /// Keeps this world's tiles in <paramref name="store"/> from now on: a tile missing from memory
    /// is read from it before the model is asked, and every tile generated is written to it. Only
    /// once the height mapping is settled, since a tile stores block heights. Tiles already in
    /// memory are written too.
    /// </summary>
    public void AttachStore(TerrainTileStore store)
    {
        _store = store;
        _pipeline.CoarseWindowStore = store.CoarseWindows(MainCoarseVariant);
        lock (_seaGate)
        {
            if (_riverFreePipeline != null) _riverFreePipeline.CoarseWindowStore = store.CoarseWindows(RiverFreeCoarseVariant);
        }

        foreach (KeyValuePair<long, Lazy<TerrainTile>> entry in _tiles)
        {
            if (!entry.Value.IsValueCreated) continue;
            TerrainTile tile = entry.Value.Value;
            store.Save(FloorDiv(tile.BlockX, _tileSize), FloorDiv(tile.BlockZ, _tileSize), tile);
        }
    }

    /// <summary>Keys the two coarse pipelines' windows apart in the tile database.</summary>
    private const int MainCoarseVariant = 0, RiverFreeCoarseVariant = 1;

    /// <summary>Coarse windows the coarse model was run for this session, in either coarse pipeline.</summary>
    public long CoarseWindowsComputed
    {
        get
        {
            WorldPipeline riverFree = _riverFreePipeline;
            return _pipeline.CoarseWindowsComputed + (riverFree?.CoarseWindowsComputed ?? 0);
        }
    }

    /// <summary>
    /// Makes sure the coarse windows over a position are stored: the main pipeline's, and with
    /// Rivers the river-free one behind its coarse sea test. Behind every player's terrain in the
    /// queue. Returns true if the coarse model ran.
    /// </summary>
    public bool PregenerateCoarse(int blockX, int blockZ)
    {
        long before = CoarseWindowsComputed;
        _pregenerating = true;
        try
        {
            _scheduler.Run(UrgencyOf(0, 0), () => CoarseElevationRoot(_pipeline, blockX, blockZ));
            if (RiversCompat.Installed) IsCoarseSea(blockX, blockZ);
        }
        finally
        {
            _pregenerating = false;
        }
        return CoarseWindowsComputed != before;
    }

    /// <summary>
    /// Empties the tile database and drops the tiles in memory, for when something they were built
    /// from has changed without the database noticing. Returns how many stored tiles went.
    /// </summary>
    public long ClearStoredTiles()
    {
        _tiles.Clear();
        _lastAccess.Clear();
        _peekTiles.Clear();
        return _store?.Clear(coarse: true) ?? 0;
    }

    /// <summary>The tile database this world keeps, or null.</summary>
    public TerrainTileStore Store => _store;

    /// <summary>Tiles read back from the tile database this session.</summary>
    public long TilesLoaded => _store?.TilesLoaded ?? 0;

    /// <summary>
    /// Whether a tile can be had without running the model: it is in memory, or in the tile
    /// database (written or queued). Never generates anything.
    /// </summary>
    public bool HasTile(int tileX, int tileZ)
    {
        long key = TileKey(tileX, tileZ);
        if (_tiles.TryGetValue(key, out Lazy<TerrainTile> lazy) && lazy.IsValueCreated) return true;

        TerrainTileStore store = _store;
        if (store == null || _absent.ContainsKey(key)) return false;
        long generated = Interlocked.Read(ref _generatedForStore);
        if (store.Contains(tileX, tileZ)) return true;
        MarkAbsent(key, generated);
        return false;
    }

    /// <summary>
    /// Whether every tile overlapping a block rectangle (maximum exclusive) can be had without
    /// running the model.
    /// </summary>
    public bool HasTilesCovering(int minX, int minZ, int maxX, int maxZ)
    {
        for (int tileZ = FloorDiv(minZ, _tileSize); tileZ <= FloorDiv(maxZ - 1, _tileSize); tileZ++)
        for (int tileX = FloorDiv(minX, _tileSize); tileX <= FloorDiv(maxX - 1, _tileSize); tileX++)
        {
            if (!HasTile(tileX, tileZ)) return false;
        }
        return true;
    }

    /// <summary>
    /// The tile covering a position if it can be had without running the model - see
    /// <see cref="HasTile"/> - or null. A tile read from the database is cached like a peeked one,
    /// so a far-reaching caller does not push a player's terrain out of memory.
    /// </summary>
    public TerrainTile TryGetStoredTileAt(int blockX, int blockZ)
    {
        int tileX = FloorDiv(blockX, _tileSize);
        int tileZ = FloorDiv(blockZ, _tileSize);
        long key = TileKey(tileX, tileZ);

        if (_tiles.TryGetValue(key, out Lazy<TerrainTile> lazy) && lazy.IsValueCreated)
        {
            _lastAccess[key] = Interlocked.Increment(ref _accessClock);
            return lazy.Value;
        }

        TerrainTileStore store = _store;
        if (store == null || _absent.ContainsKey(key)) return null;

        long generated = Interlocked.Read(ref _generatedForStore);
        TerrainTile stored = store.TryLoad(tileX, tileZ);
        if (stored == null)
        {
            MarkAbsent(key, generated);
            return null;
        }

        var loaded = new Lazy<TerrainTile>(() => stored, LazyThreadSafetyMode.ExecutionAndPublication);
        _ = loaded.Value;
        if (ReferenceEquals(_tiles.GetOrAdd(key, loaded), loaded))
        {
            _peekTiles[key] = 0;
            TileGenerated?.Invoke(stored);
        }

        // Otherwise someone else's copy may still be on its way; the stored one is the same tile.
        _lastAccess[key] = Interlocked.Increment(ref _accessClock);
        EvictLeastRecentlyUsed(key);
        return stored;
    }

    /// <summary>
    /// Makes sure a tile is in the tile database, generating it if it is not. Behind every player's
    /// terrain in the queue, and cached so that it is evicted first. Returns true if the model ran.
    /// </summary>
    public bool Pregenerate(int tileX, int tileZ)
    {
        if (HasTile(tileX, tileZ)) return false;

        _pregenerating = true;
        try
        {
            GetTile(tileX, tileZ);
        }
        finally
        {
            _pregenerating = false;
        }
        return true;
    }

    /// <summary>
    /// Records a tile as not stored, unless a tile was generated since <paramref name="generatedBefore"/>
    /// was read: that one may be this one, saved after the lookup missed it. Generation saves, counts,
    /// then clears the mark, so either the count has moved or the mark is cleared after this.
    /// </summary>
    private void MarkAbsent(long key, long generatedBefore)
    {
        _absent[key] = 0;
        if (Interlocked.Read(ref _generatedForStore) != generatedBefore) _absent.TryRemove(key, out _);
    }

    /// <summary>
    /// A tile from the tile database if it is stored, else generated and written to it. Runs once per tile
    /// inside its cache entry's <see cref="Lazy{T}"/>.
    /// </summary>
    private TerrainTile LoadOrGenerateTile(int tileX, int tileZ)
    {
        TerrainTileStore store = _store;
        TerrainTile stored = store?.TryLoad(tileX, tileZ);
        if (stored != null)
        {
            TileGenerated?.Invoke(stored);
            return stored;
        }

        TerrainTile tile = GenerateTile(tileX, tileZ);
        store?.Save(tileX, tileZ, tile);
        Interlocked.Increment(ref _generatedForStore);
        _absent.TryRemove(TileKey(tileX, tileZ), out _);
        return tile;
    }
}
