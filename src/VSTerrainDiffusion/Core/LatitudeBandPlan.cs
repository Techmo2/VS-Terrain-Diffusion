namespace VSTerrainDiffusion.Core;

/// <summary>How one latitude's climate is divided between the model and its output.</summary>
public readonly struct LatitudeBandPlan
{
    /// <summary>Mean annual temperature to condition the model on, in Celsius.</summary>
    public readonly float ConditioningTemperatureC;

    /// <summary>Annual rainfall to condition the model on, in millimetres.</summary>
    public readonly float ConditioningPrecipitationMm;

    /// <summary>
    /// Degrees the band asked for that the conditioning could not reach, added to the model's
    /// output. Additive rather than multiplicative because what runs out at the cold end is the
    /// climate distribution itself - the model has never seen a place colder than about -7 C mean
    /// annual - and an offset moves a belt without flattening the variation inside it.
    /// </summary>
    public readonly float TemperatureOffsetC;

    /// <summary>The same for rainfall, as a multiplier.</summary>
    public readonly float RainfallFactor;

    /// <summary>A plan from its four parts.</summary>
    /// <param name="conditioningTemperatureC">See <see cref="ConditioningTemperatureC"/>.</param>
    /// <param name="conditioningPrecipitationMm">See <see cref="ConditioningPrecipitationMm"/>.</param>
    /// <param name="temperatureOffsetC">See <see cref="TemperatureOffsetC"/>.</param>
    /// <param name="rainfallFactor">See <see cref="RainfallFactor"/>.</param>
    public LatitudeBandPlan(float conditioningTemperatureC, float conditioningPrecipitationMm,
                            float temperatureOffsetC, float rainfallFactor)
    {
        ConditioningTemperatureC = conditioningTemperatureC;
        ConditioningPrecipitationMm = conditioningPrecipitationMm;
        TemperatureOffsetC = temperatureOffsetC;
        RainfallFactor = rainfallFactor;
    }
}
