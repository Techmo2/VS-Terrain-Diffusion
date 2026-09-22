using System;
using System.Threading;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Thrown out of the middle of a tile's inference when something more urgent needs the device.
/// Caught by the scheduler, which runs the urgent work and then starts this tile again.
/// </summary>
public sealed class InferencePreemptedException : Exception
{
    public InferencePreemptedException() : base("Inference was preempted by higher priority work.") { }
}

/// <summary>A single unit of preemptible work. One per attempt at generating a tile.</summary>
public sealed class PreemptionToken
{
    private volatile bool _preempted;

    public bool IsPreempted => _preempted;

    public void Preempt() => _preempted = true;
}

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

    private sealed class Scope : IDisposable
    {
        public void Dispose() => _current = null;
    }
}
