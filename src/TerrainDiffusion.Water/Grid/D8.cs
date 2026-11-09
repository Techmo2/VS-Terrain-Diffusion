using System;

namespace TerrainDiffusion.Water.Grid;

/// <summary>
/// The eight-neighbour (D8) steps of the cell grid, and the direction codes a cell's water can carry.
/// </summary>
/// <remarks>
/// Codes 0 to 7 name the neighbour a cell's water steps to, numbered row by row around the cell:
/// <code>
///     0 1 2
///     3 . 4
///     5 6 7
/// </code>
/// Rows grow along world Z and columns along world X, the same as every array in this library. A heading is an
/// angle <c>atan2(rowStep, columnStep)</c> in radians: 0 points along +X (code 4), π/2 along +Z (code 6).
/// Two further codes mark cells whose water goes nowhere: <see cref="Sink"/> and <see cref="Escape"/>.
/// </remarks>
public static class D8
{
    /// <summary>Number of neighbours of a cell.</summary>
    public const int NeighbourCount = 8;

    /// <summary>Direction code of a cell where water ends: the sea, or a lake with no outlet.</summary>
    public const sbyte Sink = 8;

    /// <summary>
    /// Direction code of a cell whose flat (a lake surface) runs out of the window it was routed in. Only seen
    /// while routing: a tile that still has such cells after the widest window turns them into <see cref="Sink"/>.
    /// </summary>
    public const sbyte Escape = 9;

    /// <summary>Marker of a cell not decided yet while directions are being worked out.</summary>
    internal const sbyte Undecided = -1;

    /// <summary>Row step of each direction code (backing <see cref="RowStep"/>).</summary>
    private static readonly int[] RowSteps = { -1, -1, -1, 0, 0, 1, 1, 1 };
    /// <summary>Column step of each direction code (backing <see cref="ColumnStep"/>).</summary>
    private static readonly int[] ColumnSteps = { -1, 0, 1, -1, 1, -1, 0, 1 };

    /// <summary>The direction code at each heading 0, π/4, ..., 7π/4 (one per octant, counter-clockwise in
    /// (column, row) terms).</summary>
    private static readonly int[] CodesByOctant = { 4, 7, 6, 5, 3, 0, 1, 2 };

    /// <summary>Heading of each direction code (backing <see cref="Heading"/>).</summary>
    private static readonly double[] Headings = BuildHeadings();

    /// <summary>Row step (along Z) of each direction code 0-7.</summary>
    public static ReadOnlySpan<int> RowStep => RowSteps;

    /// <summary>Column step (along X) of each direction code 0-7.</summary>
    public static ReadOnlySpan<int> ColumnStep => ColumnSteps;

    /// <summary>The direction code whose heading is <c>octant · π/4</c>, for octant 0-7.</summary>
    public static ReadOnlySpan<int> CodeByOctant => CodesByOctant;

    /// <summary>Heading (radians, <c>atan2(rowStep, columnStep)</c>) of each direction code 0-7.</summary>
    public static ReadOnlySpan<double> Heading => Headings;

    /// <summary>Whether <paramref name="code"/> names a neighbour (0-7) rather than <see cref="Sink"/> or
    /// <see cref="Escape"/>.</summary>
    /// <param name="code">A direction code.</param>
    /// <returns>True for a step to a neighbour.</returns>
    public static bool IsStep(int code) => (uint)code < NeighbourCount;

    /// <summary><c>atan2(rowStep, columnStep)</c> of each direction code.</summary>
    private static double[] BuildHeadings()
    {
        var headings = new double[NeighbourCount];
        for (int code = 0; code < NeighbourCount; code++)
        {
            headings[code] = Math.Atan2(RowSteps[code], ColumnSteps[code]);
        }
        return headings;
    }
}
