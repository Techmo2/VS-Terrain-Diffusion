using System;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Pooling applied to the coarse model's output, from the reference implementation's
/// <c>coarse_pooling</c>: each n x n block of the coarse map it draws becomes one coarse cell of the
/// world, so the world holds n times the landscape in each direction and the base model fills every
/// cell in at the usual resolution. Mountains, valleys and coasts come closer together and the
/// relief between them steepens, without the model ever being asked for anything it was not
/// trained on.
///
/// The conditioning - the world's ocean map, its latitude bands, Rivers' basins - is drawn in the
/// coarse model's own grid, n times finer than the world's coarse cells, so everything that turns a
/// conditioning pixel into blocks divides by <see cref="Factor"/>.
/// </summary>
public readonly record struct CoarsePooling(int Factor, bool Extreme)
{
    /// <summary>No pooling: the reference pipeline's default and this mod's behaviour before it.</summary>
    public static readonly CoarsePooling None = new(1, false);

    /// <summary>The factors that divide both the coarse tile (64) and its stride (48).</summary>
    public static readonly int[] Allowed = { 1, 2, 4, 8, 16 };

    public bool IsNone => Factor <= 1;

    /// <summary>A factor this pipeline can run, or <see cref="None"/>.</summary>
    public static CoarsePooling Of(int factor, bool extreme) =>
        Array.IndexOf(Allowed, factor) > 0 ? new CoarsePooling(factor, extreme) : None;

    public override string ToString() =>
        IsNone ? "off" : $"{Factor}x{(Extreme ? ", max elevation / min p5" : "")}";
}
