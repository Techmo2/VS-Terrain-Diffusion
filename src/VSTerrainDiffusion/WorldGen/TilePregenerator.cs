using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Fills a world's tile database ahead of play: every terrain tile within a radius of a point,
/// nearest first, with no chunks generated. A server can then be handed the world and its database
/// and serve that ground - chunks, Algernon's Terrain Sampler in <c>stored</c> mode, translocators
/// with <c>TranslocatorStoredTerrainOnly</c> - without running the model.
///
/// Then the coarse model's windows, over a much wider disc (<see cref="CoarseRadiusBlocks"/>): they
/// are cheap, and with them stored a <c>stored</c> sampler query beyond the tiles, or Rivers' coarse
/// sea test, does not run the model either.
///
/// Runs on a thread of its own, one tile at a time, behind every player's terrain in the inference
/// queue (<see cref="TerrainDiffusionProvider.Pregenerate"/>). Tiles and windows already stored are
/// passed over, so starting the same job again resumes it.
/// </summary>
public sealed class TilePregenerator : IDisposable
{
    /// <summary>How often <see cref="Progress"/> is raised while the job runs.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    private readonly TerrainDiffusionProvider _provider;
    private readonly ILogger _logger;
    private readonly (int X, int Z)[] _tiles;

    /// <summary>Block positions whose coarse windows are pregenerated, nearest first.</summary>
    private readonly (int X, int Z)[] _coarse;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    private int _done;
    private long _generated;
    private long _generateTicks;
    private int _coarseDone;
    private long _coarseComputed;
    private long _coarseTicks;
    private volatile string _failure;

    public int CentreX { get; }
    public int CentreZ { get; }
    public int RadiusBlocks { get; }

    /// <summary>
    /// How far coarse windows are pregenerated: four times the tile radius, and at least 16 km past
    /// it, up to 400 km. A 32 km window took about 2 s on a laptop RTX 3060 with TensorRT RTX, and
    /// with Rivers there are two pipelines' worth.
    /// </summary>
    public int CoarseRadiusBlocks { get; }

    /// <summary>Tiles the job covers.</summary>
    public int TileCount => _tiles.Length;

    /// <summary>Coarse areas the job covers after the tiles.</summary>
    public int CoarseAreaCount => _coarse.Length;

    public bool IsRunning => _thread.IsAlive;

    /// <summary>Asked to stop, and still finishing the tile it is on.</summary>
    public bool IsStopping => _stop.IsCancellationRequested && _thread.IsAlive;

    /// <summary>Everything the job covers is stored.</summary>
    public bool Completed => Volatile.Read(ref _done) == _tiles.Length &&
                             Volatile.Read(ref _coarseDone) == _coarse.Length && _failure == null;

    /// <summary>Raised on the job's own thread when it ends, however it ends.</summary>
    public event Action<TilePregenerator> Finished;

    /// <summary>
    /// Raised every <see cref="ProgressInterval"/> while the job runs, from a timer thread, so it
    /// keeps coming while a slow tile is being generated.
    /// </summary>
    public event Action<TilePregenerator> Progress;

    /// <param name="regionSize">The world's map region size in blocks, 512 in Vintage Story.</param>
    public TilePregenerator(TerrainDiffusionProvider provider, ILogger logger, int centreX, int centreZ, int radiusBlocks,
                            int regionSize)
    {
        _provider = provider;
        _logger = logger;
        CentreX = centreX;
        CentreZ = centreZ;
        RadiusBlocks = radiusBlocks;
        _tiles = TilesFor(provider.TileSize, regionSize, centreX, centreZ, radiusBlocks);
        CoarseRadiusBlocks = (int)Math.Min(400_000L, Math.Max(4L * radiusBlocks, radiusBlocks + 16_384L));
        _coarse = CoarsePoints(provider.CoarseCellBlocks, centreX, centreZ, CoarseRadiusBlocks);
        _thread = new Thread(Run) { IsBackground = true, Name = "TerrainDiffusion pregeneration" };
    }

    /// <summary>
    /// Every tile that finishing a chunk within the radius can read (<see cref="TerrainFootprint"/>):
    /// the map regions within reach of it, neighbouring columns included, and their margins. So the
    /// stored area reaches up to about 800 blocks past the radius. Nearest first, so that a job
    /// stopped part way leaves a solid disc rather than scattered tiles - and consecutive tiles share
    /// the pipeline's cached windows.
    /// </summary>
    private static (int X, int Z)[] TilesFor(int tileSize, int regionSize, int centreX, int centreZ, int radius)
    {
        int reach = radius + TerrainFootprint.NeighbourReachBlocks;
        long reachSquared = (long)reach * reach;
        var keys = new HashSet<(int X, int Z)>();

        for (int rz = FloorDiv(centreZ - reach, regionSize); rz <= FloorDiv(centreZ + reach, regionSize); rz++)
        for (int rx = FloorDiv(centreX - reach, regionSize); rx <= FloorDiv(centreX + reach, regionSize); rx++)
        {
            long nearX = Math.Clamp(centreX, rx * regionSize, (rx + 1) * regionSize - 1) - (long)centreX;
            long nearZ = Math.Clamp(centreZ, rz * regionSize, (rz + 1) * regionSize - 1) - (long)centreZ;
            if (nearX * nearX + nearZ * nearZ > reachSquared) continue;

            TerrainFootprint.Regions(rx * regionSize, rz * regionSize, (rx + 1) * regionSize, (rz + 1) * regionSize,
                regionSize, out int fromX, out int fromZ, out int toX, out int toZ);
            for (int z = FloorDiv(fromZ, tileSize); z <= FloorDiv(toZ - 1, tileSize); z++)
            for (int x = FloorDiv(fromX, tileSize); x <= FloorDiv(toX - 1, tileSize); x++)
                keys.Add((x, z));
        }

        var tiles = new List<(int X, int Z, long Distance)>(keys.Count);
        foreach ((int x, int z) in keys)
        {
            long midX = x * (long)tileSize + tileSize / 2 - centreX;
            long midZ = z * (long)tileSize + tileSize / 2 - centreZ;
            tiles.Add((x, z, midX * midX + midZ * midZ));
        }

        tiles.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance)
            : a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));
        return tiles.ConvertAll(t => (t.X, t.Z)).ToArray();
    }

    /// <summary>
    /// Points close enough together that every coarse window over the disc is touched: a window is
    /// 64 cells wide on a stride of 48, so one point every 16 cells puts several in each.
    /// </summary>
    private static (int X, int Z)[] CoarsePoints(int cellBlocks, int centreX, int centreZ, int radius)
    {
        int spacing = 16 * cellBlocks;
        int steps = radius / spacing + 1;
        long reach = radius + (long)spacing;
        var points = new List<(int X, int Z, long Distance)>();
        for (int b = -steps; b <= steps; b++)
        for (int a = -steps; a <= steps; a++)
        {
            long dx = (long)a * spacing, dz = (long)b * spacing;
            long distance = dx * dx + dz * dz;
            if (distance > reach * reach) continue;
            points.Add(((int)(centreX + dx), (int)(centreZ + dz), distance));
        }
        points.Sort((p, q) => p.Distance.CompareTo(q.Distance));
        return points.ConvertAll(p => (p.X, p.Z)).ToArray();
    }

    public void Start() => _thread.Start();

    /// <summary>Asks the job to stop after the tile it is on.</summary>
    public void Stop() => _stop.Cancel();

    private void Run()
    {
        _logger.Notification(
            "[{0}] Pregen started: {1} tiles within {2} blocks of ({3}, {4}), then coarse areas out to {5} blocks.",
            DiffusionPaths.ModId, Count(_tiles.Length), Count(RadiusBlocks), CentreX, CentreZ, Count(CoarseRadiusBlocks));

        using var progress = new Timer(_ => RaiseProgress(), null, ProgressInterval, ProgressInterval);
        try
        {
            for (int i = 0; i < _tiles.Length && !_stop.IsCancellationRequested; i++)
            {
                long start = Stopwatch.GetTimestamp();
                if (_provider.Pregenerate(_tiles[i].X, _tiles[i].Z))
                {
                    Interlocked.Increment(ref _generated);
                    Interlocked.Add(ref _generateTicks, Stopwatch.GetTimestamp() - start);
                }
                Volatile.Write(ref _done, i + 1);
            }

            for (int i = 0; i < _coarse.Length && !_stop.IsCancellationRequested; i++)
            {
                long start = Stopwatch.GetTimestamp();
                if (_provider.PregenerateCoarse(_coarse[i].X, _coarse[i].Z))
                {
                    Interlocked.Increment(ref _coarseComputed);
                    Interlocked.Add(ref _coarseTicks, Stopwatch.GetTimestamp() - start);
                }
                Volatile.Write(ref _coarseDone, i + 1);
            }
        }
        catch (Exception e)
        {
            // The world itself carries on: chunks generate their own terrain as they always have.
            _failure = e.Message;
            _logger.Warning("[{0}] Pregen failed: {1}", DiffusionPaths.ModId, e.Message);
        }

        progress.Dispose();
        _logger.Notification("[{0}] Pregen {1}: {2}", DiffusionPaths.ModId,
            Completed ? "finished" : "stopped", Describe());

        try
        {
            Finished?.Invoke(this);
        }
        catch (Exception e)
        {
            _logger.Warning("[{0}] Pregen: the finish handler failed: {1}", DiffusionPaths.ModId, e.Message);
        }
    }

    private void RaiseProgress()
    {
        if (_stop.IsCancellationRequested) return;
        try
        {
            Progress?.Invoke(this);
        }
        catch (Exception e)
        {
            _logger.Warning("[{0}] Pregen: the progress report failed: {1}", DiffusionPaths.ModId, e.Message);
        }
    }

    /// <summary>Progress, and the time left at the pace so far.</summary>
    public string Describe()
    {
        int done = Volatile.Read(ref _done), coarseDone = Volatile.Read(ref _coarseDone);
        long generated = Interlocked.Read(ref _generated), computed = Interlocked.Read(ref _coarseComputed);
        long stored = done - generated;
        string line = $"{Count(done)}/{Count(_tiles.Length)} tiles " +
                      $"({100.0 * done / _tiles.Length:0}%{(stored > 0 ? $", {Count(stored)} already stored" : "")}), " +
                      $"{coarseDone}/{_coarse.Length} coarse areas";

        if (_failure != null) return line + ", failed: " + _failure;
        if (!IsRunning || _stop.IsCancellationRequested) return line;

        // At the pace so far, assuming the rest are unstored, which is what a fresh job is. Before the
        // first coarse window, guess half a tile's time for one.
        double tilePace = generated == 0 ? 0 : Interlocked.Read(ref _generateTicks) / (double)Stopwatch.Frequency / generated;
        double coarsePace = computed > 0
            ? Interlocked.Read(ref _coarseTicks) / (double)Stopwatch.Frequency / computed
            : tilePace / 2;
        double seconds = tilePace * (_tiles.Length - done) + coarsePace * (_coarse.Length - coarseDone);
        return seconds <= 0 ? line : line + ", about " + DescribeDuration(seconds) + " left";
    }

    /// <summary>A count with thousands separators, the same on every machine.</summary>
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string DescribeDuration(double seconds) => seconds switch
    {
        < 90 => $"{seconds:0} s",
        < 5400 => $"{seconds / 60:0} min",
        < 172800 => (seconds / 3600).ToString("0.#", CultureInfo.InvariantCulture) + " h",
        _ => (seconds / 86400).ToString("0.#", CultureInfo.InvariantCulture) + " days"
    };

    /// <summary>
    /// Stops the job and waits for the tile it is on, so the provider is never disposed under a tile
    /// that is still being generated.
    /// </summary>
    public void Dispose()
    {
        _stop.Cancel();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromMinutes(2)))
        {
            _logger.Warning("[{0}] Pregen: the current tile did not finish within two minutes of stopping.",
                DiffusionPaths.ModId);
        }
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
