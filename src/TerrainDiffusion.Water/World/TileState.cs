using System;
using TerrainDiffusion.Water.Routing;

namespace TerrainDiffusion.Water.World;

/// <summary>
/// Everything known about one generated tile: its terrain, its water, and how far it is through the three
/// stages (fill, directions, discharge).
/// </summary>
internal sealed class TileState
{
    /// <summary>A newly arrived tile: its elevation, provisional water (dry ground that may still flood) and no
    /// chunk released.</summary>
    /// <param name="tile">The tile.</param>
    /// <param name="elevation">Its elevation (copied).</param>
    /// <param name="chunkCount">Chunks per tile.</param>
    /// <param name="arrivalStep">Index of its arrival.</param>
    public TileState(TileCoord tile, ReadOnlySpan<float> elevation, int chunkCount, int arrivalStep)
    {
        Tile = tile;
        Elevation = elevation.ToArray();
        Level = elevation.ToArray();
        Discharge = new float[elevation.Length];
        IsFinal = new bool[elevation.Length];
        ChunkReleaseStep = new int[chunkCount];
        Array.Fill(ChunkReleaseStep, -1);
        ArrivalStep = arrivalStep;
    }

    /// <summary>The tile.</summary>
    public TileCoord Tile { get; }

    /// <summary>Index of the arrival that brought it.</summary>
    public int ArrivalStep { get; }

    /// <summary>Terrain height in metres per cell, row-major.</summary>
    public float[] Elevation { get; }

    /// <summary>Water surface per cell (metres). The elevation until the tile is filled; then the fill level,
    /// final for every cell.</summary>
    public float[] Level { get; }

    /// <summary>River discharge per cell: upstream cells, capped at the discharge cap. 0 until the first
    /// flush.</summary>
    public float[] Discharge { get; }

    /// <summary>Whether each cell's drawn discharge can no longer change.</summary>
    public bool[] IsFinal { get; }

    /// <summary>Per chunk of the tile (row-major), the step it was released at; −1 while not released.</summary>
    public int[] ChunkReleaseStep { get; }

    /// <summary>Whether every chunk of the tile is released.</summary>
    public bool IsFullyReleased { get; set; }

    /// <summary>Whether the fill (water level) is done.</summary>
    public bool IsFilled { get; set; }

    /// <summary>Rings of tiles around it its fill needs: 1 (its 8 neighbours) unless a lake it holds runs into
    /// the edge of a smaller window (see <see cref="RiverGraphSettings.MaxFillRadius"/>).</summary>
    public int FillRadius { get; set; } = 1;

    /// <summary>Whether its flats need the 5 × 5 tiles around it for their directions.</summary>
    public bool IsWide { get; set; }

    /// <summary>The routing state, once the tile has directions.</summary>
    public TileFlowNetwork? Network { get; set; }

    /// <summary>Whether the tile has directions.</summary>
    public bool IsRouted => Network is not null;

    /// <summary>Approximate memory held, in bytes.</summary>
    public long ByteSize => 4L * Elevation.Length * 3 + IsFinal.Length + 4L * ChunkReleaseStep.Length
                            + (Network?.ByteSize ?? 0);
}
