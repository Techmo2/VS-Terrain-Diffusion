using System;
using System.Numerics;
using System.Runtime.InteropServices;
using TerrainDiffusion.Water.Grid;

namespace TerrainDiffusion.Water.Numerics;

/// <summary>
/// Separable Gaussian smoothing of a row-major grid with the semantics of
/// <c>scipy.ndimage.gaussian_filter(input, sigma, truncate=…)</c> (mode <c>'reflect'</c>), computed only
/// where the caller needs it.
/// </summary>
/// <remarks>
/// <para>The kernel has radius <c>int(truncate · sigma + 0.5)</c> and weights
/// <c>exp(−0.5 / sigma² · x²)</c> normalised by their NumPy pairwise sum. The grid is filtered along rows
/// (axis 0) first, then along columns (axis 1), each output as <c>centre · w₀ + Σ (left + right) · wⱼ</c> for
/// j from the radius down to 1: the order SciPy uses for a symmetric kernel, so results agree to the last bit
/// up to the platform's <c>exp</c>.</para>
/// <para>Outside the grid, values reflect about its edge (<c>d c b a | a b c d | d c b a</c>).</para>
/// <para>Interior runs are vectorised with <see cref="Vector{T}"/>; IEEE addition and multiplication are done
/// element by element in the same order as the scalar code, so vectorisation does not change any result.</para>
/// </remarks>
internal sealed class GaussianFilter
{
    /// <summary>Kernel weights by distance from the centre, 0 to <see cref="Radius"/>.</summary>
    private readonly double[] weights;
    /// <summary>The same weights broadcast to every vector lane.</summary>
    private readonly Vector<double>[] weightVectors;

    /// <summary>Builds the kernel for <paramref name="sigma"/> cells, cut off at
    /// <paramref name="truncate"/> standard deviations.</summary>
    /// <param name="sigma">Standard deviation in cells (positive).</param>
    /// <param name="truncate">Cut-off in standard deviations.</param>
    public GaussianFilter(double sigma, double truncate)
    {
        if (!(sigma > 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "Sigma must be positive.");
        }
        Sigma = sigma;
        Radius = (int)(truncate * sigma + 0.5);
        int taps = 2 * Radius + 1;
        double factor = -0.5 / (sigma * sigma);
        var phi = new double[taps];
        for (int i = 0; i < taps; i++)
        {
            long x = i - Radius;
            phi[i] = Math.Exp(factor * (double)(x * x));
        }
        double total = PythonMath.PairwiseSum(phi);
        weights = new double[Radius + 1];
        weightVectors = new Vector<double>[Radius + 1];
        for (int j = 0; j <= Radius; j++)
        {
            weights[j] = phi[Radius + j] / total;
            weightVectors[j] = new Vector<double>(weights[j]);
        }
    }

    /// <summary>Standard deviation in cells.</summary>
    public double Sigma { get; }

    /// <summary>Kernel radius in cells.</summary>
    public int Radius { get; }

    /// <summary>
    /// The region of the input that filtering <paramref name="output"/> reads: the output grown by the radius on
    /// an axis where that stays inside the grid, else the whole axis (reflection can then reach anywhere).
    /// </summary>
    /// <param name="output">Cells to compute.</param>
    /// <param name="rows">Grid height.</param>
    /// <param name="columns">Grid width.</param>
    /// <returns>The cells of the input that must hold valid values.</returns>
    public GridRegion InputRegion(GridRegion output, int rows, int columns)
    {
        int r = Radius;
        bool rowsFit = output.RowStart - r >= 0 && output.RowEnd + r <= rows;
        bool columnsFit = output.ColumnStart - r >= 0 && output.ColumnEnd + r <= columns;
        return new GridRegion(
            rowsFit ? output.RowStart - r : 0, rowsFit ? output.RowEnd + r : rows,
            columnsFit ? output.ColumnStart - r : 0, columnsFit ? output.ColumnEnd + r : columns);
    }

    /// <summary>
    /// Filters <paramref name="source"/> into <paramref name="destination"/> at the cells of
    /// <paramref name="output"/>; other cells of the destination are left as they are.
    /// </summary>
    /// <param name="source">Input grid, row-major; must be valid on <see cref="InputRegion"/> of the output.</param>
    /// <param name="rows">Grid height.</param>
    /// <param name="columns">Grid width.</param>
    /// <param name="output">Cells to compute.</param>
    /// <param name="destination">Output grid, the same shape as the source.</param>
    /// <param name="scratch">Scratch grid, the same shape as the source (overwritten).</param>
    public void Apply(ReadOnlySpan<double> source, int rows, int columns, GridRegion output,
                      Span<double> destination, Span<double> scratch)
    {
        int cells = rows * columns;
        if (source.Length < cells || destination.Length < cells || scratch.Length < cells)
        {
            throw new ArgumentException("Every grid must hold rows · columns values.");
        }
        GridRegion input = InputRegion(output, rows, columns);
        FilterAlongRows(source, rows, columns, output.RowStart, output.RowEnd,
                        input.ColumnStart, input.ColumnEnd, scratch);
        FilterAlongColumns(scratch, columns, output, destination);
    }

    /// <summary>Index of <paramref name="index"/> reflected into <c>[0, length)</c> (SciPy's
    /// <c>'reflect'</c> mode, which repeats the edge value).</summary>
    /// <param name="index">Any index.</param>
    /// <param name="length">Axis length (positive).</param>
    /// <returns>An index inside the axis.</returns>
    public static int Reflect(int index, int length)
    {
        if (length == 1)
        {
            return 0;
        }
        int period = 2 * length;
        index %= period;
        if (index < 0)
        {
            index += period;
        }
        return index < length ? index : period - 1 - index;
    }

    /// <summary>Axis-0 pass: each output row from the rows above and below it, vectorised along the row.</summary>
    private void FilterAlongRows(ReadOnlySpan<double> source, int rows, int columns, int rowStart, int rowEnd,
                                 int columnStart, int columnEnd, Span<double> destination)
    {
        int width = columnEnd - columnStart, r = Radius;
        Span<int> above = stackalloc int[r + 1];      // above[j], below[j]: start of row (row ∓ j) in the source
        Span<int> below = stackalloc int[r + 1];
        for (int row = rowStart; row < rowEnd; row++)
        {
            for (int j = 1; j <= r; j++)
            {
                above[j] = Reflect(row - j, rows) * columns + columnStart;
                below[j] = Reflect(row + j, rows) * columns + columnStart;
            }
            CorrelateRun(source, row * columns + columnStart, above, below, width,
                         destination.Slice(row * columns + columnStart, width));
        }
    }

    /// <summary>Axis-1 pass: each output cell from the cells left and right of it in its row.</summary>
    private void FilterAlongColumns(ReadOnlySpan<double> source, int columns, GridRegion output,
                                    Span<double> destination)
    {
        int r = Radius;
        // Columns whose whole kernel lies inside the row run vectorised; the rest reflect, one by one.
        int interiorStart = Math.Max(output.ColumnStart, r);
        int interiorEnd = Math.Min(output.ColumnEnd, columns - r);
        Span<int> left = stackalloc int[r + 1];
        Span<int> right = stackalloc int[r + 1];
        for (int row = output.RowStart; row < output.RowEnd; row++)
        {
            int rowOffset = row * columns;
            ReadOnlySpan<double> line = source.Slice(rowOffset, columns);
            Span<double> target = destination.Slice(rowOffset, columns);
            for (int column = output.ColumnStart; column < output.ColumnEnd; column++)
            {
                if (column >= interiorStart && column < interiorEnd)
                {
                    column = interiorEnd - 1;     // the interior is done below
                    continue;
                }
                double sum = line[column] * weights[0];
                for (int j = r; j >= 1; j--)
                {
                    sum += (line[Reflect(column - j, columns)] + line[Reflect(column + j, columns)]) * weights[j];
                }
                target[column] = sum;
            }
            if (interiorEnd > interiorStart)
            {
                for (int j = 1; j <= r; j++)
                {
                    left[j] = rowOffset + interiorStart - j;
                    right[j] = rowOffset + interiorStart + j;
                }
                int width = interiorEnd - interiorStart;
                CorrelateRun(source, rowOffset + interiorStart, left, right, width, target.Slice(interiorStart, width));
            }
        }
    }

    /// <summary>
    /// <c>target[i] = source[centre + i] · w₀ + Σ_{j = r..1} (source[first[j] + i] + source[second[j] + i]) · wⱼ</c>
    /// for i in <c>[0, width)</c>: one run of outputs whose taps are all at fixed offsets.
    /// </summary>
    /// <remarks>Each vector of outputs is accumulated in a register over the whole kernel and stored once. Every
    /// offset is checked against the source once, before the loops, so the loads inside need no checks.</remarks>
    private void CorrelateRun(ReadOnlySpan<double> source, int centre, ReadOnlySpan<int> first, ReadOnlySpan<int> second,
                              int width, Span<double> target)
    {
        int r = Radius;
        if (target.Length < width || centre < 0 || centre + width > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The run lies outside the grid.");
        }
        for (int j = 1; j <= r; j++)
        {
            if (first[j] < 0 || first[j] + width > source.Length || second[j] < 0 || second[j] + width > source.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(first), "A tap lies outside the grid.");
            }
        }
        ref readonly double origin = ref MemoryMarshal.GetReference(source);
        ref double output = ref MemoryMarshal.GetReference(target);
        ReadOnlySpan<double> w = weights;
        ReadOnlySpan<Vector<double>> wv = weightVectors;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int lanes = Vector<double>.Count;
            for (; i <= width - 2 * lanes; i += 2 * lanes)            // two vectors at a time
            {
                nuint a = (nuint)i, b = (nuint)(i + lanes);
                Vector<double> sumA = Vector.LoadUnsafe(in origin, (nuint)centre + a) * wv[0];
                Vector<double> sumB = Vector.LoadUnsafe(in origin, (nuint)centre + b) * wv[0];
                for (int j = r; j >= 1; j--)
                {
                    nuint f = (nuint)first[j], g = (nuint)second[j];
                    sumA += (Vector.LoadUnsafe(in origin, f + a) + Vector.LoadUnsafe(in origin, g + a)) * wv[j];
                    sumB += (Vector.LoadUnsafe(in origin, f + b) + Vector.LoadUnsafe(in origin, g + b)) * wv[j];
                }
                Vector.StoreUnsafe(sumA, ref output, a);
                Vector.StoreUnsafe(sumB, ref output, b);
            }
            for (; i <= width - lanes; i += lanes)
            {
                Vector<double> sum = Vector.LoadUnsafe(in origin, (nuint)(centre + i)) * wv[0];
                for (int j = r; j >= 1; j--)
                {
                    sum += (Vector.LoadUnsafe(in origin, (nuint)(first[j] + i)) + Vector.LoadUnsafe(in origin, (nuint)(second[j] + i)))
                           * wv[j];
                }
                Vector.StoreUnsafe(sum, ref output, (nuint)i);
            }
        }
        for (; i < width; i++)
        {
            double sum = source[centre + i] * w[0];
            for (int j = r; j >= 1; j--)
            {
                sum += (source[first[j] + i] + source[second[j] + i]) * w[j];
            }
            target[i] = sum;
        }
    }
}
