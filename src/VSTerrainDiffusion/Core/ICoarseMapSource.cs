using VSTerrainDiffusion.Tensors;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Implemented by terrain sources that can hand out the pipeline's coarse map. The coarse stage is
/// thousands of times cheaper per unit area than full detail, which makes surveying a continent for
/// a spawn point or a peak height practical. Sources without it (the HTTP API) fall back to probing
/// at full resolution, which works but is slow enough to be worth avoiding.
/// </summary>
public interface ICoarseMapSource
{
    /// <summary>
    /// All channels of the coarse map over [ci0, ci1) x [cj0, cj1) coarse cells. Channel 0 is
    /// elevation in signed square-root space and the last channel is the blend weight every other
    /// channel must be divided by.
    /// </summary>
    FloatTensor GetCoarseSlice(int ci0, int cj0, int ci1, int cj1);

    /// <summary>Native model pixels along one edge of a coarse cell.</summary>
    int CoarseCellNativePixels { get; }
}
