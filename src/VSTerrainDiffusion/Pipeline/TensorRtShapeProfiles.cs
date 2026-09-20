namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// The input shapes TensorRT RTX builds engines for. It refuses to build for a symbolic dimension
/// it has no profile for ("kOPT values for profile 0 violate shape constraints"), and the pipeline
/// calls each graph at one spatial size, so the profiles come from its tile constants. Only the
/// base model varies, in batch.
/// </summary>
internal static class TensorRtShapeProfiles
{
    /// <summary>Min and max profile in the EP's <c>name:dimxdim</c> notation, or null if none.</summary>
    internal static (string Min, string Max)? For(string modelName)
    {
        const int coarse = WorldPipeline.CoarseTileSize;
        const int latent = WorldPipeline.LatentTileSize;
        const int decoder = WorldPipeline.DecoderTileSize;

        switch (modelName)
        {
            case "coarse":
            {
                // Six noisy channels plus five conditioning channels, and five scalar inputs.
                string shape = $"x:1x11x{coarse}x{coarse},noise_labels:1," +
                               "cond_0:1,cond_1:1,cond_2:1,cond_3:1,cond_4:1";
                return (shape, shape);
            }

            case "base":
            {
                int maxBatch = WorldPipeline.ResolveLatentBatchSize();
                string min = $"x:1x5x{latent}x{latent},noise_labels:1,cond_0:1x58";
                string max = $"x:{maxBatch}x5x{latent}x{latent},noise_labels:{maxBatch},cond_0:{maxBatch}x58";
                return (min, max);
            }

            case "decoder":
            {
                // Not batched: one window saturates the GPU.
                string shape = $"x:1x5x{decoder}x{decoder},noise_labels:1";
                return (shape, shape);
            }

            default:
                return null;
        }
    }
}
