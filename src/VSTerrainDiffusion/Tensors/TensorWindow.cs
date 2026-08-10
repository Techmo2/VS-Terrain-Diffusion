using System;

namespace VSTerrainDiffusion.Tensors;

/// <summary>
/// Defines the sliding window layout for an <see cref="InfiniteTensor"/>.
///
/// For window index w[], the covered pixel range in dimension d is
/// [w[d] * stride[d] + offset[d], w[d] * stride[d] + offset[d] + size[d]).
/// Windows may overlap (stride &lt; size) or leave gaps (stride &gt; size);
/// overlapping windows are summed during slice accumulation.
/// </summary>
public sealed class TensorWindow
{
    public readonly int[] Size;
    public readonly int[] Stride;
    public readonly int[] Offset;

    public TensorWindow(int[] size, int[] stride, int[] offset)
    {
        Size = (int[])size.Clone();
        Stride = (int[])stride.Clone();
        Offset = (int[])offset.Clone();
    }

    /// <summary>Non-overlapping windows starting at zero.</summary>
    public TensorWindow(int[] size)
    {
        Size = (int[])size.Clone();
        Stride = (int[])size.Clone();
        Offset = new int[size.Length];
    }

    /// <summary>Overlapping windows with the given stride, starting at zero.</summary>
    public TensorWindow(int[] size, int[] stride)
    {
        Size = (int[])size.Clone();
        Stride = (int[])stride.Clone();
        Offset = new int[size.Length];
    }

    public int Ndim => Size.Length;

    /// <summary>Pixel-space bounds [start, stop) for the given window index; result[d] = {start, stop}.</summary>
    public int[][] GetBounds(int[] windowIndex)
    {
        int n = Size.Length;
        var bounds = new int[n][];
        for (int i = 0; i < n; i++)
        {
            int start = windowIndex[i] * Stride[i] + Offset[i];
            bounds[i] = new[] { start, start + Size[i] };
        }
        return bounds;
    }

    /// <summary>Lowest window index per dimension whose bounds overlap the pixel range.</summary>
    public int[] GetLowestIntersection(int[][] pixelRange)
    {
        int n = Size.Length;
        var result = new int[n];
        for (int i = 0; i < n; i++)
        {
            int p = pixelRange[i][0];
            int numerator = p - Offset[i] - Size[i] + 1;
            // ceiling division that also handles negative numerators
            result[i] = numerator >= 0
                ? (numerator + Stride[i] - 1) / Stride[i]
                : -((-numerator) / Stride[i]);
        }
        return result;
    }

    /// <summary>Highest window index per dimension whose bounds overlap the pixel range.</summary>
    public int[] GetHighestIntersection(int[][] pixelRange)
    {
        int n = Size.Length;
        var result = new int[n];
        for (int i = 0; i < n; i++)
        {
            int p = pixelRange[i][1] - 1;
            result[i] = FloorDiv(p - Offset[i], Stride[i]);
        }
        return result;
    }

    internal static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }
}
