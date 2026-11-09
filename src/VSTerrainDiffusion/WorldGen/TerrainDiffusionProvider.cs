using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;
using VSTerrainDiffusion.Pipeline;
using static VSTerrainDiffusion.Core.IntMath;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Turns the diffusion pipeline into block-space terrain tiles, with an LRU cache and a single
/// inference thread so that the pipeline's tile store is never touched concurrently.
/// </summary>
/// <remarks>
/// Split by concern across partial files: this one holds construction, the tile cache and
/// scheduling; <c>.Storage</c> the tile database; <c>.Generation</c> building a tile; <c>.Coarse</c>
/// and <c>.Sampling</c> queries that do not need a tile; <c>.Calibration</c> and <c>.SpawnSearch</c>
/// the start-of-world surveys.
/// </remarks>
public sealed partial class TerrainDiffusionProvider : IDisposable
{
    /// <summary>
    /// The diffusion pipeline every tile is sampled from. Only touched under <see cref="_scheduler"/>.
    /// </summary>
    private readonly WorldPipeline _pipeline;

    /// <summary>The world's settings: scale, origin, height mapping and climate corrections.</summary>
    private readonly DiffusionWorldSettings _settings;

    /// <summary>The server log.</summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Edge of a tile in blocks: the tile database's, else 128 on CPU and 256 on GPU, unless configured.
    /// </summary>
    private readonly int _tileSize;

    /// <summary>Tiles kept in memory, from <c>terrainTileCacheMegabytes</c>; at least 16.</summary>
    private readonly int _maxCachedTiles;

    /// <summary>
    /// The tile cache, by <see cref="TileKey"/>. Lazy, so concurrent requests for one tile generate it once.
    /// </summary>
    private readonly ConcurrentDictionary<long, Lazy<TerrainTile>> _tiles = new();


    /// <summary>Monotonic access stamp per cached tile, used to pick eviction victims.</summary>
    private readonly ConcurrentDictionary<long, long> _lastAccess = new();

    /// <summary>Serialises eviction, so two callers never drop the same tile twice over.</summary>
    private readonly object _evictionLock = new();

    /// <summary>The clock <see cref="_lastAccess"/> stamps are taken from.</summary>
    private long _accessClock;

    /// <summary>
    /// Tiles first generated to serve a chunk peek. Evicted before anything a player's own chunk
    /// loading brought in, but only down to <see cref="PeekReserve"/>, because a peek that evicts
    /// its own working set regenerates it several times over.
    /// </summary>
    private readonly ConcurrentDictionary<long, byte> _peekTiles = new();

    /// <summary>
    /// Peeked tiles kept regardless of age. One peek needs about three at the default tile size,
    /// since its map layers are held to the ground it actually reads.
    ///
    /// Deliberately small. The reserve is what stops a peek evicting its own working set between
    /// one map layer's walk and the next, but every slot it holds is one a player's terrain cannot
    /// use, and too large a reserve stops the policy engaging at all once the cache is nearly full
    /// - measured at 24 with a 142 tile cache, the player lost every tile they had.
    /// </summary>
    private const int PeekReserve = 4;

    /// <summary>
    /// Raised for each tile the moment it is generated or read back from the tile database, before
    /// it can be evicted. Nothing in world generation subscribes; this is for the debug map, and a
    /// handler that throws is that handler's problem to catch.
    /// </summary>
    public event Action<TerrainTile> TileGenerated;

    /// <summary>Serialises all pipeline work; the tile store is not thread safe.</summary>
    private readonly InferenceScheduler _scheduler = new();

    /// <summary>
    /// How far a tile is from the nearest player, in blocks, which is what decides the order tiles
    /// are generated in. Supplied by the mod system, which is the part that can see the players.
    /// Until it is set everything ranks alike and the order is arrival order, as it used to be.
    /// </summary>
    public System.Func<int, int, long> TileUrgency { get; set; }

    /// <summary>How many tiles have been dropped part way through for something closer.</summary>
    public long PreemptionCount => _scheduler.PreemptionCount;

    /// <summary>
    /// How urgent a tile is for the scheduler: its centre's distance to the nearest player, or the lowest
    /// priority for a peek or pregeneration. Lower runs first.
    /// </summary>
    private long UrgencyOf(int tileX, int tileZ)
    {
        // Terrain generated to serve a chunk peek is speculative and thrown away, and pregeneration
        // is for later. Both always yield.
        if (ChunkPeekScope.Active || _pregenerating) return long.MaxValue / 4;

        System.Func<int, int, long> urgency = TileUrgency;
        if (urgency == null) return 0;

        try
        {
            return urgency(tileX * _tileSize + _tileSize / 2, tileZ * _tileSize + _tileSize / 2);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Tiles generated by the model this session, for statistics.</summary>
    private long _tilesGenerated;

    /// <summary>Wall time spent generating those tiles, in milliseconds.</summary>
    private long _totalInferenceMillis;

    /// <summary>Story structure conditioning, or null; kept for the river-free pipeline.</summary>
    private readonly IReadOnlyList<CoarseConditioningSite> _conditioningSites;

    /// <summary>Builds the pipeline for a world and sizes the tile cache.</summary>
    /// <param name="seed">The world seed.</param>
    /// <param name="models">The loaded models.</param>
    /// <param name="settings">The world's settings.</param>
    /// <param name="logger">The server log.</param>
    /// <param name="landmask">The ocean map to condition on, or null to let the model draw the coast.</param>
    /// <param name="riverBasins">Rivers' basins to shape valleys for, or null.</param>
    /// <param name="riverBasinDepth">How deep those valleys are, 0 to 1.</param>
    /// <param name="tileSize">
    /// The tile size the world's tile database holds, or null to pick one for this machine.
    /// </param>
    /// <param name="conditioningSites">Story structure conditioning, or null.</param>
    public TerrainDiffusionProvider(ulong seed, PipelineModels models, DiffusionWorldSettings settings,
                                    ILogger logger, ILandmaskSource landmask = null,
                                    IRiverBasinSource riverBasins = null, float riverBasinDepth = 0f,
                                    int? tileSize = null, IReadOnlyList<CoarseConditioningSite> conditioningSites = null)
    {
        _conditioningSites = conditioningSites;
        _pipeline = new WorldPipeline(seed, models, landmask, settings.Climate, settings.Latitude,
                                      riverBasins, riverBasinDepth, settings.CoarsePooling,
                                      settings.BaseRenoiseSigma, settings.CoarseHeightNoise,
                                      settings.AltitudeCooling, conditioningSites, settings.CoastFill);
        _seed = seed;
        _models = models;
        _landmask = landmask;
        _settings = settings;
        _logger = logger;
        int configuredTileSize = DiffusionConfig.Instance.TerrainTileSizeBlocks;
        int machineTileSize = configuredTileSize > 0
            ? configuredTileSize
            : OnnxModel.ActiveProvider is InferenceProvider.Cpu or InferenceProvider.OpenVino ? 128 : 256;
        _tileSize = tileSize ?? machineTileSize;
        if (_tileSize != machineTileSize)
        {
            logger.Notification(
                "[{0}] Terrain tiles are {1} blocks, the size this world's tile database holds; this machine " +
                "would otherwise use {2}.", DiffusionPaths.ModId, _tileSize, machineTileSize);
        }

        long budget = (long)DiffusionConfig.Instance.TerrainTileCacheMegabytes * 1024 * 1024;
        _maxCachedTiles = (int)Math.Max(16, budget / TerrainTile.EstimateBytes(_tileSize));
        logger.Notification("[{0}] Terrain tile cache: up to {1} tiles of {2}x{2} blocks ({3} MB budget)",
            DiffusionPaths.ModId, _maxCachedTiles, _tileSize, DiffusionConfig.Instance.TerrainTileCacheMegabytes);
        logger.Notification("[{0}] Pipeline tensor cache: {1} MB total; latent batch size: {2}",
            DiffusionPaths.ModId, DiffusionConfig.Instance.TileCacheMegabytes, _pipeline.LatentBatchSize);
    }

    /// <summary>The world's settings.</summary>
    public DiffusionWorldSettings Settings => _settings;

    /// <summary>Tiles generated by the model this session.</summary>
    public long TilesGenerated => Interlocked.Read(ref _tilesGenerated);

    /// <summary>Mean wall time per generated tile, in milliseconds; 0 before the first.</summary>
    public long AverageTileMillis
    {
        get
        {
            long tiles = Interlocked.Read(ref _tilesGenerated);
            return tiles == 0 ? 0 : Interlocked.Read(ref _totalInferenceMillis) / tiles;
        }
    }

    /// <summary>Edge of a tile in blocks.</summary>
    public int TileSize => _tileSize;

    /// <summary>Windows the latent stage runs per model call.</summary>
    public int LatentBatchSize => _pipeline.LatentBatchSize;

    /// <summary>Bytes held by the pipeline's tensor caches.</summary>
    public long PipelineCachedBytes => _pipeline.CachedBytes;

    /// <summary>Pipeline windows computed this session, over all stages.</summary>
    public long PipelineComputedWindows => _pipeline.TotalComputedWindowCount;

    /// <summary>One line of per-model inference timings.</summary>
    public string ModelTimingSummary => _pipeline.ModelTimingSummary;

    /// <summary>
    /// How much of the time spent generating tiles went to the models rather than to this mod's own
    /// arithmetic. A low number means the bottleneck is here, not on the GPU.
    /// </summary>
    public long InferenceSharePercent
    {
        get
        {
            long total = Interlocked.Read(ref _totalInferenceMillis);
            return total == 0 ? 0 : Math.Min(100, _pipeline.TotalModelInferenceMilliseconds * 100 / total);
        }
    }

    /// <summary>Returns the tile covering the given block position, generating it if needed.</summary>
    public TerrainTile GetTileAt(int blockX, int blockZ)
    {
        int tileX = FloorDiv(blockX, _tileSize);
        int tileZ = FloorDiv(blockZ, _tileSize);
        return GetTile(tileX, tileZ);
    }

    /// <summary>
    /// Returns the tile covering a position, reusing <paramref name="current"/> when it already
    /// covers it. Callers that walk a chunk or a run of map pixels should thread the same variable
    /// through, so a whole chunk costs one cache lookup instead of one per block.
    /// </summary>
    public TerrainTile GetTileAt(int blockX, int blockZ, ref TerrainTile current)
    {
        if (current != null &&
            blockX >= current.BlockX && blockX < current.BlockX + current.Size &&
            blockZ >= current.BlockZ && blockZ < current.BlockZ + current.Size)
        {
            return current;
        }

        current = GetTileAt(blockX, blockZ);
        return current;
    }

    /// <summary>
    /// Drops every generated tile, for when the metre-to-block mapping changes underneath them.
    ///
    /// A tile stores block heights, not metres - <c>SurfaceY</c> is
    /// <see cref="DiffusionWorldSettings.ElevationToBlockY"/> applied at the moment it was built -
    /// so a tile that survives a change to that mapping records a different landscape from its
    /// neighbours, and the join between them is a cliff. The pipeline's own windows are in metres
    /// and are not affected, so this throws away the cheap half of the work and keeps the model
    /// inference that produced it.
    /// </summary>
    public void InvalidateTiles()
    {
        int dropped = _tiles.Count;
        _tiles.Clear();
        _lastAccess.Clear();

        // Coarse windows are in metres, so they stay.
        _store?.Clear(coarse: false);

        if (dropped > 0)
        {
            _logger.Notification(
                "[{0}] Dropped {1} terrain tile(s) generated before the height mapping was settled.",
                DiffusionPaths.ModId, dropped);
        }
    }

    /// <summary>
    /// The tile at a tile coordinate, from the cache, the tile database or the model. Marks it speculative
    /// when a peek or pregeneration asked first, and evicts down to the cache budget.
    /// </summary>
    /// <param name="tileX">Tile X (block X / tile size, rounded down).</param>
    /// <param name="tileZ">Tile Z.</param>
    /// <returns>The tile; never null.</returns>
    public TerrainTile GetTile(int tileX, int tileZ)
    {
        long key = TileKey(tileX, tileZ);
        bool created = false;
        Lazy<TerrainTile> lazy = _tiles.GetOrAdd(key, _ =>
        {
            created = true;
            return new Lazy<TerrainTile>(() => LoadOrGenerateTile(tileX, tileZ), LazyThreadSafetyMode.ExecutionAndPublication);
        });

        // The translocator search peeks four times a second at points up to 8000 blocks away. Left
        // to a plain LRU that walk pushes the ground a player is standing on out of the cache, to
        // be generated again the moment they move, so tiles a peek brought into being are marked
        // and spent first - see EvictLeastRecentlyUsed. The stamp itself stays on the ordinary
        // clock: an earlier version put peeked tiles in a band below it, which made them always
        // the victim and had a peek evicting its own tiles between one map layer's walk and the
        // next, regenerating them three times over. Pregenerated tiles are on disk already, so they
        // go the same way.
        bool speculative = ChunkPeekScope.Active || _pregenerating;
        if (created)
        {
            if (speculative) _peekTiles[key] = 0;
        }
        else if (!speculative)
        {
            _peekTiles.TryRemove(key, out _);
        }

        _lastAccess[key] = Interlocked.Increment(ref _accessClock);
        TerrainTile tile = lazy.Value;
        EvictLeastRecentlyUsed(key);
        return tile;
    }

    /// <summary>
    /// Drops the least recently used tiles until the cache is back within budget. The key that was
    /// just handed out is never a candidate: evicting the tile currently being read would make the
    /// caller regenerate it on its very next block column.
    /// </summary>
    private void EvictLeastRecentlyUsed(long protectedKey)
    {
        if (_tiles.Count <= _maxCachedTiles) return;

        lock (_evictionLock)
        {
            while (_tiles.Count > _maxCachedTiles)
            {
                // Spend the peeked tiles first, down to the reserve one peek needs to finish
                // without tripping over itself. Below that, fall back to a plain global LRU.
                bool peekedOnly = _peekTiles.Count > PeekReserve;

                long oldestKey = 0;
                long oldestAccess = long.MaxValue;
                bool found = false;

                foreach (KeyValuePair<long, long> entry in _lastAccess)
                {
                    if (entry.Key == protectedKey || entry.Value >= oldestAccess) continue;
                    if (peekedOnly && !_peekTiles.ContainsKey(entry.Key)) continue;
                    oldestKey = entry.Key;
                    oldestAccess = entry.Value;
                    found = true;
                }

                if (!found && peekedOnly)
                {
                    foreach (KeyValuePair<long, long> entry in _lastAccess)
                    {
                        if (entry.Key == protectedKey || entry.Value >= oldestAccess) continue;
                        oldestKey = entry.Key;
                        oldestAccess = entry.Value;
                        found = true;
                    }
                }

                if (!found) return;
                _tiles.TryRemove(oldestKey, out _);
                _lastAccess.TryRemove(oldestKey, out _);
                _peekTiles.TryRemove(oldestKey, out _);
            }
        }
    }

    /// <summary>One 64-bit cache key from a tile coordinate.</summary>
    private static long TileKey(int tileX, int tileZ) => ((long)tileX << 32) ^ (uint)tileZ;

    /// <summary>Stops generation, drops the caches, and flushes and closes the tile database.</summary>
    public void Dispose()
    {
        _tiles.Clear();
        _lastAccess.Clear();
        _peekTiles.Clear();
        _absent.Clear();
        _scheduler.Dispose();

        // Up to TileCacheMegabytes of tensors. Released here rather than left to the collector,
        // because something outside this mod can keep a closed world reachable - VSImGui 1.2.7's
        // mouse wheel handler on the game window holds every world loaded since the game started -
        // and then this would go with it.
        _pipeline.ClearCaches();

        // After the scheduler, so nothing more is generated; this writes out what is still queued.
        _store?.Dispose();
        _store = null;
        lock (_seaGate)
        {
            _riverFreeCoarse?.Dispose();
            _riverFreeCoarse = null;
            _riverFreePipeline = null;
        }
    }
}
