namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// The ground whose terrain tiles generating chunks for real reads, as opposed to a chunk peek
/// (<see cref="ChunkPeekScope"/>, which holds its map layers to the columns it looks at).
///
/// Two things widen it past the column itself. Vintage Story advances a column to a pass only once
/// its eight neighbours have reached the pass before, so finishing one column generates terrain
/// for every column within <see cref="NeighbourReachBlocks"/>. And each column's map region is
/// generated whole, with model-backed map layers (climate, forest, shrubs, and the seasonality map)
/// that sample the whole region and past its edges - sixteen 256-block tiles for a 512-block region.
/// </summary>
public static class TerrainFootprint
{
    /// <summary>
    /// How far finishing one column generates terrain around it: the six passes from Terrain to
    /// Done each need the neighbours one chunk further out at the pass before, five chunks in all,
    /// plus one for slack.
    /// </summary>
    public const int NeighbourReachBlocks = 6 * 32;

    /// <summary>
    /// Blocks the map layers sample beyond their region, rounded up: the climate map is the widest,
    /// one 32-block pixel before the region and two after; forest and shrubs have none.
    /// </summary>
    public const int MapLayerMarginBlocks = 96;

    /// <summary>
    /// The blocks, maximum exclusive, whose terrain generating terrain for any column in the given
    /// rectangle reads: every map region the rectangle overlaps, plus <see cref="MapLayerMarginBlocks"/>.
    /// Widen the rectangle by <see cref="NeighbourReachBlocks"/> first for columns that are finished
    /// rather than only given terrain.
    /// </summary>
    public static void Regions(int minX, int minZ, int maxX, int maxZ, int regionSize,
                               out int fromX, out int fromZ, out int toX, out int toZ)
    {
        fromX = FloorDiv(minX, regionSize) * regionSize - MapLayerMarginBlocks;
        fromZ = FloorDiv(minZ, regionSize) * regionSize - MapLayerMarginBlocks;
        toX = (FloorDiv(maxX - 1, regionSize) + 1) * regionSize + MapLayerMarginBlocks;
        toZ = (FloorDiv(maxZ - 1, regionSize) + 1) * regionSize + MapLayerMarginBlocks;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
