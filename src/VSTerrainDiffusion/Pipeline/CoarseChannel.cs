namespace VSTerrainDiffusion.Pipeline;

/// <summary>Indices into a coarse model's output (and the coarse map, which adds a blend weight after the last).</summary>
public static class CoarseChannel
{
    /// <summary>Elevation, signed square-root metres.</summary>
    public const int Elevation = 0;

    /// <summary>Fifth percentile of elevation, signed square-root metres (the map stores elevation minus this).</summary>
    public const int ElevationP5 = 1;

    /// <summary>Mean annual temperature at the cell's elevation, °C (BIO1).</summary>
    public const int Temperature = 2;

    /// <summary>Standard deviation of monthly mean temperatures × 100 (BIO4).</summary>
    public const int TemperatureSeasonality = 3;

    /// <summary>Annual precipitation, mm (BIO12).</summary>
    public const int Precipitation = 4;

    /// <summary>Coefficient of variation of monthly precipitation, % (BIO15).</summary>
    public const int PrecipitationCv = 5;

    /// <summary>Second harmonic of the temperature cycle, cosine term, °C.</summary>
    public const int TemperatureShapeCos = 6;

    /// <summary>Second harmonic of the temperature cycle, sine term, °C.</summary>
    public const int TemperatureShapeSin = 7;

    /// <summary>
    /// First harmonic of precipitation relative to the monthly mean, cosine term: when the wet season falls.
    /// </summary>
    public const int WetSeasonCos = 8;

    /// <summary>First harmonic of precipitation relative to the monthly mean, sine term.</summary>
    public const int WetSeasonSin = 9;

    /// <summary>
    /// Second harmonic of precipitation relative to the monthly mean, cosine term: a second rainy season,
    /// sharp monsoons.
    /// </summary>
    public const int SecondRainyCos = 10;

    /// <summary>Second harmonic of precipitation relative to the monthly mean, sine term.</summary>
    public const int SecondRainySin = 11;

    /// <summary>Mean diurnal temperature range, °C (BIO2).</summary>
    public const int DiurnalRange = 12;

    /// <summary>The annual statistics every coarse model has: elevation, its fifth percentile and four climate channels.</summary>
    public const int AnnualCount = 6;

    /// <summary>The annual channels plus the six seasonal-cycle channels and the diurnal range.</summary>
    public const int SeasonalCount = 13;
}
