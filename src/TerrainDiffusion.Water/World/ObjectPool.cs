using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TerrainDiffusion.Water.World;

/// <summary>
/// A thread-safe pool of reusable work objects (scratch buffers), so that tiles worked on in parallel each get
/// their own and nothing is reallocated per tile.
/// </summary>
/// <remarks>At most <c>maxRetained</c> objects are kept between uses; one given back beyond that is dropped
/// (for the garbage collector), so the memory held while idle stays bounded.</remarks>
/// <typeparam name="T">The pooled type.</typeparam>
internal sealed class ObjectPool<T> where T : class
{
    /// <summary>Objects waiting to be reused.</summary>
    private readonly ConcurrentBag<T> items = new();
    /// <summary>Makes a new object when none is waiting.</summary>
    private readonly Func<T> create;
    /// <summary>Most objects kept waiting.</summary>
    private readonly int maxRetained;
    /// <summary>Objects waiting (kept in step with <see cref="items"/>).</summary>
    private int retained;

    /// <summary>A pool that makes new objects with <paramref name="create"/> when empty.</summary>
    /// <param name="create">Factory.</param>
    /// <param name="maxRetained">Most objects kept between uses (at least 1).</param>
    public ObjectPool(Func<T> create, int maxRetained)
    {
        this.create = create;
        this.maxRetained = Math.Max(1, maxRetained);
    }

    /// <summary>Takes an object (a pooled one if there is one).</summary>
    /// <returns>The object; give it back with <see cref="Return"/>.</returns>
    public T Rent()
    {
        if (items.TryTake(out T? item))
        {
            Interlocked.Decrement(ref retained);
            return item;
        }
        return create();
    }

    /// <summary>Gives an object back for reuse, or drops it if the pool is full.</summary>
    /// <param name="item">An object from <see cref="Rent"/>.</param>
    public void Return(T item)
    {
        if (Interlocked.Increment(ref retained) > maxRetained)
        {
            Interlocked.Decrement(ref retained);
            return;
        }
        items.Add(item);
    }

    /// <summary>Drops every waiting object.</summary>
    public void Clear()
    {
        while (items.TryTake(out _))
        {
            Interlocked.Decrement(ref retained);
        }
    }
}
