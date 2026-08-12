using System;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Maps the model's moisture onto Vintage Story's 0-255 rainfall byte.
///
/// The two quantities mean different things. The model's outputs are physical: millimetres per
/// year, and an aridity index that measures those millimetres against how much water the climate
/// can evaporate. Vintage Story's rainfall byte is not a measurement at all — vanilla draws it
/// <em>uniformly</em> over 0-255, and every threshold that reads it (the level above which ground
/// stops being bare gravel, the fertility curve that decides whether soil forms, the rainfall bands
/// on every tree and block patch) was tuned against that uniform spread.
///
/// So the honest conversion is a quantile map rather than a physical one: push the model's
/// distribution through its own CDF and the result comes out uniform, which is exactly the input
/// the rest of the game expects. An ordinary landscape then reads as ordinary instead of as
/// near-desert, while genuinely arid ground still lands at the dry end where it belongs.
///
/// Which quantity gets mapped is the interesting choice. Raw precipitation treats 800 mm in
/// Lapland and 800 mm in the Sahel as the same place, which is how a boreal forest ends up
/// classified as scrub. Tree moisture — aridity, discounted for a pronounced dry season — is what
/// vegetation actually responds to, and is the default.
/// </summary>
public sealed class RainfallScale
{
    private readonly bool _useMoisture;
    private readonly float _logMedian;
    private readonly float _sigma;
    private readonly float _bias;

    private RainfallScale(bool useMoisture, float median, float sigma, float bias)
    {
        _useMoisture = useMoisture;
        _logMedian = (float)Math.Log(Math.Max(1e-4f, median));
        _sigma = Math.Max(0.05f, sigma);
        _bias = bias;
    }

    public static RainfallScale FromConfig(WorldGenConfig config)
    {
        bool moisture = config.RainfallBasis != "precipitation";
        return new RainfallScale(
            moisture,
            moisture ? config.MoistureMedian : config.RainfallMedianMm,
            moisture ? config.MoistureSpread : config.RainfallSpread,
            config.RainfallBias);
    }

    /// <summary>Converts a column's climate to a 0-255 rainfall value.</summary>
    public int ToRainfall(in Bioclim climate)
        => ToRainfall(_useMoisture ? climate.TreeMoisture : climate.PrecipitationMm);

    /// <summary>Converts an already-selected moisture quantity to a 0-255 rainfall value.</summary>
    public int ToRainfall(float value)
    {
        double fraction = StandardNormalCdf((Math.Log(Math.Max(1e-4f, value)) - _logMedian) / _sigma);
        return (int)Math.Round(Math.Clamp((fraction + _bias) * 255.0, 0.0, 255.0));
    }

    /// <summary>Cumulative distribution of the standard normal.</summary>
    private static double StandardNormalCdf(double x) => 0.5 * Erfc(-x / Math.Sqrt(2.0));

    /// <summary>
    /// Complementary error function, Numerical Recipes' Chebyshev approximation. Accurate to about
    /// 1.2e-7 relative, which is far finer than a 1/255 output step.
    /// </summary>
    private static double Erfc(double x)
    {
        double z = Math.Abs(x);
        double t = 2.0 / (2.0 + z);
        double ty = 4.0 * t - 2.0;

        double[] coefficients =
        {
            -1.3026537197817094, 6.4196979235649026e-1, 1.9476473204185836e-2, -9.561514786808631e-3,
            -9.46595344482036e-4, 3.66839497852761e-4, 4.2523324806907e-5, -2.0278578112534e-5,
            -1.624290004647e-6, 1.303655835580e-6, 1.5626441722e-8, -8.5238095915e-8,
            6.529054439e-9, 5.059343495e-9, -9.91364156e-10, -2.27365122e-10,
            9.6467911e-11, 2.394038e-12, -6.886027e-12, 8.94487e-13,
            3.13092e-13, -1.12708e-13, 3.81e-16, 7.106e-15
        };

        double d = 0.0, dd = 0.0;
        for (int j = coefficients.Length - 1; j > 0; j--)
        {
            double tmp = d;
            d = ty * d - dd + coefficients[j];
            dd = tmp;
        }

        double result = t * Math.Exp(-z * z + 0.5 * (coefficients[0] + ty * d) - dd);
        return x >= 0.0 ? result : 2.0 - result;
    }
}
