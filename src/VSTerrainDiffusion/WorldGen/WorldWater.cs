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
public sealed partial class WorldWater : IDisposable
{
    /// <summary>Edge of a water tile, in blocks, whatever size the terrain tiles are.</summary>
    public const int TileBlocks = 256;

    /// <summary>Edge of a chunk, in blocks.</summary>
    private const int ChunkBlocks = 32;

    /// <summary>Chunks along a water tile's edge.</summary>
    private const int ChunksPerTile = TileBlocks / ChunkBlocks;

    /// <summary>Cells (columns) in a water tile.</summary>
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

    /// <summary>Stored chunks within this many blocks of a player are never dropped.</summary>
    private const long KeepChunksNearPlayersBlocks = 2048;

    /// <summary>Tiles waiting for the water above which pregeneration pauses.</summary>
    public const int PregenBacklogLimit = 24;

    /// <summary>
    /// Width in metres of the largest lake fill window from water version 2: the library's default 7 x 7
    /// tiles at the 15 m cells it was tuned on.
    /// </summary>
    private const double FillReachMeters = (2 * RiverGraphSettings.DefaultMaxFillRadius + 1) * TileBlocks * 15.0;

    /// <summary>The most rings of tiles the library lets a lake fill grow to.</summary>
    private const int LibraryMaxFillRadius = 8;

    /// <summary>
    /// The most rings of tiles a lake fill may grow to before a lake still decided by the window edge is
    /// cut off at a tile seam.
    /// </summary>
    /// <remarks>
    /// A lake spans more cells at a finer resolution, so from water version 2 the window keeps the same
    /// width in metres as the library's default at 15 m: 3 rings at 15 m and coarser, 5 at 10 m, 7 at
    /// 7.5 m, and the library's limit of 8 at 6 m and finer. Version 1 always used the default, 3.
    /// Widening only happens for the tiles a lake needs it for.
    /// </remarks>
    /// <param name="waterVersion">The world's water version.</param>
    /// <param name="metersPerBlock">Horizontal metres per block.</param>
    /// <returns>Rings of tiles around the tile, 3 to 8.</returns>
    public static int FillRadius(int waterVersion, float metersPerBlock)
    {
        if (waterVersion < 2 || metersPerBlock <= 0f) return RiverGraphSettings.DefaultMaxFillRadius;

        // Tiles across the window, rounded up to an odd count centred on the tile.
        double tilesAcross = FillReachMeters / (TileBlocks * (double)metersPerBlock);
        int radius = (int)Math.Ceiling((tilesAcross - 1.0) / 2.0 - 1e-9);
        return Math.Clamp(radius, RiverGraphSettings.DefaultMaxFillRadius, LibraryMaxFillRadius);
    }

    /// <summary>The world's terrain.</summary>
    private readonly TerrainDiffusionProvider _provider;

    /// <summary>The world's settings, for heights and the water settings.</summary>
    private readonly DiffusionWorldSettings _settings;

    /// <summary>The server log.</summary>
    private readonly ILogger _logger;

    /// <summary>Distance in blocks from a position to the nearest player.</summary>
    private readonly PlayerDistance _distanceToPlayers;

    /// <summary>Edge of a terrain tile, in blocks.</summary>
    private readonly int _terrainTile;

    /// <summary>The world's sea level, as a block Y.</summary>
    private readonly int _seaLevel;

    /// <summary>Discharge, in cells, from which a cell carries a river; 0 for no rivers.</summary>
    private readonly double _riverThreshold;

    /// <summary>The world's river width multiplier.</summary>
    private readonly float _riverWidth;

    /// <summary>Lakes stand this many blocks below their spill; what they leave dry is wetland.</summary>
    private readonly int _wetlandDepth;

    /// <summary>
    /// The lake and river solver, over every held water tile. Only touched under <see cref="_graphLock"/>.
    /// </summary>
    private readonly RiverGraph _graph;

    /// <summary>Guards <see cref="_graph"/> and <see cref="_runoff"/>.</summary>
    private readonly object _graphLock = new();

    /// <summary>Runoff (mm/yr) per cell of every held tile, for river geometry. Under the lock.</summary>
    private readonly Dictionary<TileCoord, float[]> _runoff = new();

    /// <summary>Released chunks' water, by <see cref="ChunkKey"/>.</summary>
    private readonly ConcurrentDictionary<long, ChunkWater> _chunks = new();

    /// <summary>How many chunks <see cref="_chunks"/> holds.</summary>
    private int _storedChunks;

    /// <summary>Terrain tiles queued for the water thread.</summary>
    private readonly BlockingCollection<TerrainTile> _arrivals = new();

    /// <summary>The water thread.</summary>
    private readonly Thread _thread;

    /// <summary>Set once background feeding stops.</summary>
    private volatile bool _stopping;

    /// <summary>Tiles the water thread has taken but not finished.</summary>
    private int _inFlight;

    /// <summary>Water tiles added for waiting chunks, for <see cref="Describe"/>.</summary>
    private long _tilesForChunks;

    /// <summary>Stopwatch ticks chunks spent waiting, for <see cref="Describe"/>.</summary>
    private long _chunkWaitTicks;

    /// <summary>Water tiles forgotten to stay within budget.</summary>
    private long _tilesForgotten;

    /// <summary>Chunks released a second time.</summary>
    private long _releasedTwice;

    /// <summary>Chunks released a second time with different water; should stay 0.</summary>
    private long _mismatches;

    /// <summary>Batches the water thread failed to add.</summary>
    private long _backgroundFailures;

    /// <summary>Creates the world's water and starts feeding it from generated terrain.</summary>
    /// <param name="provider">The world's terrain.</param>
    /// <param name="settings">The world's settings.</param>
    /// <param name="seed">The world seed, for meanders.</param>
    /// <param name="rivers">False when another mod supplies the rivers.</param>
    /// <param name="distanceToPlayers">Distance in blocks from a position to the nearest player.</param>
    /// <param name="logger">The server log.</param>
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
            MaxFillRadius = FillRadius(settings.WaterVersion, settings.MetersPerBlock),
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

    /// <summary>The river graph's settings, derived from the world's.</summary>
    public RiverGraphSettings Settings { get; }

    /// <summary>The rivers cut into the land: the world's setting, or off when another mod has rivers.</summary>
    public RiverMode RiverMode { get; }

    /// <summary>Whether rivers are cut at all.</summary>
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

    /// <summary>One line on the water's state, for <c>/tdiff status</c>.</summary>
    /// <returns>The line.</returns>
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

    /// <summary>Stops the background thread and cancels any solve in progress.</summary>
    public void Dispose()
    {
        StopBackground();
        _graph.Cancel();
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
            _logger.Warning("[{0}] The water thread did not stop within 10 s.", DiffusionPaths.ModId);
    }

    /// <summary>Squared distance from a tile's centre to a point, in tiles.</summary>
    /// <param name="tile">The tile.</param>
    /// <param name="row">The point's row, in tiles.</param>
    /// <param name="column">The point's column, in tiles.</param>
    /// <returns>The squared distance.</returns>
    private static double TileDistance(TileCoord tile, double row, double column) =>
        Math.Pow(tile.Row + 0.5 - row, 2) + Math.Pow(tile.Column + 0.5 - column, 2);

    /// <summary>A chunk's key in <see cref="_chunks"/>: X in the high half, Z in the low.</summary>
    /// <param name="chunkX">Chunk column.</param>
    /// <param name="chunkZ">Chunk row.</param>
    /// <returns>The key.</returns>
    private static long ChunkKey(int chunkX, int chunkZ) => ((long)chunkX << 32) | (uint)chunkZ;
}
