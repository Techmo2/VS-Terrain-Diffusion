using System;
using System.Collections.Generic;

namespace VSTerrainDiffusion.Tensors;

/// <summary>
/// A lazy, sliding-window "infinite" tensor backed by a <see cref="MemoryTileStore"/>.
/// Each computed window output is stored in an LRU cache keyed by window index; overlapping
/// windows are summed to produce the final slice. Create instances through the store only.
/// </summary>
public sealed class InfiniteTensor
{
    private readonly string _id;

    /// <summary>Shape per dimension; null entries mean unbounded.</summary>
    private readonly int?[] _shape;

    private readonly TensorWindow _outputWindow;
    private readonly TensorFunction _function;
    private readonly BatchTensorFunction _batchFunction;
    private readonly int _batchSize;
    private readonly InfiniteTensor[] _deps;
    private readonly TensorWindow[] _depWindows;
    private readonly MemoryTileStore _store;
    private readonly long _cacheLimitBytes;

    internal InfiniteTensor(
        string id,
        int?[] shape,
        TensorWindow outputWindow,
        TensorFunction function,
        BatchTensorFunction batchFunction,
        int batchSize,
        InfiniteTensor[] deps,
        TensorWindow[] depWindows,
        MemoryTileStore store,
        long cacheLimitBytes)
    {
        _id = id;
        _shape = shape;
        _outputWindow = outputWindow;
        _function = function;
        _batchFunction = batchFunction;
        _batchSize = batchSize;
        _deps = deps;
        _depWindows = depWindows;
        _store = store;
        _cacheLimitBytes = cacheLimitBytes;
    }

    /// <summary>Retrieves a contiguous slice; <paramref name="start"/> inclusive, <paramref name="end"/> exclusive.</summary>
    public FloatTensor GetSlice(int[] start, int[] end)
    {
        int n = _shape.Length;
        int[][] pixelRange = BuildRange(start, end);

        EnsureComputed(pixelRange);

        var outShape = new int[n];
        for (int d = 0; d < n; d++) outShape[d] = end[d] - start[d];
        var output = new FloatTensor(outShape);

        int[] lo = _outputWindow.GetLowestIntersection(pixelRange);
        int[] hi = _outputWindow.GetHighestIntersection(pixelRange);

        IterateWindows(lo, hi, windowIndex =>
        {
            FloatTensor cached = _store.GetCachedWindow(_id, windowIndex);
            if (cached == null) return;

            int[][] wBounds = _outputWindow.GetBounds(windowIndex);

            var isect = new int[n][];
            for (int d = 0; d < n; d++)
            {
                int a = Math.Max(pixelRange[d][0], wBounds[d][0]);
                int b = Math.Min(pixelRange[d][1], wBounds[d][1]);
                if (a >= b) return; // no overlap
                isect[d] = new[] { a, b };
            }

            var srcRegion = new int[n][];
            var dstRegion = new int[n][];
            for (int d = 0; d < n; d++)
            {
                srcRegion[d] = new[] { isect[d][0] - wBounds[d][0], isect[d][1] - wBounds[d][0] };
                dstRegion[d] = new[] { isect[d][0] - pixelRange[d][0], isect[d][1] - pixelRange[d][0] };
            }

            output.AddFrom(cached, dstRegion, srcRegion);
        });

        _store.EvictIfNeeded(_id, _cacheLimitBytes);
        return output;
    }

    private void EnsureComputed(int[][] pixelRange)
        => EnsureComputedRanges(new List<int[][]> { pixelRange });

    /// <summary>
    /// Ensures every window intersecting any of the given pixel ranges is cached, recursively
    /// resolving upstream dependencies first. Ranges are kept separate (no bounding-box union) so
    /// only windows that actually intersect a request are computed.
    /// </summary>
    private void EnsureComputedRanges(IReadOnlyList<int[][]> pixelRanges)
    {
        var pendingSet = new HashSet<string>();
        var pending = new List<int[]>();

        foreach (int[][] range in pixelRanges)
        {
            int[] lo = _outputWindow.GetLowestIntersection(range);
            int[] hi = _outputWindow.GetHighestIntersection(range);
            IterateWindows(lo, hi, wi =>
            {
                if (_store.IsWindowCached(_id, wi)) return;
                string key = string.Join(",", wi);
                if (pendingSet.Add(key)) pending.Add(wi);
            });
        }

        if (pending.Count == 0) return;

        for (int i = 0; i < _deps.Length; i++)
        {
            var depRanges = new List<int[][]>(pending.Count);
            foreach (int[] wi in pending) depRanges.Add(_depWindows[i].GetBounds(wi));
            _deps[i].EnsureComputedRanges(depRanges);
        }

        if (_batchSize > 0 && _batchFunction != null) ComputeBatched(pending);
        else foreach (int[] windowIndex in pending) ComputeSingle(windowIndex);
    }

    private void ComputeSingle(int[] windowIndex)
    {
        var args = new List<FloatTensor>(_deps.Length);
        for (int i = 0; i < _deps.Length; i++) args.Add(SliceDep(i, windowIndex));

        FloatTensor result = _function(windowIndex, args);
        ValidateOutputShape(result, windowIndex);
        _store.CacheWindow(_id, windowIndex, result);
    }

    private void ComputeBatched(List<int[]> windowIndices)
    {
        int from = 0;
        while (from < windowIndices.Count)
        {
            int to = Math.Min(from + _batchSize, windowIndices.Count);
            var batch = windowIndices.GetRange(from, to - from);

            var args = new List<IReadOnlyList<FloatTensor>>(_deps.Length);
            for (int i = 0; i < _deps.Length; i++)
            {
                var depArgs = new List<FloatTensor>(batch.Count);
                foreach (int[] windowIndex in batch) depArgs.Add(SliceDep(i, windowIndex));
                args.Add(depArgs);
            }

            IReadOnlyList<FloatTensor> outputs = _batchFunction(batch, args);
            for (int k = 0; k < batch.Count; k++)
            {
                FloatTensor result = outputs[k];
                ValidateOutputShape(result, batch[k]);
                _store.CacheWindow(_id, batch[k], result);
            }

            from = to;
        }
    }

    private FloatTensor SliceDep(int depIndex, int[] windowIndex)
    {
        int[][] bounds = _depWindows[depIndex].GetBounds(windowIndex);
        var depStart = new int[bounds.Length];
        var depEnd = new int[bounds.Length];
        for (int d = 0; d < bounds.Length; d++)
        {
            depStart[d] = bounds[d][0];
            depEnd[d] = bounds[d][1];
        }
        return _deps[depIndex].GetSlice(depStart, depEnd);
    }

    private void ValidateOutputShape(FloatTensor result, int[] windowIndex)
    {
        int n = _outputWindow.Size.Length;
        if (result.Shape.Length != n)
        {
            throw new InvalidOperationException(
                $"Function for tensor '{_id}' returned shape with {result.Shape.Length} dims, expected {n}");
        }
        for (int d = 0; d < n; d++)
        {
            if (result.Shape[d] != _outputWindow.Size[d])
            {
                throw new InvalidOperationException(
                    $"Function for tensor '{_id}' returned shape[{d}]={result.Shape[d]}, expected {_outputWindow.Size[d]}");
            }
        }
    }

    private static int[][] BuildRange(int[] start, int[] end)
    {
        int n = start.Length;
        var range = new int[n][];
        for (int d = 0; d < n; d++) range[d] = new[] { start[d], end[d] };
        return range;
    }

    /// <summary>Iterates over all window index combinations in the inclusive range [lo, hi].</summary>
    private static void IterateWindows(int[] lo, int[] hi, Action<int[]> action)
    {
        int n = lo.Length;
        for (int d = 0; d < n; d++) if (lo[d] > hi[d]) return;

        var current = (int[])lo.Clone();
        while (true)
        {
            action((int[])current.Clone());

            int d = n - 1;
            for (; d >= 0; d--)
            {
                current[d]++;
                if (current[d] <= hi[d]) break;
                current[d] = lo[d];
                if (d == 0) return;
            }
        }
    }
}
