using System;

namespace VSTerrainDiffusion.Tensors;

/// <summary>
/// An N-dimensional float array with row-major (C-order) layout.
/// Used as the data container for <see cref="InfiniteTensor"/> computations.
/// </summary>
public sealed class FloatTensor
{
    public readonly int[] Shape;
    public readonly float[] Data;
    internal readonly int[] Strides;

    public FloatTensor(int[] shape)
    {
        Shape = (int[])shape.Clone();
        int total = 1;
        foreach (int d in shape) total *= d;
        Data = new float[total];
        Strides = ComputeStrides(shape);
    }

    public FloatTensor(int[] shape, float[] data)
    {
        Shape = (int[])shape.Clone();
        Data = data;
        Strides = ComputeStrides(shape);
    }

    private static int[] ComputeStrides(int[] shape)
    {
        int n = shape.Length;
        var s = new int[n];
        int stride = 1;
        for (int i = n - 1; i >= 0; i--)
        {
            s[i] = stride;
            stride *= shape[i];
        }
        return s;
    }

    public int Ndim => Shape.Length;

    public long ByteSize => (long)Data.Length * sizeof(float);

    /// <summary>
    /// Adds values from <paramref name="src"/> into this tensor at a sub-region.
    /// dstRegion[d] = {start, stop}, srcRegion[d] = {start, stop}; region sizes must match per dimension.
    /// </summary>
    public void AddFrom(FloatTensor src, int[][] dstRegion, int[][] srcRegion)
    {
        int n = Shape.Length;
        var count = new int[n];
        int total = 1;
        for (int d = 0; d < n; d++)
        {
            count[d] = dstRegion[d][1] - dstRegion[d][0];
            total *= count[d];
        }
        if (total == 0) return;

        // Fast path for the shape actually used by the pipeline: 3-D regions.
        if (n == 3)
        {
            int c0 = count[0], c1 = count[1], c2 = count[2];
            int dstS0 = Strides[0], dstS1 = Strides[1], dstS2 = Strides[2];
            int srcS0 = src.Strides[0], srcS1 = src.Strides[1], srcS2 = src.Strides[2];
            for (int a = 0; a < c0; a++)
            {
                int dstA = (dstRegion[0][0] + a) * dstS0;
                int srcA = (srcRegion[0][0] + a) * srcS0;
                for (int b = 0; b < c1; b++)
                {
                    int dstB = dstA + (dstRegion[1][0] + b) * dstS1;
                    int srcB = srcA + (srcRegion[1][0] + b) * srcS1;
                    for (int c = 0; c < c2; c++)
                    {
                        Data[dstB + (dstRegion[2][0] + c) * dstS2] +=
                            src.Data[srcB + (srcRegion[2][0] + c) * srcS2];
                    }
                }
            }
            return;
        }

        var iterStrides = new int[n];
        iterStrides[n - 1] = 1;
        for (int d = n - 2; d >= 0; d--) iterStrides[d] = iterStrides[d + 1] * count[d + 1];

        for (int flat = 0; flat < total; flat++)
        {
            int dstFlat = 0, srcFlat = 0;
            for (int d = 0; d < n; d++)
            {
                int idx = (flat / iterStrides[d]) % count[d];
                dstFlat += (dstRegion[d][0] + idx) * Strides[d];
                srcFlat += (srcRegion[d][0] + idx) * src.Strides[d];
            }
            Data[dstFlat] += src.Data[srcFlat];
        }
    }

    /// <summary>Extracts a contiguous sub-region as a new zero-based tensor. region[d] = {start, stop}.</summary>
    public FloatTensor Slice(int[][] region)
    {
        int n = Shape.Length;
        var newShape = new int[n];
        for (int d = 0; d < n; d++) newShape[d] = region[d][1] - region[d][0];

        var result = new FloatTensor(newShape);
        var dstRegion = new int[n][];
        for (int d = 0; d < n; d++) dstRegion[d] = new[] { 0, newShape[d] };
        result.AddFrom(this, dstRegion, region);
        return result;
    }

    public override string ToString() => "FloatTensor(shape=[" + string.Join(",", Shape) + "])";
}
