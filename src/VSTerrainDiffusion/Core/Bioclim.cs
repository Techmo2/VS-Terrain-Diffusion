using System;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Turns the model's four WorldClim variables into the ecological quantities that actually decide
/// what grows somewhere.
///
/// The model predicts BIO1 (annual mean temperature), BIO4 (temperature seasonality), BIO12
/// (annual precipitation) and BIO15 (precipitation seasonality). None of those is directly what a
/// plant cares about: 800 mm of rain is generous in Lapland and semi-arid in the Sahel, and a mean
/// of 5 C is a pleasant montane climate if it is 5 C all year and a brutal one if it swings forty
/// degrees. The derived quantities here - potential evapotranspiration, aridity, growing season -
/// combine them the way climatologists do, and are what the rest of the mod keys off.
///
/// The formulas are ported from <c>_compute_climate_vars</c> in the Terrain Diffusion reference
/// implementation (<c>terrain_diffusion/inference/minecraft_api.py</c>) so that a place classified
/// as savanna there is classified the same way here.
/// </summary>
public readonly struct Bioclim
{
    /// <summary>Annual mean temperature at the surface, degrees Celsius. WorldClim BIO1.</summary>
    public readonly float MeanTemperatureC;

    /// <summary>Standard deviation of monthly mean temperature, degrees Celsius. BIO4 / 100.</summary>
    public readonly float TemperatureSigmaC;

    /// <summary>Annual precipitation, millimetres. WorldClim BIO12.</summary>
    public readonly float PrecipitationMm;

    /// <summary>Precipitation coefficient of variation across the year, percent. WorldClim BIO15.</summary>
    public readonly float PrecipitationCv;

    /// <summary>Potential evapotranspiration, millimetres per year: how much water the climate can lift.</summary>
    public readonly float PotentialEvapotranspirationMm;

    /// <summary>
    /// Precipitation over potential evapotranspiration. Above 1 is humid, 0.2 to 0.5 semi-arid,
    /// below 0.2 arid. This, not rainfall, is what makes ground bare.
    /// </summary>
    public readonly float AridityIndex;

    /// <summary>
    /// Aridity discounted for a pronounced dry season, which trees feel even when the annual total
    /// looks generous - a monsoon forest is markedly less productive than an equatorial one.
    /// </summary>
    public readonly float TreeMoisture;

    /// <summary>Days per year above 5 C, from a sinusoidal fit to the annual temperature cycle.</summary>
    public readonly float GrowingSeasonDays;

    /// <summary>
    /// Mean temperature of the coldest month. A sine of amplitude A has standard deviation
    /// A / sqrt(2), so a seasonality of sigma puts midwinter sqrt(2) sigma below the annual mean —
    /// the same figure <see cref="WorldGen.DiffusionSeasons"/> actually simulates, so what this
    /// says about a place is what the player will feel there.
    /// </summary>
    public readonly float ColdestMonthC;

    /// <summary>Mean temperature of the warmest month, sqrt(2) sigma above the annual mean.</summary>
    public readonly float WarmestMonthC;

    /// <summary>
    /// <see cref="TreeMoisture"/> scaled by how much of the year is warm enough to grow in. Zero
    /// where the growing season is under 60 days, whatever the rainfall.
    /// </summary>
    public readonly float EffectiveTreeMoisture;

    /// <summary>Days below which nothing woody establishes.</summary>
    private const float MinimumGrowingSeasonDays = 60f;

    /// <summary>Days above which the growing season stops being the limiting factor.</summary>
    private const float FullGrowingSeasonDays = 150f;

    /// <summary>Amplitude of the annual temperature cycle per unit of seasonality: sqrt(2).</summary>
    private const float SeasonalAmplitude = 1.4142136f;

    public Bioclim(float meanTemperatureC, float temperatureSeasonality, float precipitationMm, float precipitationCv)
    {
        MeanTemperatureC = meanTemperatureC;
        TemperatureSigmaC = Math.Max(0f, temperatureSeasonality) / 100f;
        PrecipitationMm = Math.Max(0f, precipitationMm);
        PrecipitationCv = Math.Max(0f, precipitationCv);

        // Thornthwaite-style quadratic in an "effective" temperature: evaporation is concentrated
        // in the warm half of the year, so a continental climate evaporates more than its annual
        // mean suggests.
        float effectiveTemperature = Math.Max(0f, MeanTemperatureC + 0.5f * TemperatureSigmaC);
        PotentialEvapotranspirationMm = Math.Max(250f,
            250f + 25f * effectiveTemperature + 0.7f * effectiveTemperature * effectiveTemperature);

        AridityIndex = PrecipitationMm / Math.Max(1f, PotentialEvapotranspirationMm);
        TreeMoisture = AridityIndex * (1f - 0.35f * Math.Min(1f, PrecipitationCv / 100f));

        GrowingSeasonDays = DaysAbove(5f, MeanTemperatureC, TemperatureSigmaC);
        ColdestMonthC = MeanTemperatureC - SeasonalAmplitude * TemperatureSigmaC;
        WarmestMonthC = MeanTemperatureC + SeasonalAmplitude * TemperatureSigmaC;

        float seasonFactor = Clamp01(
            (GrowingSeasonDays - MinimumGrowingSeasonDays) / (FullGrowingSeasonDays - MinimumGrowingSeasonDays));
        EffectiveTreeMoisture = TreeMoisture * seasonFactor;
    }

    /// <summary>
    /// Days per year above <paramref name="threshold"/>, assuming temperature follows a sine wave
    /// with the given mean and standard deviation. A sine of amplitude A has sd A/sqrt(2), so the
    /// fraction of the cycle above a level follows directly from an arcsine.
    /// </summary>
    private static float DaysAbove(float threshold, float mean, float sigma)
    {
        float amplitude = sigma * 1.414f;
        if (amplitude < 0.1f) return mean > threshold ? 365f : 0f;

        float x = (threshold - mean) / amplitude;
        if (x <= -1f) return 365f;
        if (x >= 1f) return 0f;
        return 365f * (0.5f - (float)Math.Asin(Math.Clamp(x, -1f, 1f)) / (float)Math.PI);
    }

    /// <summary>
    /// Slope, as a rise over run, above which soil stops holding onto a hillside. Roots bind soil,
    /// so a rainforest gorge stays green at an angle that would leave a desert canyon bare rock:
    /// 35 degrees where nothing grows, up to 50 degrees where everything does.
    /// </summary>
    public float BareSlopeThreshold
    {
        get
        {
            const float arid = 0.7f;   // tan 35 degrees
            const float humid = 1.19f; // tan 50 degrees
            return arid + (humid - arid) * Clamp01((TreeMoisture - 0.35f) / 0.45f);
        }
    }

    /// <summary>
    /// Fraction of the ground trees would cover, 0 to 1, before slope is taken into account.
    ///
    /// The reference implementation sorts effective moisture into five bands - none, sparse,
    /// forest, dense, rainforest - to pick a Minecraft biome. Vintage Story wants a continuous
    /// density instead, so the band edges become the knots of a piecewise-linear curve and places
    /// inside a band vary smoothly rather than snapping.
    /// </summary>
    public float ForestDensity
    {
        get
        {
            float m = EffectiveTreeMoisture;
            if (m < 0.2f) return 0f;
            if (m < 0.5f) return Lerp(0.05f, 0.30f, (m - 0.2f) / 0.3f);
            if (m < 0.8f) return Lerp(0.30f, 0.58f, (m - 0.5f) / 0.3f);
            if (m < 1.3f) return Lerp(0.58f, 0.82f, (m - 0.8f) / 0.5f);
            return Math.Min(1f, Lerp(0.82f, 1f, (m - 1.3f) / 0.7f));
        }
    }

    /// <summary>
    /// Fraction of the ground shrubs would cover. Shrubland is what sits between grassland and
    /// closed forest: scrub peaks where there is enough water for woody plants but not enough for
    /// a canopy, and thins out again under one.
    /// </summary>
    public float ShrubDensity
    {
        get
        {
            float m = EffectiveTreeMoisture;
            if (m < 0.08f) return 0f;
            if (m < 0.45f) return Lerp(0.05f, 0.65f, (m - 0.08f) / 0.37f);
            if (m < 1.0f) return Lerp(0.65f, 0.25f, (m - 0.45f) / 0.55f);
            return 0.25f;
        }
    }

    /// <summary>
    /// True where the warmest month stays below freezing and there is enough precipitation to
    /// build up: an ice cap rather than a summit that merely gets snowy in winter.
    /// </summary>
    public bool IsPermanentIce => WarmestMonthC < 0f && PrecipitationMm > 150f;

    private static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
