namespace TerrainDiffusion.Water.Grid;

/// <summary>
/// A rectangle of cells of a row-major grid: rows <c>[RowStart, RowEnd)</c> by columns
/// <c>[ColumnStart, ColumnEnd)</c>.
/// </summary>
/// <param name="RowStart">First row in the region.</param>
/// <param name="RowEnd">One past the last row.</param>
/// <param name="ColumnStart">First column in the region.</param>
/// <param name="ColumnEnd">One past the last column.</param>
internal readonly record struct GridRegion(int RowStart, int RowEnd, int ColumnStart, int ColumnEnd)
{
    /// <summary>Number of rows in the region.</summary>
    public int Height => RowEnd - RowStart;

    /// <summary>Number of columns in the region.</summary>
    public int Width => ColumnEnd - ColumnStart;

    /// <summary>A square region <c>[start, start + size)</c> on both axes.</summary>
    /// <param name="start">First row and column.</param>
    /// <param name="size">Side length in cells.</param>
    /// <returns>The region.</returns>
    public static GridRegion Square(int start, int size) => new(start, start + size, start, start + size);
}
