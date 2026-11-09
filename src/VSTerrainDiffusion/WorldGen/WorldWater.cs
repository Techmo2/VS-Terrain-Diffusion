using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using TerrainDiffusion.Water;
using Vintagestory.API.Common;
using PlayerDistance = System.Func<int, int, long>;
using VSTerrainDiffusion.Core;
using static VSTerrainDiffusion.Core.IntMath;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Lakes and rivers for the world (TerrainDiffusion.Water's <see cref="RiverGraph"/>), solved on
/// 256-block water tiles whatever size the terrain tiles are, and handed to the terrain pass chunk by
/// chunk once final.
///
/// Terrain tiles feed the graph in the background as they are generated. A chunk whose water is not
/// final yet (<see cref="Settle"/>) adds the tiles its water is waiting on, nearest first, generating
/// them if need be. Released chunks are copied out into <see cref="ChunkWater"/>, which the terrain
/// pass reads without touching the graph. The water depends only on the terrain and the world's water
/// settings, never on the order tiles came in, so tiles far from every player can be forgotten and
/// added again later with the same result.
///
/// Every call into the graph holds <see cref="_graphLock"/>; nothing that may run the model does.
/// </summary>
public sealed class WorldWater : IDisposable
{
    public const int TileBlocks = 256;
    private const int ChunkBlocks = 32;
    private const int ChunksPerTile = TileBlocks / ChunkBlocks;
    private const int Cells = TileBlocks * TileBlocks;

    /// <summary>Most tiles the background thread adds in one call (the lock is held that long).</summary>
    private const int BackgroundBatch = 16;

    /// <summary>Most already-available tiles a waiting chunk adds in one call.</summary>
    private const int SettleBatch = 32;

    /// <summary>Memory the held tiles may use before far ones are forgotten.</summary>
    private const long BudgetBytes = 512L * 1024 * 1024;

    /// <summary>Tiles within this many tiles of a player are never forgotten.</summary>
    private const int KeepTilesNearPlayers = 4;

    /// <summary>
    /// Nor are the tiles of the last this many arrivals, so a tile a waiting chunk just added back is
    /// not forgotten again before the chunk is released.
    /// </summary>
    private const int KeepRecentArrivals = 128;

    /// <summary>Released chunks kept before those far from every player are dropped (they can be copied out again).</summary>
    private const int MaxStoredChunks = 65_536;
    private const long KeepChunksNearPlayersBlocks = 2048;

    /// <summary>Tiles waiting for the water above which pregeneration pauses.</summary>
    public const int PregenBacklogLimit = 24;

    private readonly TerrainDiffusionProvider _provider;
    private readonly DiffusionWorldSettings _settings;
    private readonly ILogger _logger;
    private readonly PlayerDistance _distanceToPlayers;
    private readonly int _terrainTile;
    private readonly int _seaLevel;
    private readonly double _riverThreshold;
    private readonly float _riverWidth;

    /// <summary>Lakes stand this many blocks below their spill; what they leave dry is wetland.</summary>
    private readonly int _wetlandDepth;

    private readonly RiverGraph _graph;
    private readonly object _graphLock = new();

    /// <summary>Runoff (mm/yr) per cell of every held tile, for river geometry. Under the lock.</summary>
    private readonly Dictionary<TileCoord, float[]> _runoff = new();

    private readonly ConcurrentDictionary<long, ChunkWater> _chunks = new();
    private int _storedChunks;

    private readonly BlockingCollection<TerrainTile> _arrivals = new();
    private readonly Thread _thread;
    private volatile bool _stopping;
    private int _inFlight;

    private long _tilesForChunks;
    private long _chunkWaitTicks;
    private long _tilesForgotten;
    private long _releasedTwice;
    private long _mismatches;
    private long _backgroundFailures;

    public WorldWater(TerrainDiffusionProvider provider, DiffusionWorldSettings settings, uint seed,
                      bool rivers, PlayerDistance distanceToPlayers, ILogger logger)
    {
        _provider = provider;
        _settings = settings;
        _logger = logger;
        _distanceToPlayers = distanceToPlayers;
        _terrainTile = provider.TileSize;
        _seaLevel = settings.SeaLevel;
        RiverMode = rivers ? settings.RiverMode : RiverMode.Off;
        // The stricter of the mode's threshold and the trim, a share of the peak (the cap).
        _riverThreshold = RiverMode == RiverMode.Off
            ? 0.0
            : Math.Max(RiverGeometry.ThresholdCells(RiverMode), settings.RiverTrim * RiverGraphSettings.DefaultDischargeCap);
        _riverWidth = settings.RiverWidth;
        _wetlandDepth = settings.WetlandDepth;

        Settings = new RiverGraphSettings
        {
            TileSize = TileBlocks,
            ChunksPerTileSide = ChunksPerTile,
            CellSizeMetres = settings.MetersPerBlock,
            Seed = seed,
            LakeCells = TileBlocks * TileBlocks,
            FillMargin = null,
            MaxFillRadius = RiverGraphSettings.DefaultMaxFillRadius,
            FlatGroundWeight = RiverGraphSettings.DefaultFlatGroundWeight,
            DischargeCap = RiverGraphSettings.DefaultDischargeCap,
            Meander = settings.MeanderSinuosity <= 1f ? null : new MeanderSettings
            {
                MaxSinuosity = settings.MeanderSinuosity,
                WavelengthMetres = settings.MeanderWavelengthMeters,
                SlopeScale = settings.MeanderSlope,
                ReliefScaleMetres = settings.MeanderReliefMeters,
                ConfinementMetres = settings.MeanderConfinementMeters,
                FreeReliefRatio = settings.MeanderFreeRelief,
                WaveCount = 9
            },
            MaxDegreeOfParallelism = settings.WaterThreads > 0
                ? settings.WaterThreads
                : RiverGraphSettings.DefaultMaxDegreeOfParallelism
        };
        _graph = new RiverGraph(Settings);

        _provider.TileGenerated += OnTileGenerated;
        _thread = new Thread(Run) { IsBackground = true, Name = "TerrainDiffusion water" };
        _thread.Start();
    }

    public RiverGraphSettings Settings { get; }

    /// <summary>The rivers cut into the land: the world's setting, or off when another mod has rivers.</summary>
    public RiverMode RiverMode { get; }

    private bool HasRivers => _riverThreshold > 0.0;

    /// <summary>Terrain tiles waiting to be added in the background.</summary>
    public int Backlog => _arrivals.Count + Volatile.Read(ref _inFlight);

    /// <summary>
    /// The water of chunk column (chunkX, chunkZ) once it is final, with the river cells of its 8
    /// neighbours when there are rivers (a channel reaches up to 31 blocks). Adds, and if need be
    /// generates, the terrain tiles it is waiting on.
    /// </summary>
    public SettledWater Settle(int chunkX, int chunkZ)
    {
        int halo = HasRivers ? 1 : 0;
        if (TryAssemble(chunkX, chunkZ, halo, fromGraph: false, out SettledWater water)) return water;

        double row = (chunkZ + 0.5) / ChunksPerTile, column = (chunkX + 0.5) / ChunksPerTile;
        double radius = 0.5 + (halo + 0.5) * ChunkBlocks / (double)TileBlocks;
        long start = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                var wanted = new List<TileCoord>();
                lock (_graphLock)
                {
                    if (TryAssemble(chunkX, chunkZ, halo, fromGraph: true, out water)) return water;
                    wanted.AddRange(_graph.GetWantedTiles(row, column, radius));
                }

                if (wanted.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"The water of chunk ({chunkX}, {chunkZ}) is not final, and the river graph is waiting on no tile.");
                }

                wanted.Sort((a, b) =>
                {
                    int byDistance = TileDistance(a, row, column).CompareTo(TileDistance(b, row, column));
                    return byDistance != 0 ? byDistance : a.CompareTo(b);
                });

                // Whatever needs no model goes in at once, as one batch; otherwise the nearest tile is
                // generated, and the chunk looked at again.
                var batch = new List<TileCoord>();
                foreach (TileCoord tile in wanted)
                {
                    if (batch.Count < SettleBatch && PartsAvailable(tile)) batch.Add(tile);
                }
                if (batch.Count == 0) batch.Add(wanted[0]);

                var built = new List<(TileCoord, float[], float[])>(batch.Count);
                foreach (TileCoord tile in batch)
                {
                    if (TryBuild(tile, (x, z) => _provider.GetTile(x, z), out float[] heights, out float[] runoff))
                        built.Add((tile, heights, runoff));
                }
                Interlocked.Add(ref _tilesForChunks, Add(built));
            }
        }
        finally
        {
            Interlocked.Add(ref _chunkWaitTicks, Stopwatch.GetTimestamp() - start);
        }
    }

    /// <summary>
    /// The lakes of a chunk as far as they are known, for a throwaway peek: never waits, never
    /// generates, and has no rivers.
    /// </summary>
    public SettledWater Peek(int chunkX, int chunkZ)
    {
        if (_chunks.TryGetValue(ChunkKey(chunkX, chunkZ), out ChunkWater stored))
            return new SettledWater(stored.LakeTop, stored.Wetland, Array.Empty<RiverCell>());

        if (!Monitor.TryEnter(_graphLock)) return SettledWater.Dry;
        try
        {
            var tile = new TileCoord(FloorDiv(chunkZ, ChunksPerTile), FloorDiv(chunkX, ChunksPerTile));
            if (!_graph.TryGetTile(tile, out TileWater water) || !water.IsFilled) return SettledWater.Dry;
            return new SettledWater(LakeTops(water, chunkX - tile.Column * ChunksPerTile, chunkZ - tile.Row * ChunksPerTile), null,
                Array.Empty<RiverCell>());
        }
        finally
        {
            Monitor.Exit(_graphLock);
        }
    }

    /// <summary>Whether a column gets lake or river water. Waits for its chunk to be final.</summary>
    public bool IsWater(int blockX, int blockZ)
    {
        int chunkX = FloorDiv(blockX, ChunkBlocks), chunkZ = FloorDiv(blockZ, ChunkBlocks);
        SettledWater water = Settle(chunkX, chunkZ);

        TerrainTile tile = _provider.GetTileAt(blockX, blockZ);
        int index = tile.Index(blockX - tile.BlockX, blockZ - tile.BlockZ);
        int surface = tile.SurfaceY[index];
        if (tile.ElevationMeters[index] < 0f) return false;   // the sea, not a lake or river

        int lake = water.LakeTop?[(blockZ - chunkZ * ChunkBlocks) * ChunkBlocks + blockX - chunkX * ChunkBlocks] ?? 0;
        if (lake >= _seaLevel && lake > surface) return true;

        foreach (RiverCell cell in water.Rivers)
        {
            if (cell.Dry) continue;
            if (cell.DistanceTo(blockX, blockZ) <= cell.Reach) return true;
        }
        return false;
    }

    public string Describe()
    {
        RiverGraphStatistics stats;
        int held;
        lock (_graphLock)
        {
            stats = _graph.GetStatistics();
            held = _runoff.Count;
        }

        double solveMillis = stats.FillMilliseconds + stats.RouteMilliseconds + stats.DischargeMilliseconds;
        long bytes = stats.Bytes + (long)held * Cells * sizeof(float);
        string text =
            $"rivers {RiverMode.ToString().ToLowerInvariant()}; {held} tiles held ({bytes / 1048576.0:0} of {BudgetBytes / 1048576} MB), " +
            $"{Volatile.Read(ref _storedChunks)} chunks ready, backlog {Backlog}; " +
            $"{(stats.Step > 0 ? solveMillis / stats.Step : 0):0} ms per tile over {stats.Step}; " +
            $"{Interlocked.Read(ref _tilesForChunks)} added for waiting chunks " +
            $"({Interlocked.Read(ref _chunkWaitTicks) / (double)Stopwatch.Frequency:0.#} s waited); " +
            $"{Interlocked.Read(ref _tilesForgotten)} forgotten; violations {stats.Violations}";
        long twice = Interlocked.Read(ref _releasedTwice);
        if (twice > 0) text += $"; {twice} chunks released again, {Interlocked.Read(ref _mismatches)} differing";
        long failures = Interlocked.Read(ref _backgroundFailures);
        if (failures > 0) text += $"; {failures} background FAILURES";
        return text;
    }

    /// <summary>Stops feeding tiles in the background; waiting chunks still add their own.</summary>
    public void StopBackground()
    {
        if (_stopping) return;
        _stopping = true;
        _provider.TileGenerated -= OnTileGenerated;
        _arrivals.CompleteAdding();
    }

    public void Dispose()
    {
        StopBackground();
        _graph.Cancel();
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
            _logger.Warning("[{0}] The water thread did not stop within 10 s.", DiffusionPaths.ModId);
    }

    // ---- Background feeding ----

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

    // ---- The graph ----

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

    private static double TileDistance(TileCoord tile, double row, double column) =>
        Math.Pow(tile.Row + 0.5 - row, 2) + Math.Pow(tile.Column + 0.5 - column, 2);

    private static long ChunkKey(int chunkX, int chunkZ) => ((long)chunkX << 32) | (uint)chunkZ;
}

/// <summary>A chunk's final water: its lake tops, and the river cells within reach of it.</summary>
public sealed class SettledWater
{
    public static readonly SettledWater Dry = new(null, null, Array.Empty<RiverCell>());

    /// <summary>Top lake water block per column (lz * 32 + lx), 0 where none; null without lakes.</summary>
    public readonly short[] LakeTop;

    /// <summary>Wetness of wetland columns (1 to 255), 0 elsewhere; null without wetland.</summary>
    public readonly byte[] Wetland;

    /// <summary>River cells of the chunk and its neighbours, world coordinates.</summary>
    public readonly RiverCell[] Rivers;

    public SettledWater(short[] lakeTop, byte[] wetland, RiverCell[] rivers)
    {
        LakeTop = lakeTop;
        Wetland = wetland;
        Rivers = rivers;
    }
}
