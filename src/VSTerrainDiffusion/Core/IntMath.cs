namespace VSTerrainDiffusion.Core;

/// <summary>Integer arithmetic that rounds towards negative infinity, for grid coordinates either side of 0.</summary>
public static class IntMath
{
    /// <summary>
    /// <paramref name="a"/> / <paramref name="b"/> rounded down, so the cell index of a negative
    /// coordinate is the cell that contains it (-1 / 32 is -1, not 0).
    /// </summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor; not 0.</param>
    /// <returns>The floor of the exact quotient.</returns>
    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }

    /// <summary>
    /// <paramref name="a"/> modulo <paramref name="b"/>, never negative for a positive divisor, so
    /// a coordinate's offset within its cell is the same either side of 0 (-1 mod 32 is 31).
    /// </summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor; positive.</param>
    /// <returns>A value in [0, <paramref name="b"/>).</returns>
    public static int FloorMod(int a, int b)
    {
        int m = a % b;
        return m < 0 ? m + b : m;
    }
}
