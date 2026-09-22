namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Tells the coarse model where a river system runs, so the land it invents there is low enough to
/// carry one.
///
/// This is the counterpart to <see cref="ILandmaskSource"/> and works the same way: one value per
/// coarse conditioning pixel, used to bend the elevation rank rather than the elevation itself.
///
/// It cannot describe a river. A coarse pixel is 512 blocks across at the default scale and a
/// channel is tens of blocks wide, and there is no finer spatial conditioning anywhere in the
/// cascade - the latent stage sees a 58-value summary of a 4x4 coarse patch and everything below
/// that is invented. What it describes is a *drainage basin*: this cell is somewhere a river runs
/// through, so it should come out as low ground rather than a ridge. The channel itself is still
/// cut afterwards, into terrain that now has somewhere to put it.
/// </summary>
public interface IRiverBasinSource
{
    /// <summary>
    /// Basin strength in 0..1 for every coarse pixel of the half-open window
    /// [<paramref name="x1"/>, <paramref name="x2"/>) x [<paramref name="y1"/>, <paramref name="y2"/>),
    /// row-major, where 0 is untouched ground and 1 is as low as land is taken. Null when this
    /// world has no river network, which is not an error - it is the ordinary case.
    /// </summary>
    float[] BasinStrength(int x1, int y1, int x2, int y2);
}
