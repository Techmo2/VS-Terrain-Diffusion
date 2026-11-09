namespace TerrainDiffusion.Water.Meander;

/// <summary>
/// A uniform random number per world cell, from its coordinates and the seed alone, so every tile that looks
/// at a cell sees the same number. It dithers each cell's choice between the two neighbours either side of its
/// heading.
/// </summary>
public static class CellHash
{
    /// <summary>A number in [0, 1) for world cell (<paramref name="row"/>, <paramref name="column"/>).</summary>
    /// <param name="row">World row (Z).</param>
    /// <param name="column">World column (X).</param>
    /// <param name="seed">World seed.</param>
    /// <returns>The cell's number, with 53 bits of resolution.</returns>
    public static double UnitInterval(long row, long column, uint seed)
    {
        unchecked
        {
            ulong h = ((ulong)row * 0xD1B5_4A32_D192_ED03UL) ^ ((ulong)column * 0xABC9_8388_FB8F_AC03UL);
            h ^= seed * 0x9E37_79B9_7F4A_7C15UL;
            h ^= h >> 31;
            h *= 0x7FB5_D329_728E_A185UL;
            h ^= h >> 27;
            h *= 0x81DA_DEF4_BC2D_D44DUL;
            h ^= h >> 33;
            return (h >> 11) / (double)(1UL << 53);
        }
    }
}
