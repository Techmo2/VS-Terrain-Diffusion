namespace VSTerrainDiffusion.WorldGen;

/// <summary>How much of the world's climate the diffusion model is responsible for.</summary>
public enum DiffusionClimateMode
{
    /// <summary>Vanilla climate, untouched.</summary>
    Off,

    /// <summary>
    /// Temperature, rainfall and vegetation cover all come from the model, with no latitude
    /// gradient at all.
    /// </summary>
    Full
}
