using System;

namespace VSTerrainDiffusion.Api;

/// <summary>
/// Twelve monthly temperatures and precipitations rebuilt from the annual statistics and the six
/// seasonal-cycle channels: a port of the reference's <c>seasonality.reconstruct_monthly</c>.
///
/// The model has no calendar or hemispheres, so the year is measured from each place's own warm
/// season: <b>month 0 is the first month after midsummer</b>, and the temperature cycle's first
/// harmonic peaks at the boundary between months 11 and 0. Two harmonics make a smooth curve, so a
/// one-month spike of rain comes out spread over two or three months.
/// </summary>
public readonly struct MonthlyClimate
{
    /// <summary>Added to the monthly mean before dividing, so near-rainless deserts don't get huge relative amplitudes.</summary>
    public const double PrecipitationEpsilon = 5.0;

    /// <summary>Mean temperature of each month, °C.</summary>
    public readonly double[] TemperatureC;

    /// <summary>Precipitation of each month, mm; sums to the annual total.</summary>
    public readonly double[] PrecipitationMm;

    /// <summary>Wraps the twelve months; use <see cref="Reconstruct"/>.</summary>
    /// <param name="temperatureC">Monthly mean temperatures, °C.</param>
    /// <param name="precipitationMm">Monthly precipitation, mm.</param>
    private MonthlyClimate(double[] temperatureC, double[] precipitationMm)
    {
        TemperatureC = temperatureC;
        PrecipitationMm = precipitationMm;
    }

    /// <summary>Angle of month m's midpoint: θ_m = 2π(m + 0.5) / 12.</summary>
    public static double MonthTheta(int month) => 2.0 * Math.PI * (month + 0.5) / 12.0;

    /// <summary>
    /// Rebuilds the twelve months from the annual statistics and the cycle channels, as the
    /// reference pipeline does: temperature from two harmonics whose variance matches the
    /// seasonality, and rain from two harmonics around the monthly mean, clipped at zero and
    /// rescaled to the annual total.
    /// </summary>
    /// <param name="meanC">Annual mean temperature, °C.</param>
    /// <param name="seasonality">Temperature seasonality as the model gives it: SD of monthly means times 100 (BIO4).</param>
    /// <param name="annualMm">Annual precipitation, mm.</param>
    /// <param name="cycle">The six cycle channels: t2c, t2s, p1c, p1s, p2c, p2s.</param>
    /// <returns>The months, from month 0: the first month after midsummer.</returns>
    public static MonthlyClimate Reconstruct(double meanC, double seasonality, double annualMm, ReadOnlySpan<double> cycle)
    {
        double sd = Math.Max(seasonality, 0.0) / 100.0;
        double annual = Math.Max(annualMm, 0.0);
        double t2c = cycle[0], t2s = cycle[1], p1c = cycle[2], p1s = cycle[3], p2c = cycle[4], p2s = cycle[5];

        // SD² = (A1² + A2²) / 2, so the first harmonic's amplitude is what the second leaves of it.
        double a1 = Math.Sqrt(Math.Max(0.0, 2.0 * sd * sd - t2c * t2c - t2s * t2s));
        double monthlyMean = annual / 12.0;

        var t = new double[12];
        var p = new double[12];
        double sum = 0.0;
        for (int m = 0; m < 12; m++)
        {
            double th = MonthTheta(m);
            double c1 = Math.Cos(th), s1 = Math.Sin(th), c2 = Math.Cos(2.0 * th), s2 = Math.Sin(2.0 * th);
            t[m] = meanC + a1 * c1 + t2c * c2 + t2s * s2;
            double pm = monthlyMean + (monthlyMean + PrecipitationEpsilon) * (p1c * c1 + p1s * s1 + p2c * c2 + p2s * s2);
            p[m] = Math.Max(pm, 0.0);
            sum += p[m];
        }

        double rescale = annual / Math.Max(sum, 1e-9);
        for (int m = 0; m < 12; m++) p[m] *= rescale;
        return new MonthlyClimate(t, p);
    }
}
