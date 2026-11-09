using System;
using System.Collections.Generic;

namespace TerrainDiffusion.Water.World;

/// <summary>
/// The generated tiles, by coordinate, and windows of cells read across several of them.
/// </summary>
/// <remarks>Not thread safe for writes; concurrent reads are safe while nothing is added or removed.</remarks>
internal sealed class TileStore
{
    /// <summary>The tiles held, by coordinate.</summary>
    private readonly Dictionary<TileCoord, TileState> tiles = new();

    /// <summary>A store for tiles of <paramref name="tileSize"/> cells.</summary>
    /// <param name="tileSize">Tile side in cells.</param>
    public TileStore(int tileSize)
    {
        TileSize = tileSize;
    }

    /// <summary>Tile side in cells.</summary>
    public int TileSize { get; }

    /// <summary>Number of tiles held.</summary>
    public int Count => tiles.Count;

    /// <summary>Every tile held.</summary>
    public IEnumerable<TileState> All => tiles.Values;

    /// <summary>The tile at <paramref name="tile"/>, or null if it has not arrived.</summary>
    /// <param name="tile">The tile.</param>
    /// <returns>Its state, or null.</returns>
    public TileState? Get(TileCoord tile) => tiles.TryGetValue(tile, out TileState? state) ? state : null;

    /// <summary>Whether <paramref name="tile"/> has arrived.</summary>
    /// <param name="tile">The tile.</param>
    /// <returns>True if held.</returns>
    public bool Contains(TileCoord tile) => tiles.ContainsKey(tile);

    /// <summary>Adds a newly arrived tile.</summary>
    /// <param name="state">The tile.</param>
    public void Add(TileState state) => tiles.Add(state.Tile, state);

    /// <summary>Forgets a tile.</summary>
    /// <param name="tile">The tile.</param>
    /// <returns>Whether it was held.</returns>
    public bool Remove(TileCoord tile) => tiles.Remove(tile);

    /// <summary>Whether every tile within <paramref name="radius"/> tiles of <paramref name="centre"/> (a
    /// <c>(2 · radius + 1)²</c> block) is held and satisfies <paramref name="condition"/>.</summary>
    /// <param name="centre">Centre tile.</param>
    /// <param name="radius">Radius in tiles.</param>
    /// <param name="condition">Test for each tile.</param>
    /// <returns>True if all are there and pass.</returns>
    public bool AllWithin(TileCoord centre, int radius, Func<TileState, bool> condition)
    {
        for (int dr = -radius; dr <= radius; dr++)
        {
            for (int dc = -radius; dc <= radius; dc++)
            {
                TileState? state = Get(centre.Offset(dr, dc));
                if (state is null || !condition(state))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Copies a square window of cells around a tile: the tile plus <paramref name="padding"/> cells on each
    /// side, read from each overlapping tile's array chosen by <paramref name="select"/>, and
    /// <paramref name="missing"/> where a tile is absent or <paramref name="select"/> gives null.
    /// </summary>
    /// <param name="centre">The tile at the window's centre.</param>
    /// <param name="padding">Cells around the tile.</param>
    /// <param name="select">The per-tile array to read (row-major, one value per cell), or null.</param>
    /// <param name="window">Receives <c>(TileSize + 2 · padding)²</c> values, row-major.</param>
    /// <param name="missing">Value for cells with no source.</param>
    public void CopyWindow(TileCoord centre, int padding, Func<TileState, float[]?> select, Span<float> window,
                           float missing)
    {
        int t = TileSize, side = t + 2 * padding;
        if (window.Length != side * side)
        {
            throw new ArgumentException("The window must be (TileSize + 2 · padding)² values.", nameof(window));
        }
        long top = (long)centre.Row * t - padding, left = (long)centre.Column * t - padding;
        int rings = (padding + t - 1) / t;
        for (int dr = -rings; dr <= rings; dr++)
        {
            for (int dc = -rings; dc <= rings; dc++)
            {
                TileCoord source = centre.Offset(dr, dc);
                long sourceTop = (long)source.Row * t, sourceLeft = (long)source.Column * t;
                // Overlap of the source tile with the window, in world cells
                long rowStart = Math.Max(sourceTop, top), rowEnd = Math.Min(sourceTop + t, top + side);
                long columnStart = Math.Max(sourceLeft, left), columnEnd = Math.Min(sourceLeft + t, left + side);
                if (rowStart >= rowEnd || columnStart >= columnEnd)
                {
                    continue;
                }
                TileState? state = Get(source);
                float[]? values = state is null ? null : select(state);
                int width = (int)(columnEnd - columnStart);
                for (long row = rowStart; row < rowEnd; row++)
                {
                    Span<float> target = window.Slice((int)((row - top) * side + (columnStart - left)), width);
                    if (values is null)
                    {
                        target.Fill(missing);
                    }
                    else
                    {
                        values.AsSpan((int)((row - sourceTop) * t + (columnStart - sourceLeft)), width).CopyTo(target);
                    }
                }
            }
        }
    }
}
