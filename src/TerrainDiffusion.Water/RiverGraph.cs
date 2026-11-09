using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using TerrainDiffusion.Water.Grid;
using TerrainDiffusion.Water.Meander;
using TerrainDiffusion.Water.World;

namespace TerrainDiffusion.Water;

/// <summary>
/// Lakes, rivers and meandering for an unbounded world whose terrain arrives tile by tile, in any order.
/// </summary>
/// <remarks>
/// <para>Every cell's next cell is decided once, by its own tile, from a bounded neighbourhood, so the river
/// network is one graph whatever order the tiles come in, and the water of the same world is the same in any
/// order. Each tile goes through three stages, each as soon as what it needs has arrived:</para>
/// <list type="number">
/// <item><description><b>Fill</b> (needs the tile's 8 neighbours, or more rings of tiles where a lake runs into
/// the edge of its window): the water level of each cell, lakes filled to their spill
/// (<see cref="Lakes.DepressionFill"/>, <see cref="World.FillStage"/>).</description></item>
/// <item><description><b>Directions</b> (needs the neighbours' fill, rarely the 5 × 5 tiles'): the neighbour each
/// cell's water steps to, strictly downhill and aimed by the meander headings
/// (<see cref="Routing.FlowDirectionSolver"/>, <see cref="MeanderField"/>).</description></item>
/// <item><description><b>Discharge</b>: upstream cells per cell; a change propagates downstream tile to tile
/// until nothing changes (<see cref="World.DischargeStage"/>).</description></item>
/// </list>
/// <para>A cell's discharge is final once everything upstream of it is known, or once the known part reaches the
/// cap: then it can never change. A chunk is released once all its cells are final, and never changes after.
/// <see cref="GetWantedTiles"/> names the tiles the unfinished water near a point is waiting on, so a generator
/// can make those first.</para>
/// <para><b>Threads.</b> One caller at a time: <see cref="AddTile"/> and the other members must not run
/// concurrently with each other. <see cref="Cancel"/> may be called from any thread at any time. Internally,
/// tiles made ready by one arrival, or by one batch (<see cref="AddTiles"/>), are filled and routed in parallel
/// (up to <see cref="RiverGraphSettings.MaxDegreeOfParallelism"/>); the result does not depend on it.</para>
/// <para><b>Coordinates.</b> Tile (Row, Column) covers world cells <c>[Row · T, (Row + 1) · T)</c> along Z by
/// <c>[Column · T, (Column + 1) · T)</c> along X, for tile size T. Every per-tile array is row-major.</para>
/// </remarks>
public sealed class RiverGraph
{
    /// <summary>Index tables shared by every tile.</summary>
    private readonly TileGeometry geometry;
    /// <summary>The tiles held.</summary>
    private readonly TileStore store;
    /// <summary>Stage 1: water levels.</summary>
    private readonly FillStage fill;
    /// <summary>Stage 2: directions and networks.</summary>
    private readonly RoutingStage routing;
    /// <summary>Stage 3: discharge and release.</summary>
    private readonly DischargeStage discharge;
    /// <summary>The search behind <see cref="GetWantedTiles"/>.</summary>
    private readonly UpstreamDemand demand;
    /// <summary>Set by <see cref="Cancel"/>. Never disposed: it has no timer or wait handle to release.</summary>
    private readonly CancellationTokenSource cancellation = new();

    /// <summary>Filled cells decided only by their window edge.</summary>
    private long uncertainCells;

    /// <summary>Terminal lakes found while routing.</summary>
    private long terminalFlats;

    /// <summary>Cells of over-wide flats made sinks.</summary>
    private long escapedCells;
    /// <summary>Target sinuosity summed over routed land cells.</summary>
    private double sinuositySum;
    /// <summary>Routed land cells counted in <see cref="sinuositySum"/>.</summary>
    private long sinuosityCells;
    /// <summary>Stopwatch ticks spent filling.</summary>
    private long fillTicks;

    /// <summary>Stopwatch ticks spent routing.</summary>
    private long routeTicks;

    /// <summary>Stopwatch ticks spent on meander headings, summed over threads.</summary>
    private long meanderTicks;

    /// <summary>Stopwatch ticks spent on discharge and release.</summary>
    private long dischargeTicks;

    /// <summary>Stopwatch ticks of the last <see cref="AddTile"/>.</summary>
    private long lastAddTicks;
    /// <summary>Bumped when a tile is forgotten (invalidates the wanted-tiles cache).</summary>
    private int revision;
    /// <summary>The query the cached wanted tiles answer.</summary>
    private WantedQuery? wantedKey;
    /// <summary>The cached answer of <see cref="GetWantedTiles"/>.</summary>
    private IReadOnlyCollection<TileCoord> wantedTiles = Array.Empty<TileCoord>();

    /// <summary>An empty world with the meander field laid out from the seed.</summary>
    /// <param name="settings">Settings (validated).</param>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    public RiverGraph(RiverGraphSettings settings)
        : this(settings, null)
    {
    }

    /// <summary>An empty world with explicit meander tables (to match another implementation exactly).</summary>
    /// <param name="settings">Settings (validated).</param>
    /// <param name="meanderTables">The meander field's waves and sinuosity table; null lays them out from the
    /// seed. Ignored without meandering.</param>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    public RiverGraph(RiverGraphSettings settings, MeanderTables? meanderTables)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Settings = settings;
        geometry = new TileGeometry(settings.TileSize, settings.ChunksPerTileSide);
        store = new TileStore(settings.TileSize);
        if (settings.Meander is { } meander)
        {
            Meander = meanderTables is null
                ? new MeanderField(meander, settings.CellSizeMetres, settings.Seed)
                : new MeanderField(meander, settings.CellSizeMetres, settings.Seed, meanderTables);
        }
        fill = new FillStage(store, settings.EffectiveFillMargin, settings.EffectiveLakeCells, settings.MaxFillRadius,
                             settings.MaxDegreeOfParallelism);
        routing = new RoutingStage(store, geometry, Meander, settings.FlatGroundWeight, settings.MaxDegreeOfParallelism);
        discharge = new DischargeStage(store, geometry, settings.DischargeCap);
        demand = new UpstreamDemand(store, geometry, settings.DischargeCap);
    }

    /// <summary>The settings.</summary>
    public RiverGraphSettings Settings { get; }

    /// <summary>The meander field, or null without meandering.</summary>
    public MeanderField? Meander { get; }

    /// <summary>Tile arrivals so far. Each <see cref="AddTile"/> is one step; chunks it releases carry
    /// <c>Step − 1</c>.</summary>
    public int Step { get; private set; }

    /// <summary>Tiles held.</summary>
    public int TileCount => store.Count;

    /// <summary>Whether <see cref="Cancel"/> was called.</summary>
    public bool IsCancelled => cancellation.IsCancellationRequested;

    /// <summary>
    /// Abandons this world: the work in progress throws <see cref="OperationCanceledException"/> at its next
    /// check, and so does every later call that changes it. Safe from any thread. A world cancelled in the middle
    /// of <see cref="AddTile"/> may be inconsistent and must be discarded.
    /// </summary>
    public void Cancel() => cancellation.Cancel();

    /// <summary>Whether <paramref name="tile"/> has arrived (and was not forgotten).</summary>
    /// <param name="tile">The tile.</param>
    /// <returns>True if held.</returns>
    public bool Contains(TileCoord tile) => store.Contains(tile);

    /// <summary>
    /// Adds a generated tile and does every stage it makes possible, here and in its neighbours.
    /// </summary>
    /// <param name="tile">The tile; must not be held already.</param>
    /// <param name="elevation">Its terrain height in metres per cell, row-major, <c>TileSize²</c> finite values
    /// (copied). Cells at or below 0 are sea.</param>
    /// <returns>The tiles whose water changed, and the chunks released.</returns>
    /// <exception cref="ArgumentException">The elevation has the wrong size or a non-finite value.</exception>
    /// <exception cref="InvalidOperationException">The tile is held already.</exception>
    /// <exception cref="OperationCanceledException">The world was cancelled.</exception>
    public TileUpdate AddTile(TileCoord tile, ReadOnlySpan<float> elevation)
        => AddTiles(new ReadOnlySpan<TileCoord>(in tile), elevation);

    /// <summary>
    /// Adds several generated tiles at once and does every stage they make possible. The water is the same as
    /// adding them one by one (in any order); only the release steps differ (every chunk released carries the
    /// last arrival's step). Use it when tiles arrive faster than they are solved: the tiles they make ready are
    /// filled and routed in parallel, and the discharge is swept once.
    /// </summary>
    /// <param name="tiles">The tiles; distinct, none held already. They take steps in this order.</param>
    /// <param name="elevations">Their heights, one tile after the other, each as for <see cref="AddTile"/>
    /// (<c>tiles.Length · TileSize²</c> values, copied).</param>
    /// <returns>The tiles whose water changed (every added tile among them), and the chunks released.</returns>
    /// <exception cref="ArgumentException">The elevations have the wrong size or a non-finite value.</exception>
    /// <exception cref="InvalidOperationException">A tile is held already or listed twice. Nothing is added.</exception>
    /// <exception cref="OperationCanceledException">The world was cancelled.</exception>
    public TileUpdate AddTiles(ReadOnlySpan<TileCoord> tiles, ReadOnlySpan<float> elevations)
    {
        ThrowIfCancelled();
        int cells = geometry.CellCount;
        if (elevations.Length != (long)tiles.Length * cells)
        {
            throw new ArgumentException($"{tiles.Length} tiles need {(long)tiles.Length * cells} heights, not {elevations.Length}.",
                                        nameof(elevations));
        }
        var arriving = new HashSet<TileCoord>();
        for (int k = 0; k < tiles.Length; k++)
        {
            ReadOnlySpan<float> elevation = elevations.Slice(k * cells, cells);
            for (int i = 0; i < elevation.Length; i++)
            {
                if (!float.IsFinite(elevation[i]))
                {
                    throw new ArgumentException($"Height {i} of tile {tiles[k]} is {elevation[i]}.", nameof(elevations));
                }
            }
            if (store.Contains(tiles[k]) || !arriving.Add(tiles[k]))
            {
                throw new InvalidOperationException($"Tile {tiles[k]} has arrived already.");
            }
        }

        long start = Stopwatch.GetTimestamp();
        var changed = new HashSet<TileCoord>();
        for (int k = 0; k < tiles.Length; k++)
        {
            store.Add(new TileState(tiles[k], elevations.Slice(k * cells, cells),
                                    geometry.ChunksPerSide * geometry.ChunksPerSide, Step));
            Step++;
            changed.Add(tiles[k]);
        }
        int releaseStep = Step - 1;
        var released = new List<ReleasedChunk>();

        List<TileState> filled = FillReadyTilesAround(arriving, changed);
        List<TileState> routed = RouteReadyTilesAround(filled);

        long dischargeStart = Stopwatch.GetTimestamp();
        var sweep = new HashSet<TileCoord>();
        foreach (TileState state in routed)
        {
            for (int dr = -1; dr <= 1; dr++)
            {
                for (int dc = -1; dc <= 1; dc++)
                {
                    TileCoord around = state.Tile.Offset(dr, dc);
                    if (store.Get(around)?.IsRouted == true)
                    {
                        sweep.Add(around);
                    }
                }
            }
        }
        discharge.Propagate(sweep, releaseStep, changed, released, ThrowIfCancelled);
        dischargeTicks += Stopwatch.GetTimestamp() - dischargeStart;

        var changedTiles = new List<TileCoord>(changed);
        changedTiles.Sort();
        lastAddTicks = Stopwatch.GetTimestamp() - start;
        return new TileUpdate(changedTiles, released);
    }

    /// <summary>The water of a held tile.</summary>
    /// <param name="tile">The tile.</param>
    /// <param name="water">Its arrays (live views: they change as the world grows, except where final).</param>
    /// <returns>Whether the tile is held.</returns>
    public bool TryGetTile(TileCoord tile, out TileWater water)
    {
        TileState? state = store.Get(tile);
        water = state is null ? default : new TileWater(state);
        return state is not null;
    }

    /// <summary>
    /// The ungenerated tiles that the not yet final water within <paramref name="radius"/> tiles of a point is
    /// waiting on (for a generator to make first). Cached until the next change.
    /// </summary>
    /// <param name="focusRow">The point's row, in tiles (fractional).</param>
    /// <param name="focusColumn">The point's column, in tiles (fractional).</param>
    /// <param name="radius">Radius in tiles.</param>
    /// <returns>The tiles, unordered.</returns>
    public IReadOnlyCollection<TileCoord> GetWantedTiles(double focusRow, double focusColumn, double radius)
    {
        var key = new WantedQuery(Step, revision, focusRow, focusColumn, radius);
        if (wantedKey != key)
        {
            wantedTiles = demand.Find(focusRow, focusColumn, radius);
            wantedKey = key;
        }
        return wantedTiles;
    }

    /// <summary>
    /// Forgets a tile (to bound memory far from the player). Its neighbours then treat it as not generated:
    /// water that came from it is unchanged where final, and adding it again later is allowed.
    /// </summary>
    /// <param name="tile">The tile.</param>
    /// <returns>Whether it was held.</returns>
    public bool ForgetTile(TileCoord tile)
    {
        bool removed = store.Remove(tile);
        if (removed)
        {
            revision++;
        }
        return removed;
    }

    /// <summary>A snapshot of the counters and timings.</summary>
    /// <returns>The statistics.</returns>
    public RiverGraphStatistics GetStatistics()
    {
        int filledCount = 0, routedCount = 0, wideCount = 0, widenedCount = 0;
        long bytes = 0;
        foreach (TileState state in store.All)
        {
            filledCount += state.IsFilled ? 1 : 0;
            routedCount += state.IsRouted ? 1 : 0;
            wideCount += state.IsWide ? 1 : 0;
            widenedCount += state.FillRadius > 1 ? 1 : 0;
            bytes += state.ByteSize;
        }
        return new RiverGraphStatistics
        {
            Step = Step,
            TilesKnown = store.Count,
            TilesFilled = filledCount,
            TilesRouted = routedCount,
            TilesWide = wideCount,
            UncertainCells = uncertainCells,
            TilesFillWidened = widenedCount,
            TerminalFlats = terminalFlats,
            EscapedCells = escapedCells,
            Sweeps = discharge.Sweeps,
            Violations = discharge.Violations,
            MeanSinuosity = sinuosityCells > 0 ? sinuositySum / sinuosityCells : null,
            FillMilliseconds = Milliseconds(fillTicks),
            RouteMilliseconds = Milliseconds(routeTicks),
            MeanderMilliseconds = Milliseconds(meanderTicks),
            DischargeMilliseconds = Milliseconds(dischargeTicks),
            LastAddMilliseconds = Milliseconds(lastAddTicks),
            Bytes = bytes,
        };
    }

    /// <summary>Stage 1 for the tiles around new arrivals that they made ready: the tiles within their fill
    /// radius have all arrived. Returns the tiles that got their fill (a fill may instead grow and wait).</summary>
    private List<TileState> FillReadyTilesAround(IEnumerable<TileCoord> arrivals, HashSet<TileCoord> changed)
    {
        var candidates = new SortedSet<TileCoord>();
        int reach = Settings.MaxFillRadius;
        foreach (TileCoord tile in arrivals)
        {
            for (int dr = -reach; dr <= reach; dr++)
            {
                for (int dc = -reach; dc <= reach; dc++)
                {
                    candidates.Add(tile.Offset(dr, dc));
                }
            }
        }
        var ready = new List<TileState>();
        foreach (TileCoord around in candidates)
        {
            if (fill.IsReady(around))
            {
                ready.Add(store.Get(around)!);
            }
        }
        long start = Stopwatch.GetTimestamp();
        FillOutcome[] outcomes = RunEach(ready, fill.Fill);
        fillTicks += Stopwatch.GetTimestamp() - start;
        var filled = new List<TileState>(ready.Count);
        for (int i = 0; i < ready.Count; i++)
        {
            if (!outcomes[i].Filled)
            {
                continue;
            }
            ready[i].IsFilled = true;
            changed.Add(ready[i].Tile);
            uncertainCells += outcomes[i].UncertainCells;
            filled.Add(ready[i]);
        }
        return filled;
    }

    /// <summary>Stage 2 for the unrouted tiles within two tiles of the newly filled ones whose fill is now all
    /// there. Returns the tiles routed, in row-major order.</summary>
    private List<TileState> RouteReadyTilesAround(List<TileState> filled)
    {
        var candidates = new SortedSet<TileCoord>();
        foreach (TileState state in filled)
        {
            for (int dr = -2; dr <= 2; dr++)
            {
                for (int dc = -2; dc <= 2; dc++)
                {
                    candidates.Add(state.Tile.Offset(dr, dc));
                }
            }
        }
        var toRoute = new List<TileState>();
        foreach (TileCoord candidate in candidates)
        {
            TileState? state = store.Get(candidate);
            if (state is { IsFilled: true, IsRouted: false })
            {
                toRoute.Add(state);
            }
        }

        long start = Stopwatch.GetTimestamp();
        RouteOutcome[] outcomes = RunEach(toRoute, routing.TryRoute);
        routeTicks += Stopwatch.GetTimestamp() - start;
        var routed = new List<TileState>();
        for (int i = 0; i < toRoute.Count; i++)
        {
            RouteOutcome outcome = outcomes[i];
            if (!outcome.Routed)
            {
                continue;
            }
            routed.Add(toRoute[i]);
            terminalFlats += outcome.TerminalFlats;
            escapedCells += outcome.EscapedCells;
            sinuositySum += outcome.SinuositySum;
            sinuosityCells += outcome.LandCells;
            meanderTicks += outcome.MeanderTicks;
        }
        return routed;
    }

    /// <summary>Runs <paramref name="work"/> on each item, in parallel when there are several; results in item
    /// order. Exceptions surface unwrapped.</summary>
    private T[] RunEach<T>(IReadOnlyList<TileState> items, Func<TileState, T> work)
    {
        var results = new T[items.Count];
        if (items.Count <= 1 || Settings.MaxDegreeOfParallelism == 1)
        {
            for (int i = 0; i < items.Count; i++)
            {
                ThrowIfCancelled();
                results[i] = work(items[i]);
            }
            return results;
        }
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Settings.MaxDegreeOfParallelism,
            CancellationToken = cancellation.Token,
        };
        try
        {
            Parallel.For(0, items.Count, options, i => results[i] = work(items[i]));
        }
        catch (AggregateException error) when (error.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
        }
        return results;
    }

    /// <summary>Throws <see cref="OperationCanceledException"/> once <see cref="Cancel"/> was called.</summary>
    private void ThrowIfCancelled() => cancellation.Token.ThrowIfCancellationRequested();

    /// <summary>Stopwatch ticks in milliseconds.</summary>
    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>A wanted-tiles query: the world's state and the point asked about.</summary>
    /// <param name="Step">Arrivals so far.</param>
    /// <param name="Revision">Forget count.</param>
    /// <param name="Row">Point row, tiles.</param>
    /// <param name="Column">Point column, tiles.</param>
    /// <param name="Radius">Radius, tiles.</param>
    private readonly record struct WantedQuery(int Step, int Revision, double Row, double Column, double Radius);
}

/// <summary>A read-only view of one tile's terrain and water (row-major arrays, one value per cell).</summary>
public readonly struct TileWater
{
    /// <summary>The tile viewed.</summary>
    private readonly TileState state;

    /// <summary>A view of <paramref name="state"/>.</summary>
    internal TileWater(TileState state)
    {
        this.state = state;
    }

    /// <summary>The tile.</summary>
    public TileCoord Tile => state.Tile;

    /// <summary>Index of the arrival that brought the tile.</summary>
    public int ArrivalStep => state.ArrivalStep;

    /// <summary>Terrain height, metres.</summary>
    public ReadOnlySpan<float> Elevation => state.Elevation;

    /// <summary>Water surface, metres: the elevation until the tile is filled, then the lake level where there
    /// is a lake (water depth is <c>Level − Elevation</c>).</summary>
    public ReadOnlySpan<float> Level => state.Level;

    /// <summary>River discharge: cells upstream (the cell included), capped at
    /// <see cref="RiverGraphSettings.DischargeCap"/>; 0 until the tile's first sweep.</summary>
    public ReadOnlySpan<float> Discharge => state.Discharge;

    /// <summary>Whether each cell's water can no longer change.</summary>
    public ReadOnlySpan<bool> IsFinal => state.IsFinal;

    /// <summary>Per chunk of the tile (row-major), the step it was released at, or −1.</summary>
    public ReadOnlySpan<int> ChunkReleaseSteps => state.ChunkReleaseStep;

    /// <summary>Whether every chunk of the tile is released.</summary>
    public bool IsFullyReleased => state.IsFullyReleased;

    /// <summary>Whether the tile has its fill.</summary>
    public bool IsFilled => state.IsFilled;

    /// <summary>Whether the tile has its directions.</summary>
    public bool IsRouted => state.IsRouted;

    /// <summary>Direction codes (<see cref="D8"/>), or empty before the tile is routed.</summary>
    public ReadOnlySpan<sbyte> Directions => state.Network is { } network ? network.Directions : ReadOnlySpan<sbyte>.Empty;
}
