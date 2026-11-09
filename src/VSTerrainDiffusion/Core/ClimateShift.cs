using System;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// The world's "Global temperature" and "Global precipitation" settings, on their way into the
/// model.
///
/// Vanilla applies both as plain multipliers on the climate map it draws at random, which is
/// available to this mod too and is what it used to do: generate an ordinary world and scale the
/// numbers on the way out. That produces the right readings and the wrong world — a "hyperarid"
/// planet whose forests, soils and snow line are all still those of a temperate one, because
/// nothing upstream of the climate map ever heard about the setting.
///
/// So the setting is handed to the model instead, as a bias on the climate it is conditioned on,
/// and only the part the model cannot reach is applied to its output afterwards. The division is
/// worked out in <see cref="ClimatePlan"/>.
///
/// Stored as displacements from neutral so that <c>default</c> is a world with both settings left
/// alone: a struct that defaulted to zero multipliers would quietly freeze any world whose shift
/// someone forgot to pass on.
/// </summary>
public readonly struct ClimateShift
{
    /// <summary>
    /// Bottom of Vintage Story's temperature scale, in Celsius. Its climate map stores temperature
    /// as one byte spanning this to +40 C, so its multiplier scales degrees above -20 rather than
    /// degrees above freezing: "Snowball earth" (0.25x) turns a 14 C place into -11.5 C, not into
    /// 3.5 C, and it is the first of those that the setting's name promises.
    /// </summary>
    public const float ScaleFloorC = -20f;

    /// <summary>Warmest temperature the game's climate byte can hold, in Celsius.</summary>
    public const float ScaleCeilingC = 40f;

    /// <summary><see cref="Temperature"/> minus one.</summary>
    private readonly float _temperatureFromNeutral;

    /// <summary><see cref="Precipitation"/> minus one.</summary>
    private readonly float _precipitationFromNeutral;

    /// <summary>A shift from the two settings' multipliers.</summary>
    /// <param name="temperature"><c>globalTemperature</c>; 1 is neutral.</param>
    /// <param name="precipitation"><c>globalPrecipitation</c>; 1 is neutral.</param>
    public ClimateShift(float temperature, float precipitation)
    {
        _temperatureFromNeutral = temperature - 1f;
        _precipitationFromNeutral = precipitation - 1f;
    }

    /// <summary>Vanilla's <c>globalTemperature</c>: scales temperature measured from -20 C.</summary>
    public float Temperature => 1f + _temperatureFromNeutral;

    /// <summary>Vanilla's <c>globalPrecipitation</c>: scales annual rainfall.</summary>
    public float Precipitation => 1f + _precipitationFromNeutral;

    /// <summary>A world running both settings at their defaults.</summary>
    public static ClimateShift None => default;

    /// <summary>Whether both settings are at their defaults.</summary>
    public bool IsNeutral => _temperatureFromNeutral == 0f && _precipitationFromNeutral == 0f;

    /// <summary>Applies a temperature multiplier the way Vintage Story's climate byte does.</summary>
    public static float ApplyTemperature(float celsius, float multiplier)
        => (celsius - ScaleFloorC) * multiplier + ScaleFloorC;

    /// <summary>Both multipliers, for logs.</summary>
    public override string ToString()
        => $"temperature {Temperature:0.##}x, precipitation {Precipitation:0.##}x";
}
