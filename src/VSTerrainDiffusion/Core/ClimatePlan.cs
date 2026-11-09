namespace VSTerrainDiffusion.Core;

/// <summary>
/// How one world's climate settings are divided between the model and its output.
///
/// A return value rather than state: both halves come from the same call so that the warp the
/// model is given and the correction applied afterwards can never disagree about who is doing
/// what.
/// </summary>
public readonly struct ClimatePlan
{
    /// <summary>Rank warp for the conditioning temperature channel; 1 leaves it alone.</summary>
    public readonly float TemperatureExponent;

    /// <summary>Scale for the conditioning precipitation channel; 1 leaves it alone.</summary>
    public readonly float PrecipitationScale;

    /// <summary>What is left of <c>globalTemperature</c> once the model has had its share.</summary>
    public readonly float TemperatureCorrection;

    /// <summary>What is left of <c>globalPrecipitation</c>.</summary>
    public readonly float RainfallCorrection;

    /// <summary>A plan from its four parts.</summary>
    /// <param name="temperatureExponent">See <see cref="TemperatureExponent"/>.</param>
    /// <param name="precipitationScale">See <see cref="PrecipitationScale"/>.</param>
    /// <param name="temperatureCorrection">See <see cref="TemperatureCorrection"/>.</param>
    /// <param name="rainfallCorrection">See <see cref="RainfallCorrection"/>.</param>
    public ClimatePlan(float temperatureExponent, float precipitationScale,
                       float temperatureCorrection, float rainfallCorrection)
    {
        TemperatureExponent = temperatureExponent;
        PrecipitationScale = precipitationScale;
        TemperatureCorrection = temperatureCorrection;
        RainfallCorrection = rainfallCorrection;
    }

    /// <summary>
    /// The plan for a world the model is told nothing about: it draws its ordinary climate and the
    /// whole of both settings is applied to the result, which is vanilla's arrangement and this
    /// mod's own before the settings became conditioning.
    /// </summary>
    public static ClimatePlan Unconditioned(ClimateShift climate)
        => new(1f, 1f, climate.Temperature, climate.Precipitation);
}
