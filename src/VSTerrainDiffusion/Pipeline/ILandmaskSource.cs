namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Supplies "how much of this coarse conditioning pixel is sea" to the coarse model.
///
/// The coarse model is conditional: alongside the noise it denoises, it takes a five-channel
/// conditioning image of elevation and climate. Left to itself the pipeline fills that in with
/// Perlin noise quantile-matched to real-world statistics, which is why the model invents its own
/// continents and ignores everything the world was configured to be. Handing it a landmask instead
/// makes the world's own ocean map decide where the sea goes, leaving the model to decide what the
/// coast, the shelf and the mountains behind them actually look like.
///
/// Coordinates are coarse pixels in the model's own frame - the same units
/// <see cref="SyntheticMapFactory.Sample"/> takes, one pixel per 256 native pixels.
/// </summary>
public interface ILandmaskSource
{
    /// <summary>
    /// Sea fraction in 0..1 for every coarse pixel of the half-open window
    /// [<paramref name="x1"/>, <paramref name="x2"/>) x [<paramref name="y1"/>, <paramref name="y2"/>),
    /// row-major. Never null, and never partial: a source that cannot answer must stop the game
    /// rather than return nothing, because a window conditioned on the world's ocean map and the
    /// next one conditioned on the pipeline's own noise put their continents in different places.
    /// </summary>
    float[] SeaFraction(int x1, int y1, int x2, int y2);
}
