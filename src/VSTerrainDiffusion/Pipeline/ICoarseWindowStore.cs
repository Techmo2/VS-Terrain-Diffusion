namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Somewhere a pipeline's finished coarse windows outlive the session - the world's tile database -
/// so the coarse model runs once per window per world. See <see cref="WorldPipeline.CoarseWindowStore"/>.
/// </summary>
public interface ICoarseWindowStore
{
    /// <summary>The stored window, exactly as it was computed, or null.</summary>
    float[] TryLoad(int windowI, int windowJ, int length);

    void Save(int windowI, int windowJ, float[] data);
}
