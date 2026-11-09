using System;

namespace TerrainDiffusion.Water.Numerics;

/// <summary>The result of <see cref="PythonMath.DivMod"/>.</summary>
/// <param name="Quotient">The floored quotient.</param>
/// <param name="Remainder">The remainder, with the sign of the divisor.</param>
internal readonly record struct FloorDivision(double Quotient, double Remainder);

/// <summary>
/// Arithmetic with the exact semantics of the Python reference implementation (CPython float division and
/// modulo, NumPy pairwise summation and <c>numpy.interp</c>), so that the port reproduces it value for value.
/// </summary>
internal static class PythonMath
{
    /// <summary>Block size below which NumPy's pairwise summation adds sequentially (<c>PW_BLOCKSIZE</c>).</summary>
    private const int PairwiseBlockSize = 128;

    /// <summary>
    /// Python's <c>divmod(a, b)</c> for floats: the floored quotient, and the remainder with the sign of
    /// <paramref name="b"/>.
    /// </summary>
    /// <param name="a">Dividend.</param>
    /// <param name="b">Divisor (non-zero).</param>
    /// <returns>(floor(a / b), a mod b) exactly as CPython computes them.</returns>
    public static FloorDivision DivMod(double a, double b)
    {
        double remainder = a % b;                 // C fmod: exact, with the sign of a
        double quotient = (a - remainder) / b;
        if (remainder != 0.0)
        {
            if ((b < 0.0) != (remainder < 0.0))
            {
                remainder += b;
                quotient -= 1.0;
            }
        }
        else
        {
            remainder = Math.CopySign(0.0, b);
        }

        double floored;
        if (quotient != 0.0)
        {
            floored = Math.Floor(quotient);
            if (quotient - floored > 0.5)
            {
                floored += 1.0;
            }
        }
        else
        {
            floored = Math.CopySign(0.0, a / b);
        }
        return new FloorDivision(floored, remainder);
    }

    /// <summary>Python's float <c>a % b</c> (the remainder of <see cref="DivMod"/>).</summary>
    /// <param name="a">Dividend.</param>
    /// <param name="b">Divisor (non-zero).</param>
    /// <returns>The remainder, with the sign of <paramref name="b"/>.</returns>
    public static double Mod(double a, double b) => DivMod(a, b).Remainder;

    /// <summary>Python's float <c>a // b</c> (the quotient of <see cref="DivMod"/>).</summary>
    /// <param name="a">Dividend.</param>
    /// <param name="b">Divisor (non-zero).</param>
    /// <returns>The floored quotient.</returns>
    public static double FloorDivide(double a, double b) => DivMod(a, b).Quotient;

    /// <summary>
    /// The sum of <paramref name="values"/> in NumPy's pairwise order (<c>numpy.sum</c> of a contiguous float64
    /// array), which differs from a plain left-to-right sum in the last bits.
    /// </summary>
    /// <param name="values">Values to add.</param>
    /// <returns>Their sum.</returns>
    public static double PairwiseSum(ReadOnlySpan<double> values)
    {
        int n = values.Length;
        if (n < 8)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                sum += values[i];
            }
            return sum;
        }

        if (n <= PairwiseBlockSize)
        {
            Span<double> partial = stackalloc double[8];
            values[..8].CopyTo(partial);
            int i = 8;
            for (; i < n - (n % 8); i += 8)
            {
                for (int lane = 0; lane < 8; lane++)
                {
                    partial[lane] += values[i + lane];
                }
            }
            double sum = ((partial[0] + partial[1]) + (partial[2] + partial[3]))
                       + ((partial[4] + partial[5]) + (partial[6] + partial[7]));
            for (; i < n; i++)
            {
                sum += values[i];
            }
            return sum;
        }

        int half = n / 2;
        half -= half % 8;
        return PairwiseSum(values[..half]) + PairwiseSum(values[half..]);
    }

    /// <summary>
    /// <c>numpy.interp(x, xs, ys)</c> for one value: piecewise-linear interpolation through the points
    /// (<paramref name="xs"/>, <paramref name="ys"/>), clamped to the end values outside them.
    /// </summary>
    /// <param name="x">Where to interpolate.</param>
    /// <param name="xs">Increasing sample positions (at least two).</param>
    /// <param name="ys">Values at those positions.</param>
    /// <returns>The interpolated value (NaN for a NaN <paramref name="x"/>).</returns>
    public static double Interpolate(double x, ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        if (double.IsNaN(x))
        {
            return x;
        }
        int last = xs.Length - 1;
        if (x < xs[0])
        {
            return ys[0];
        }
        if (x > xs[last])
        {
            return ys[last];
        }

        // j: the last sample position <= x
        int lo = 0, hi = last;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (xs[mid] <= x)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        int j = lo;
        if (j == last || xs[j] == x)
        {
            return ys[j];
        }

        double slope = (ys[j + 1] - ys[j]) / (xs[j + 1] - xs[j]);
        double value = slope * (x - xs[j]) + ys[j];
        if (double.IsNaN(value))
        {
            value = slope * (x - xs[j + 1]) + ys[j + 1];
            if (double.IsNaN(value) && ys[j] == ys[j + 1])
            {
                value = ys[j];
            }
        }
        return value;
    }

    /// <summary>Integer floor division (rounds toward negative infinity, like Python's <c>//</c>).</summary>
    /// <param name="a">Dividend.</param>
    /// <param name="b">Positive divisor.</param>
    /// <returns>floor(a / b).</returns>
    public static long FloorDivide(long a, long b)
    {
        long quotient = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? quotient - 1 : quotient;
    }

    /// <summary>Integer modulo with the sign of the divisor (Python's <c>%</c>).</summary>
    /// <param name="a">Dividend.</param>
    /// <param name="b">Positive divisor.</param>
    /// <returns>a mod b in [0, b).</returns>
    public static long Mod(long a, long b)
    {
        long remainder = a % b;
        return remainder < 0 ? remainder + b : remainder;
    }
}
