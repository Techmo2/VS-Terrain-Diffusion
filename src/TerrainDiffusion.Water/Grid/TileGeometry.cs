using System;
using System.Collections.Generic;

namespace TerrainDiffusion.Water.Grid;

/// <summary>
/// Index tables shared by every tile of one size: the tile's perimeter, the cells beside each neighbour, and the
/// chunk layout. Built once per <see cref="RiverGraph"/>.
/// </summary>
/// <remarks>
/// A tile's cells are indexed row-major, <c>row · Size + column</c>. Neighbour slots follow the D8 code order
/// (see <see cref="D8"/>): slot 0 is the tile up and to the left (row − 1, column − 1), slot 7 down and to the
/// right.
/// </remarks>
internal sealed class TileGeometry
{
    /// <summary>Per neighbour slot (D8 order), the cells of the tile beside that neighbour.</summary>
    private readonly int[][] edgeCellsBySlot;

    /// <summary>Builds the tables for tiles of <paramref name="size"/> cells split into
    /// <paramref name="chunksPerSide"/> chunks per side.</summary>
    /// <param name="size">Tile side in cells.</param>
    /// <param name="chunksPerSide">Chunks per tile side; must divide <paramref name="size"/>.</param>
    public TileGeometry(int size, int chunksPerSide)
    {
        Size = size;
        CellCount = size * size;
        ChunksPerSide = chunksPerSide;
        ChunkSize = size / chunksPerSide;
        Perimeter = BuildPerimeter(size);
        edgeCellsBySlot = new int[D8.NeighbourCount][];
        for (int slot = 0; slot < D8.NeighbourCount; slot++)
        {
            edgeCellsBySlot[slot] = BuildEdgeCells(size, D8.RowStep[slot], D8.ColumnStep[slot]);
        }
    }

    /// <summary>Tile side in cells.</summary>
    public int Size { get; }

    /// <summary>Cells per tile (<c>Size²</c>).</summary>
    public int CellCount { get; }

    /// <summary>Chunks per tile side.</summary>
    public int ChunksPerSide { get; }

    /// <summary>Chunk side in cells.</summary>
    public int ChunkSize { get; }

    /// <summary>The cells on the tile's border, in row-major order.</summary>
    public int[] Perimeter { get; }

    /// <summary>The cells of this tile that touch the neighbour in <paramref name="slot"/>, row-major: a whole
    /// row or column for an edge neighbour, one corner cell for a diagonal one.</summary>
    /// <param name="slot">Neighbour slot 0-7 (D8 order).</param>
    /// <returns>Cell indices within the tile.</returns>
    public ReadOnlySpan<int> EdgeCellsFacing(int slot) => edgeCellsBySlot[slot];

    /// <summary>The border cells of a tile, row-major.</summary>
    private static int[] BuildPerimeter(int size)
    {
        var cells = new List<int>(4 * size);
        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
            {
                if (row == 0 || column == 0 || row == size - 1 || column == size - 1)
                {
                    cells.Add(row * size + column);
                }
            }
        }
        return cells.ToArray();
    }

    /// <summary>The cells of a tile beside the neighbour one (<paramref name="rowStep"/>, <paramref name="columnStep"/>) away.</summary>
    private static int[] BuildEdgeCells(int size, int rowStep, int columnStep)
    {
        (int rowStart, int rowEnd) = rowStep switch { -1 => (0, 1), 1 => (size - 1, size), _ => (0, size) };
        (int columnStart, int columnEnd) = columnStep switch { -1 => (0, 1), 1 => (size - 1, size), _ => (0, size) };
        var cells = new List<int>(size);
        for (int row = rowStart; row < rowEnd; row++)
        {
            for (int column = columnStart; column < columnEnd; column++)
            {
                cells.Add(row * size + column);
            }
        }
        return cells.ToArray();
    }
}
