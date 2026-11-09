using System;

namespace TerrainDiffusion.Water;

/// <summary>
/// How far rivers wander from straight downhill, and where the terrain lets them (see
/// <see cref="Meander.MeanderField"/>). The defaults are the conditioning lab's.
/// </summary>
/// <remarks>
/// The target sinuosity of a cell is
/// <code>
///   S = 1 + (MaxSinuosity − 1) / (1 + (slope / SlopeScale)²) / (1 + (relief / ReliefScaleMetres)²)
/// </code>
/// so a wide, gentle plain gets <see cref="MaxSinuosity"/>, and steep or confined ground gets about 1.
/// </remarks>
public sealed class MeanderSettings
{
    /// <summary>Sinuosity (path length over straight distance) of a river on a wide, flat plain. 1 turns
    /// meandering off; real lowland rivers are 1.5-3.</summary>
    public double MaxSinuosity { get; init; } = 1.9;

    /// <summary>Wavelength in metres of the swing field: about the distance between successive bends.</summary>
    public double WavelengthMetres { get; init; } = 600.0;

    /// <summary>Gradient (metres per metre) at which the target sinuosity's excess over 1 is halved.</summary>
    public double SlopeScale { get; init; } = 0.015;

    /// <summary>R.m.s. local relief in metres at which the target sinuosity's excess over 1 is halved.</summary>
    public double ReliefScaleMetres { get; init; } = 4.0;

    /// <summary>Distance in metres over which relief counts as confining (the smoothing scale of the relief
    /// measure): valley walls, ridges and hills within it hold a river in.</summary>
    public double ConfinementMetres { get; init; } = 240.0;

    /// <summary>Relief, as a multiple of <see cref="ReliefScaleMetres"/>, above which a cell keeps plain
    /// steepest descent (the ground holds the water in its channels).</summary>
    public double FreeReliefRatio { get; init; } = 2.0;

    /// <summary>Number of plane waves summed into the swing field.</summary>
    public int WaveCount { get; init; } = 9;

    /// <summary>Throws if any setting is out of range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    public void Validate()
    {
        Require(MaxSinuosity >= 1.0 && MaxSinuosity <= 10.0, nameof(MaxSinuosity), MaxSinuosity, "1-10");
        Require(WavelengthMetres > 0.0 && double.IsFinite(WavelengthMetres), nameof(WavelengthMetres), WavelengthMetres, "positive");
        Require(SlopeScale > 0.0 && double.IsFinite(SlopeScale), nameof(SlopeScale), SlopeScale, "positive");
        Require(ReliefScaleMetres > 0.0 && double.IsFinite(ReliefScaleMetres), nameof(ReliefScaleMetres), ReliefScaleMetres, "positive");
        Require(ConfinementMetres > 0.0 && double.IsFinite(ConfinementMetres), nameof(ConfinementMetres), ConfinementMetres, "positive");
        Require(FreeReliefRatio > 0.0 && double.IsFinite(FreeReliefRatio), nameof(FreeReliefRatio), FreeReliefRatio, "positive");
        Require(WaveCount >= 1 && WaveCount <= 64, nameof(WaveCount), WaveCount, "1-64");
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for setting <paramref name="name"/> unless
    /// <paramref name="ok"/>.</summary>
    private static void Require(bool ok, string name, object value, string range)
    {
        if (!ok)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be {range}.");
        }
    }
}
