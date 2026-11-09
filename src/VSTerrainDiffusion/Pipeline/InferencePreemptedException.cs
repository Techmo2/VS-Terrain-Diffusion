using System;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Thrown out of the middle of a tile's inference when something more urgent needs the device.
/// Caught by the scheduler, which runs the urgent work and then starts this tile again.
/// </summary>
public sealed class InferencePreemptedException : Exception
{
    /// <summary>The one message every preemption carries.</summary>
    public InferencePreemptedException() : base("Inference was preempted by higher priority work.") { }
}
