using System;

namespace TerrainDiffusion.Water;

/// <summary>
/// A terrain tile of the world grid: tile (Row, Column) covers world cells
/// <c>[Row · T, (Row + 1) · T) × [Column · T, (Column + 1) · T)</c> for tile size T. Rows run along world Z,
/// columns along world X; both may be negative.
/// </summary>
/// <param name="Row">Tile row (along Z).</param>
/// <param name="Column">Tile column (along X).</param>
public readonly record struct TileCoord(int Row, int Column) : IComparable<TileCoord>
{
    /// <summary>The tile <paramref name="rowOffset"/> rows and <paramref name="columnOffset"/> columns
    /// away.</summary>
    /// <param name="rowOffset">Rows to move.</param>
    /// <param name="columnOffset">Columns to move.</param>
    /// <returns>That tile.</returns>
    public TileCoord Offset(int rowOffset, int columnOffset) => new(Row + rowOffset, Column + columnOffset);

    /// <summary>Row-major order: by row, then by column.</summary>
    /// <param name="other">The tile to compare with.</param>
    /// <returns>Negative, zero or positive.</returns>
    public int CompareTo(TileCoord other) => Row != other.Row ? Row.CompareTo(other.Row) : Column.CompareTo(other.Column);

    /// <inheritdoc/>
    public override int GetHashCode() => unchecked((Row * 73856093) ^ (Column * 19349663));

    /// <inheritdoc/>
    public override string ToString() => $"({Row}, {Column})";
}

/// <summary>
/// A chunk of the world grid: tiles are split into <see cref="RiverGraphSettings.ChunksPerTileSide"/> chunks per
/// side, so chunk (Row, Column) is chunk (Row mod C, Column mod C) of tile (Row div C, Column div C).
/// </summary>
/// <param name="Row">Chunk row (along Z).</param>
/// <param name="Column">Chunk column (along X).</param>
public readonly record struct ChunkCoord(int Row, int Column)
{
    /// <inheritdoc/>
    public override string ToString() => $"({Row}, {Column})";
}

/// <summary>A chunk whose water became final, and the step at which it did.</summary>
/// <param name="Chunk">The chunk.</param>
/// <param name="Step">The <see cref="RiverGraph.Step"/> of the tile arrival that made it final, minus one (the
/// index of that arrival).</param>
public readonly record struct ReleasedChunk(ChunkCoord Chunk, int Step);
