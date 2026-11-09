namespace VSTerrainDiffusion.Pipeline;

/// <summary>A single unit of preemptible work. One per attempt at generating a tile.</summary>
public sealed class PreemptionToken
{
    /// <summary>Backing field of <see cref="IsPreempted"/>; written by the scheduler, read by the tile's thread.</summary>
    private volatile bool _preempted;

    /// <summary>Whether the attempt should give up at its next check.</summary>
    public bool IsPreempted => _preempted;

    /// <summary>Asks the attempt to give up; it does so at its next <see cref="InferencePreemption.ThrowIfPreempted"/>.</summary>
    public void Preempt() => _preempted = true;
}
