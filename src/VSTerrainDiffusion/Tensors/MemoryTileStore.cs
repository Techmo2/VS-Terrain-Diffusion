using System;
using System.Collections.Generic;
using System.Threading;

namespace VSTerrainDiffusion.Tensors;

/// <summary>Computes a single window of an <see cref="InfiniteTensor"/>.</summary>
public delegate FloatTensor TensorFunction(int[] windowIndex, IReadOnlyList<FloatTensor> args);

/// <summary>Batched variant of <see cref="TensorFunction"/>; args[depIdx][batchIdx].</summary>
public delegate IReadOnlyList<FloatTensor> BatchTensorFunction(
    IReadOnlyList<int[]> windowIndices, IReadOnlyList<IReadOnlyList<FloatTensor>> args);

/// <summary>
/// In-memory factory and LRU cache for <see cref="InfiniteTensor"/> window outputs.
/// All registered tensors share one byte budget and access-ordered eviction queue.
/// </summary>
public sealed class MemoryTileStore
{
    private sealed class WindowCache
    {
        public readonly Dictionary<WindowKey, LinkedListNode<CacheNode>> Map = new();
    }

    private sealed class CacheNode
    {
        public string CacheId;
        public WindowKey Key;
        public FloatTensor Value;
    }

    private readonly struct WindowKey : IEquatable<WindowKey>
    {
        private readonly int[] _index;
        private readonly int _hash;

        public WindowKey(int[] index)
        {
            _index = (int[])index.Clone();
            int h = 17;
            foreach (int v in _index) h = h * 31 + v;
            _hash = h;
        }

        public bool Equals(WindowKey other)
        {
            if (_index.Length != other._index.Length) return false;
            for (int i = 0; i < _index.Length; i++)
                if (_index[i] != other._index[i]) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is WindowKey k && Equals(k);
        public override int GetHashCode() => _hash;
        public int[] ToArray() => (int[])_index.Clone();
    }

    private readonly Dictionary<string, WindowCache> _caches = new();
    private readonly Dictionary<string, InfiniteTensor> _tensors = new();
    private readonly Dictionary<string, int> _activeReaders = new();
    private readonly LinkedList<CacheNode> _order = new();
    private readonly long _cacheLimitBytes;
    private long _cachedBytes;
    private long _totalComputedWindowCount;

    public MemoryTileStore(long cacheLimitBytes)
    {
        _cacheLimitBytes = cacheLimitBytes;
    }

    // ---------------------------------------------------------------------
    // Factory
    // ---------------------------------------------------------------------

    /// <summary>Creates a non-batched tensor, or returns the existing one registered under <paramref name="id"/>.</summary>
    public InfiniteTensor GetOrCreate(
        string id,
        int?[] shape,
        TensorFunction function,
        TensorWindow outputWindow,
        InfiniteTensor[] deps,
        TensorWindow[] depWindows)
    {
        if (_tensors.TryGetValue(id, out var existing)) return existing;

        var tensor = new InfiniteTensor(id, shape, outputWindow, function, null, 0,
            deps, depWindows, this);
        Register(id, tensor);
        return tensor;
    }

    /// <summary>Creates a batched tensor, or returns the existing one.</summary>
    public InfiniteTensor GetOrCreateBatched(
        string id,
        int?[] shape,
        BatchTensorFunction batchFunction,
        TensorWindow outputWindow,
        InfiniteTensor[] deps,
        TensorWindow[] depWindows,
        int batchSize)
    {
        if (_tensors.TryGetValue(id, out var existing)) return existing;

        var tensor = new InfiniteTensor(id, shape, outputWindow, null, batchFunction, batchSize,
            deps, depWindows, this);
        Register(id, tensor);
        return tensor;
    }

    private void Register(string id, InfiniteTensor tensor)
    {
        _tensors[id] = tensor;
        _caches[id] = new WindowCache();
    }

    // ---------------------------------------------------------------------
    // Cache operations (called from InfiniteTensor)
    // ---------------------------------------------------------------------

    internal void CacheWindow(string id, int[] windowIndex, FloatTensor output)
    {
        var cache = _caches[id];
        var key = new WindowKey(windowIndex);
        if (cache.Map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddLast(node);
            return;
        }

        var added = _order.AddLast(new CacheNode { CacheId = id, Key = key, Value = output });
        cache.Map[key] = added;
        _cachedBytes += output.ByteSize;
        Interlocked.Increment(ref _totalComputedWindowCount);
    }

    /// <summary>Number of windows newly computed and cached since startup.</summary>
    public long TotalComputedWindowCount => Interlocked.Read(ref _totalComputedWindowCount);

    internal void BeginRead(string id)
    {
        _activeReaders.TryGetValue(id, out int readers);
        _activeReaders[id] = readers + 1;
    }

    internal void EndRead(string id)
    {
        if (!_activeReaders.TryGetValue(id, out int readers))
        {
            EvictIfNeeded();
            return;
        }

        readers--;
        if (readers == 0) _activeReaders.Remove(id);
        else _activeReaders[id] = readers;
        EvictIfNeeded();
    }

    private void EvictIfNeeded()
    {
        if (_cacheLimitBytes == long.MaxValue) return;

        while (_cachedBytes > _cacheLimitBytes)
        {
            LinkedListNode<CacheNode> candidate = _order.First;
            while (candidate != null && _activeReaders.ContainsKey(candidate.Value.CacheId))
                candidate = candidate.Next;

            // Active reads pin their tensor's windows until the requested slice has been copied.
            // If every entry is pinned, the outermost read will trim the cache when it completes.
            if (candidate == null) break;

            _order.Remove(candidate);
            _caches[candidate.Value.CacheId].Map.Remove(candidate.Value.Key);
            _cachedBytes -= candidate.Value.Value.ByteSize;
        }
    }

    internal FloatTensor GetCachedWindow(string id, int[] windowIndex)
    {
        if (!_caches.TryGetValue(id, out var cache)) return null;
        if (!cache.Map.TryGetValue(new WindowKey(windowIndex), out var node)) return null;
        _order.Remove(node);
        _order.AddLast(node);
        return node.Value.Value;
    }

    internal bool IsWindowCached(string id, int[] windowIndex)
        => _caches.TryGetValue(id, out var cache) && cache.Map.ContainsKey(new WindowKey(windowIndex));

    /// <summary>Removes all cached window outputs for every registered tensor.</summary>
    public void ClearAllCaches()
    {
        foreach (var cache in _caches.Values)
        {
            cache.Map.Clear();
        }
        _order.Clear();
        _cachedBytes = 0;
    }

    /// <summary>Total bytes currently held across all window caches.</summary>
    public long CachedBytes => _cachedBytes;
}
