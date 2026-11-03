using System;

namespace VSTerrainDiffusion.Api;

/// <summary>
/// The climate of one block column as Terrain Diffusion generated it. The four annual figures
/// include the world's climate settings (temperature shift, latitude bands, rainfall), so they are
/// what the game uses; the seasonal-cycle figures are the model's own.
/// </summary>
public readonly struct ClimateRecord
{
    /// <summary>Annual mean temperature at the ground, °C (BIO1).</summary>
    public readonly float TemperatureC;

    /// <summary>Temperature seasonality: SD of monthly means × 100 (BIO4).</summary>
    public readonly float TemperatureSeasonality;

    /// <summary>Annual precipitation, mm (BIO12).</summary>
    public readonly float PrecipitationMm;

    /// <summary>Precipitation seasonality: coefficient of variation of monthly rain, % (BIO15).</summary>
    public readonly float PrecipitationCv;

    /// <summary>Ground elevation above sea level, metres (negative under the sea).</summary>
    public readonly float ElevationMeters;

    /// <summary>
    /// True when the world's coarse model predicts the seasonal cycle and diurnal range. False on a
    /// world made with the annual model: the cycle below is then an approximation (rain peaking in
    /// summer, as strongly as <see cref="PrecipitationCv"/> says; a symmetric temperature year) and
    /// <see cref="DiurnalRangeC"/> is NaN.
    /// </summary>
    public readonly bool HasSeasonalCycle;

    /// <summary>Second harmonic of the temperature year, °C: short sharp or long flat summers.</summary>
    public readonly float TemperatureShapeCos, TemperatureShapeSin;

    /// <summary>First harmonic of rain relative to the monthly mean: when the wet season falls (cos 1 = midsummer).</summary>
    public readonly float WetSeasonCos, WetSeasonSin;

    /// <summary>Second harmonic of rain relative to the monthly mean: a second rainy season, sharp monsoons.</summary>
    public readonly float SecondRainyCos, SecondRainySin;

    /// <summary>Mean diurnal temperature range, °C (BIO2): the usual day-night swing. NaN without the seasonal model.</summary>
    public readonly float DiurnalRangeC;

    public ClimateRecord(float temperatureC, float temperatureSeasonality, float precipitationMm, float precipitationCv,
                         float elevationMeters, bool hasSeasonalCycle, ReadOnlySpan<float> cycle, float diurnalRangeC)
    {
        TemperatureC = temperatureC;
        TemperatureSeasonality = temperatureSeasonality;
        PrecipitationMm = precipitationMm;
        PrecipitationCv = precipitationCv;
        ElevationMeters = elevationMeters;
        HasSeasonalCycle = hasSeasonalCycle;
        TemperatureShapeCos = cycle[0];
        TemperatureShapeSin = cycle[1];
        WetSeasonCos = cycle[2];
        WetSeasonSin = cycle[3];
        SecondRainyCos = cycle[4];
        SecondRainySin = cycle[5];
        DiurnalRangeC = diurnalRangeC;
    }

    /// <summary>Under the sea: no land climate. <see cref="KoppenClass"/> is then <see cref="Koppen.Water"/>.</summary>
    public bool IsWater => ElevationMeters <= 0f;

    /// <summary>The six cycle channels in the reference's order: t2c, t2s, p1c, p1s, p2c, p2s.</summary>
    public double[] Cycle() => new double[]
    {
        TemperatureShapeCos, TemperatureShapeSin, WetSeasonCos, WetSeasonSin, SecondRainyCos, SecondRainySin
    };

    /// <summary>
    /// Twelve monthly temperatures and rainfalls. Month 0 is the first month after midsummer: the
    /// model has no calendar, so its year runs from each place's own warm season.
    /// </summary>
    public MonthlyClimate Months() =>
        MonthlyClimate.Reconstruct(TemperatureC, TemperatureSeasonality, PrecipitationMm, Cycle());

    /// <summary>The hard Köppen class, an index into <see cref="Koppen.Classes"/>, or <see cref="Koppen.Water"/>.</summary>
    public int KoppenClass => IsWater ? Koppen.Water : Koppen.Classify(TemperatureC, PrecipitationMm, Months());

    /// <summary>
    /// A weight per Köppen class (length <see cref="Koppen.Count"/>) summing to one, softened across
    /// class boundaries by <paramref name="softness"/> (<see cref="Koppen.Softness.Default"/> when
    /// null). All zero under the sea.
    /// </summary>
    public float[] KoppenMemberships(Koppen.Softness? softness = null)
    {
        var weights = new float[Koppen.Count];
        if (!IsWater) Koppen.Memberships(TemperatureC, PrecipitationMm, Months(), softness ?? Koppen.Softness.Default, weights);
        return weights;
    }

    /// <summary>
    /// The cycle an annual-model world implies: no shape to the temperature year, and rain peaking
    /// at midsummer with the spread BIO15 gives (a cosine of amplitude a has a CV of a / √2).
    /// </summary>
    internal static void ApproximateCycle(float precipitationMm, float precipitationCv, Span<float> cycle)
    {
        cycle.Clear();
        double monthlyMean = Math.Max(precipitationMm, 0f) / 12.0;
        cycle[2] = (float)(Math.Sqrt(2.0) * precipitationCv / 100.0 * monthlyMean
                           / (monthlyMean + MonthlyClimate.PrecipitationEpsilon));
    }
}
