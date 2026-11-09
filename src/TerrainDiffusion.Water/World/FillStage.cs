using System;
using TerrainDiffusion.Water.Lakes;

namespace TerrainDiffusion.Water.World;

/// <summary>
/// Stage 1 of a tile: its water level, from the fill of a window of the tile and its neighbours (see
/// <see cref="DepressionFill"/>).
/// </summary>
/// <remarks>
/// <para>The window is the tile plus <c>FillMargin</c> cells of its 8 neighbours. A level decided only by the
/// window edge is uncertain: a lake may run on beyond the window, and a neighbour (whose window ends elsewhere)
/// would give the same lake another level, a hard lake edge at the tile seam (on the side that drained it early,
/// the cells come out dry). Every other level is the one a fill of the whole world would give, whatever the
/// window (see <see cref="DepressionFill"/>). So a tile with any uncertain cell of its own is filled again with
/// one more ring of tiles (<see cref="TileState.FillRadius"/>), up to
/// <see cref="RiverGraphSettings.MaxFillRadius"/>, waiting for those tiles to arrive if need be; neighbours then
/// agree wherever neither is left uncertain. Whether a tile grows depends only on the terrain, never on the order
/// tiles arrive in.</para>
/// <para>Thread safe: several tiles may be filled at once (each writes only its own levels and radius).</para>
/// </remarks>
internal sealed class FillStage
{
    /// <summary>The tiles.</summary>
    private readonly TileStore store;
    /// <summary>Cells of the neighbours the fill sees at radius 1.</summary>
    private readonly int margin;
    /// <summary>Size at which a closed basin stays a terminal lake.</summary>
    private readonly int lakeCells;
    /// <summary>Most rings of tiles a fill may grow to.</summary>
    private readonly int maxRadius;
    /// <summary>Scratch per concurrent fill.</summary>
    private readonly ObjectPool<Workspace> workspaces;
    /// <summary>Cells of a window at radius 1: scratch for a wider window is freed after use.</summary>
    private readonly int baseWindowCells;

    /// <summary>A fill stage reading from <paramref name="store"/>.</summary>
    /// <param name="store">The tiles.</param>
    /// <param name="margin">Cells of the neighbours the fill sees at radius 1.</param>
    /// <param name="lakeCells">Size at which a closed basin stays a terminal lake.</param>
    /// <param name="maxRadius">Most rings of tiles a fill may grow to.</param>
    /// <param name="maxRetained">Workspaces kept between fills (the most fills that run at once).</param>
    public FillStage(TileStore store, int margin, int lakeCells, int maxRadius, int maxRetained)
    {
        this.store = store;
        this.margin = margin;
        this.lakeCells = lakeCells;
        this.maxRadius = maxRadius;
        workspaces = new ObjectPool<Workspace>(() => new Workspace(store.TileSize), maxRetained);
        int baseSide = store.TileSize + 2 * margin;
        baseWindowCells = baseSide * baseSide;
    }

    /// <summary>Whether <paramref name="tile"/> can be filled: it is not filled yet and the tiles within its
    /// <see cref="TileState.FillRadius"/> have all arrived.</summary>
    /// <param name="tile">The tile.</param>
    /// <returns>True if ready.</returns>
    public bool IsReady(TileCoord tile)
    {
        TileState? state = store.Get(tile);
        return state is not null && !state.IsFilled && store.AllWithin(tile, state.FillRadius, _ => true);
    }

    /// <summary>
    /// Fills a ready tile, growing its window while any of its levels is decided by the window edge: writes its
    /// levels if it got them (the caller marks it filled), else leaves it waiting for the tiles of its new
    /// <see cref="TileState.FillRadius"/>.
    /// </summary>
    /// <param name="state">The tile.</param>
    /// <returns>Whether it got its levels, and how many of them the edge of the window kept decided.</returns>
    public FillOutcome Fill(TileState state)
    {
        Workspace workspace = workspaces.Rent();
        try
        {
            int tileSize = store.TileSize;
            while (true)
            {
                int padding = margin + (state.FillRadius - 1) * tileSize;
                Span<float> window = workspace.WindowFor(tileSize + 2 * padding);
                store.CopyWindow(state.Tile, padding, s => s.Elevation, window, float.NaN);
                int uncertain = workspace.Filler.Fill(window, tileSize + 2 * padding, lakeCells, padding, tileSize,
                                                      workspace.CoreLevel);
                if (uncertain > 0 && state.FillRadius < maxRadius)
                {
                    state.FillRadius++;
                    if (store.AllWithin(state.Tile, state.FillRadius, _ => true))
                    {
                        continue;
                    }
                    return new FillOutcome(false, 0);
                }
                workspace.CoreLevel.CopyTo(state.Level, 0);
                return new FillOutcome(true, uncertain);
            }
        }
        finally
        {
            workspace.Trim(baseWindowCells);         // a widened window's scratch is several times the usual
            workspaces.Return(workspace);
        }
    }

    /// <summary>Scratch for one fill.</summary>
    private sealed class Workspace
    {
        /// <summary>The window's heights; grown to the largest window used.</summary>
        private float[] window = Array.Empty<float>();

        /// <summary>Scratch for tiles of <paramref name="tileSize"/> cells.</summary>
        public Workspace(int tileSize)
        {
            CoreLevel = new float[tileSize * tileSize];
        }

        /// <summary>The tile's levels from the last fill, before they are known to be kept.</summary>
        public float[] CoreLevel { get; }

        /// <summary>The fill, with its own buffers.</summary>
        public DepressionFill Filler { get; } = new();

        /// <summary>Frees the buffers sized for windows of more than <paramref name="maxCells"/> cells.</summary>
        /// <param name="maxCells">Largest window worth keeping buffers for.</param>
        public void Trim(int maxCells)
        {
            if (window.Length > maxCells)
            {
                window = Array.Empty<float>();
            }
            Filler.Trim(maxCells);
        }

        /// <summary>The window buffer for a window of <paramref name="side"/> cells.</summary>
        /// <param name="side">Window side in cells.</param>
        /// <returns><c>side²</c> values.</returns>
        public Span<float> WindowFor(int side)
        {
            int n = side * side;
            if (window.Length < n)
            {
                window = new float[n];
            }
            return window.AsSpan(0, n);
        }
    }
}

/// <summary>What <see cref="FillStage.Fill"/> did to a tile.</summary>
/// <param name="Filled">Whether the tile got its levels (else it waits for more tiles).</param>
/// <param name="UncertainCells">Cells of the tile whose level the edge of the window kept decided (0 when not
/// filled).</param>
internal readonly record struct FillOutcome(bool Filled, int UncertainCells);
