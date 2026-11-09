using System;

namespace TerrainDiffusion.Water;

/// <summary>
/// Configuration of a <see cref="RiverGraph"/>. Every tile of one graph uses the same settings; a different
/// setting means a different world's water.
/// </summary>
public sealed class RiverGraphSettings
{
    /// <summary>Discharge (upstream cells) above which every river is drawn alike: 2¹⁹. Once a cell's known
    /// discharge reaches it the cell is final, whatever arrives upstream later.</summary>
    public const double DefaultDischargeCap = 524288.0;

    /// <summary>Default <see cref="MaxFillRadius"/>: up to 7 × 7 tiles. On real terrain about a quarter of the
    /// tiles need more than 3 × 3 and one in twenty 7 × 7.</summary>
    public const int DefaultMaxFillRadius = 3;

    /// <summary>Default <see cref="FlatGroundWeight"/>.</summary>
    public const double DefaultFlatGroundWeight = 8.0;

    /// <summary>Tile side in cells (blocks). The mod generates 256 on GPU and 128 on CPU.</summary>
    public int TileSize { get; init; } = 256;

    /// <summary>Chunks per tile side; a chunk is released (handed to the game) once all its cells are final.
    /// 8 gives the game's 32-block chunks on 256-block tiles.</summary>
    public int ChunksPerTileSide { get; init; } = 8;

    /// <summary>Cells of the neighbouring tiles the fill sees around a tile (1 to <see cref="TileSize"/>;
    /// null means a whole tile). More sees more of a basin before calling it open.</summary>
    public int? FillMargin { get; init; }

    /// <summary>Rings of tiles a fill may grow to (1 to 8): a tile any of whose own cells has a level decided
    /// only by the edge of its window (a lake may go on beyond it) is filled again with one more ring of tiles, up
    /// to this many, so that every tile a lake touches sees the lake whole and gives it the same level. 1 keeps
    /// every fill to the tile's 8 neighbours (faster; a lake wider than a window then ends at a tile seam).</summary>
    public int MaxFillRadius { get; init; } = DefaultMaxFillRadius;

    /// <summary>How much a river crossing a flat (a lake surface, often only centimetres over the ground) keeps to
    /// the low ground under it: crossing a cell costs 1 over the flat's lowest ground, rising to 1 + this where the
    /// ground reaches the flat's level. 0 crosses by the shortest way, a straight line to the outlet; the default
    /// follows the drowned valley floor, as a channel would.</summary>
    public double FlatGroundWeight { get; init; } = DefaultFlatGroundWeight;

    /// <summary>Closed basins of at least this many cells keep a terminal lake of about this size instead of
    /// filling to their spill (null means one tile's worth, <c>TileSize²</c>).</summary>
    public int? LakeCells { get; init; }

    /// <summary>See <see cref="DefaultDischargeCap"/>.</summary>
    public double DischargeCap { get; init; } = DefaultDischargeCap;

    /// <summary>Size of a cell in metres (30 m / the mod's scale).</summary>
    public double CellSizeMetres { get; init; } = 15.0;

    /// <summary>World seed. Only the meander field depends on it.</summary>
    public uint Seed { get; init; }

    /// <summary>Meandering; null routes plain steepest descent.</summary>
    public MeanderSettings? Meander { get; init; } = new();

    /// <summary>The default <see cref="MaxDegreeOfParallelism"/>: half the logical processors (about one per
    /// physical core, leaving their second hardware threads to the terrain generator and the game), at most
    /// 8 (more barely helps: the fill is limited by memory bandwidth), at least 1.</summary>
    public static int DefaultMaxDegreeOfParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    /// <summary>Most threads working at once: the tiles an arrival (or a batch, see
    /// <see cref="RiverGraph.AddTiles"/>) makes ready are filled and routed in parallel, each with its own scratch
    /// (about 30 MB per thread, kept between arrivals; the larger scratch of a widened window is freed after
    /// use). 1 is sequential. The result never depends on it.</summary>
    public int MaxDegreeOfParallelism { get; init; } = DefaultMaxDegreeOfParallelism;

    /// <summary>The fill margin in cells, with the default applied.</summary>
    public int EffectiveFillMargin => FillMargin ?? TileSize;

    /// <summary>The lake size in cells, with the default applied.</summary>
    public int EffectiveLakeCells => LakeCells ?? TileSize * TileSize;

    /// <summary>Throws if any setting is out of range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    public void Validate()
    {
        if (TileSize < 8 || TileSize > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(TileSize), TileSize, "TileSize must be 8-4096.");
        }
        if (ChunksPerTileSide < 1 || TileSize % ChunksPerTileSide != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunksPerTileSide), ChunksPerTileSide,
                "ChunksPerTileSide must be positive and divide TileSize.");
        }
        if (EffectiveFillMargin < 1 || EffectiveFillMargin > TileSize)
        {
            throw new ArgumentOutOfRangeException(nameof(FillMargin), FillMargin, "FillMargin must be 1 to TileSize.");
        }
        if (MaxFillRadius < 1 || MaxFillRadius > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFillRadius), MaxFillRadius, "MaxFillRadius must be 1-8.");
        }
        if (!(FlatGroundWeight >= 0.0) || !double.IsFinite(FlatGroundWeight))
        {
            throw new ArgumentOutOfRangeException(nameof(FlatGroundWeight), FlatGroundWeight,
                "FlatGroundWeight must be finite and at least 0.");
        }
        if (EffectiveLakeCells < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(LakeCells), LakeCells, "LakeCells must be positive.");
        }
        if (!(DischargeCap >= 1.0) || !double.IsFinite(DischargeCap))
        {
            throw new ArgumentOutOfRangeException(nameof(DischargeCap), DischargeCap, "DischargeCap must be at least 1.");
        }
        if (!(CellSizeMetres > 0.0) || !double.IsFinite(CellSizeMetres))
        {
            throw new ArgumentOutOfRangeException(nameof(CellSizeMetres), CellSizeMetres, "CellSizeMetres must be positive.");
        }
        if (MaxDegreeOfParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism), MaxDegreeOfParallelism,
                "MaxDegreeOfParallelism must be at least 1.");
        }
        Meander?.Validate();
    }
}
