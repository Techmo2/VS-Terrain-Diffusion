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
    /// <summary>One tensor's cached windows, by index.</summary>
    private sealed class WindowCache
    {
        /// <summary>Window index to its node in the shared eviction order.</summary>
        public readonly Dictionary<WindowKey, LinkedListNode<CacheNode>> Map = new();
    }

    /// <summary>One cached window, in the shared eviction order.</summary>
    private sealed class CacheNode
    {
        /// <summary>The tensor it belongs to.</summary>
        public string CacheId;

        /// <summary>Its window index.</summary>
        public WindowKey Key;

        /// <summary>The computed window.</summary>
        public FloatTensor Value;
    }

    /// <summary>A window index as a dictionary key, compared by value.</summary>
    private readonly struct WindowKey : IEquatable<WindowKey>
    {
        /// <summary>The index, copied.</summary>
        private readonly int[] _index;

        /// <summary>Hash of the index, computed once.</summary>
        private readonly int _hash;

        /// <summary>Creates a key.</summary>
        /// <param name="index">The window index; copied.</param>
        public WindowKey(int[] index)
        {
            _index = (int[])index.Clone();
            int h = 17;
            foreach (int v in _index) h = h * 31 + v;
            _hash = h;
        }

        /// <summary>Whether two keys hold the same index.</summary>
        /// <param name="other">The other key.</param>
        /// <returns>True if equal.</returns>
        public bool Equals(WindowKey other)
        {
            if (_index.Length != other._index.Length) return false;
            for (int i = 0; i < _index.Length; i++)
                if (_index[i] != other._index[i]) return false;
            return true;
        }

        /// <summary>Whether an object is an equal key.</summary>
        /// <param name="obj">The object.</param>
        /// <returns>True if equal.</returns>
        public override bool Equals(object obj) => obj is WindowKey k && Equals(k);

        /// <summary>The precomputed hash.</summary>
        /// <returns>The hash.</returns>
        public override int GetHashCode() => _hash;

        /// <summary>A copy of the index.</summary>
        /// <returns>The index.</returns>
        public int[] ToArray() => (int[])_index.Clone();
    }

    /// <summary>Each tensor's cached windows, by tensor id.</summary>
    private readonly Dictionary<string, WindowCache> _caches = new();

    /// <summary>Registered tensors, by id.</summary>
    private readonly Dictionary<string, InfiniteTensor> _tensors = new();

    /// <summary>Slices being read per tensor id; their windows are not evicted meanwhile.</summary>
    private readonly Dictionary<string, int> _activeReaders = new();

    /// <summary>Every cached window, least recently used first.</summary>
    private readonly LinkedList<CacheNode> _order = new();

    /// <summary>The byte budget shared by all tensors; long.MaxValue for none.</summary>
    private readonly long _cacheLimitBytes;

    /// <summary>Bytes of windows cached now.</summary>
    private long _cachedBytes;

    /// <summary>Windows computed and cached since startup.</summary>
    private long _totalComputedWindowCount;

    /// <summary>Creates an empty store.</summary>
    /// <param name="cacheLimitBytes">The byte budget for every tensor's windows together.</param>
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

    /// <summary>Records a new tensor and gives it a cache.</summary>
    /// <param name="id">Its name.</param>
    /// <param name="tensor">The tensor.</param>
    private void Register(string id, InfiniteTensor tensor)
    {
        _tensors[id] = tensor;
        _caches[id] = new WindowCache();
    }

    // ---------------------------------------------------------------------
    // Cache operations (called from InfiniteTensor)
    // ---------------------------------------------------------------------

    /// <summary>Caches a computed window, or marks it recently used if already cached.</summary>
    /// <param name="id">The tensor.</param>
    /// <param name="windowIndex">The window.</param>
    /// <param name="output">The computed values.</param>
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

    /// <summary>Pins a tensor's windows while a slice of it is read.</summary>
    /// <param name="id">The tensor.</param>
    internal void BeginRead(string id)
    {
        _activeReaders.TryGetValue(id, out int readers);
        _activeReaders[id] = readers + 1;
    }

    /// <summary>Unpins after a read, and evicts if over budget.</summary>
    /// <param name="id">The tensor.</param>
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

    /// <summary>
    /// Drops least recently used windows of unpinned tensors until within budget, or until only pinned
    /// windows are left.
    /// </summary>
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

    /// <summary>A cached window, marked recently used, or null.</summary>
    /// <param name="id">The tensor.</param>
    /// <param name="windowIndex">The window.</param>
    /// <returns>The window, or null.</returns>
    internal FloatTensor GetCachedWindow(string id, int[] windowIndex)
    {
        if (!_caches.TryGetValue(id, out var cache)) return null;
        if (!cache.Map.TryGetValue(new WindowKey(windowIndex), out var node)) return null;
        _order.Remove(node);
        _order.AddLast(node);
        return node.Value.Value;
    }

    /// <summary>Whether a window is cached, without touching its recency.</summary>
    /// <param name="id">The tensor.</param>
    /// <param name="windowIndex">The window.</param>
    /// <returns>True if cached.</returns>
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
