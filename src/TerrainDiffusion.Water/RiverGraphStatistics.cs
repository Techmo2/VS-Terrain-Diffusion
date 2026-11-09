namespace TerrainDiffusion.Water;

/// <summary>A snapshot of what a <see cref="RiverGraph"/> has done so far.</summary>
public sealed class RiverGraphStatistics
{
    /// <summary>Tile arrivals so far (<see cref="RiverGraph.Step"/>).</summary>
    public int Step { get; init; }

    /// <summary>Tiles held.</summary>
    public int TilesKnown { get; init; }

    /// <summary>Tiles with their fill.</summary>
    public int TilesFilled { get; init; }

    /// <summary>Tiles with directions.</summary>
    public int TilesRouted { get; init; }

    /// <summary>Tiles whose flats needed 5 × 5 tiles of fill.</summary>
    public int TilesWide { get; init; }

    /// <summary>Filled cells whose level was decided only by the edge of their fill window, even the widest
    /// allowed (<see cref="RiverGraphSettings.MaxFillRadius"/>): where a lake may still end at a tile seam.
    /// Every other level is the one a fill of the whole world would give.</summary>
    public long UncertainCells { get; init; }

    /// <summary>Tiles whose fill needed more than their 8 neighbours, to see a lake whole.</summary>
    public int TilesFillWidened { get; init; }

    /// <summary>Lakes without an outlet found while routing.</summary>
    public long TerminalFlats { get; init; }

    /// <summary>Cells of flats wider than 5 × 5 tiles, made sinks.</summary>
    public long EscapedCells { get; init; }

    /// <summary>Discharge sweeps of a tile.</summary>
    public long Sweeps { get; init; }

    /// <summary>Sweeps that would have changed a final cell (never expected: 0).</summary>
    public long Violations { get; init; }

    /// <summary>Mean target sinuosity over the routed land cells; null before any (or without meandering).</summary>
    public double? MeanSinuosity { get; init; }

    /// <summary>Wall time spent filling, milliseconds.</summary>
    public double FillMilliseconds { get; init; }

    /// <summary>Wall time spent routing (directions and headings), milliseconds.</summary>
    public double RouteMilliseconds { get; init; }

    /// <summary>Time spent on meander headings, summed over threads, milliseconds.</summary>
    public double MeanderMilliseconds { get; init; }

    /// <summary>Wall time spent on discharge and release, milliseconds.</summary>
    public double DischargeMilliseconds { get; init; }

    /// <summary>Wall time of the last <see cref="RiverGraph.AddTile"/>, milliseconds.</summary>
    public double LastAddMilliseconds { get; init; }

    /// <summary>Approximate memory held by the tiles, bytes.</summary>
    public long Bytes { get; init; }
}
