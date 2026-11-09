using System;
using System.Threading;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Where the pipeline asks whether it should still be running.
///
/// Checked between model runs rather than inside one. A run is 2-8 ms for the coarse model and
/// 25-120 ms for the base and decoder, so the coarsest possible answer is still about a tenth of a
/// second, and a tile is many runs. ONNX Runtime's own <c>RunOptions.Terminate</c> would cut a run
/// in half instead, but it is checked at node boundaries, and on the TensorRT RTX provider - the
/// fast one - most of the graph is a single fused node, so it would buy little for a good deal of
/// provider-specific behaviour.
///
/// Nothing is published until a window is complete (<c>InfiniteTensor.ComputeSingle</c> calls
/// <c>CacheWindow</c> only after the function returns), so abandoning a run part way through leaves
/// no half-built terrain behind - only work to do again.
/// </summary>
public static class InferencePreemption
{
    /// <summary>This thread's current attempt, or null outside one or inside <see cref="Suspend"/>.</summary>
    [ThreadStatic] private static PreemptionToken _current;

    /// <summary>Binds a token to this thread for the duration of one attempt.</summary>
    public static IDisposable Begin(PreemptionToken token)
    {
        _current = token;
        return new Scope();
    }

    /// <summary>Abandons the current attempt if something more urgent is waiting.</summary>
    public static void ThrowIfPreempted()
    {
        if (_current is { IsPreempted: true }) throw new InferencePreemptedException();
    }

    /// <summary>
    /// Stops this thread's current attempt from being abandoned until the scope ends, then restores
    /// it. For work nested inside an attempt that must not be interrupted part way: its caller
    /// would keep the failure, not retry it.
    /// </summary>
    public static IDisposable Suspend()
    {
        var restore = new Restore(_current);
        _current = null;
        return restore;
    }

    /// <summary>Ends a <see cref="Begin"/> scope.</summary>
    private sealed class Scope : IDisposable
    {
        /// <summary>Unbinds the attempt.</summary>
        public void Dispose() => _current = null;
    }

    /// <summary>Ends a <see cref="Suspend"/> scope.</summary>
    private sealed class Restore : IDisposable
    {
        /// <summary>The attempt suspended, to rebind.</summary>
        private readonly PreemptionToken _saved;

        /// <summary>Remembers the attempt to rebind.</summary>
        /// <param name="saved">This thread's attempt when the scope began.</param>
        public Restore(PreemptionToken saved) => _saved = saved;

        /// <summary>Rebinds the suspended attempt.</summary>
        public void Dispose() => _current = _saved;
    }
}
