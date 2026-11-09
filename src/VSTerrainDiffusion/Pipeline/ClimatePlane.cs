namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Planes of <see cref="WorldPipeline.Sample.Climate"/>, in the reference pipeline's native climate
/// order: <c>[T, Tseas, P, Pcv, β, t2c, t2s, p1c, p1s, p2c, p2s, DTR]</c>. The first five exist for
/// every coarse model; the rest only with <see cref="CoarseModelSpec.HasSeasonalCycle"/>.
/// </summary>
public static class ClimatePlane
{
    /// <summary>Mean annual temperature at the column's elevation, °C.</summary>
    public const int Temperature = 0;

    /// <summary>Temperature seasonality, as <see cref="CoarseChannel.TemperatureSeasonality"/>.</summary>
    public const int TemperatureSeasonality = 1;

    /// <summary>Annual precipitation, mm.</summary>
    public const int Precipitation = 2;

    /// <summary>Precipitation seasonality, as <see cref="CoarseChannel.PrecipitationCv"/>.</summary>
    public const int PrecipitationCv = 3;

    /// <summary>The local lapse rate β, °C per metre, including the world's altitude cooling.</summary>
    public const int LapseRate = 4;

    /// <summary>
    /// Second harmonic of the temperature cycle, cosine; see <see cref="CoarseChannel.TemperatureShapeCos"/>.
    /// </summary>
    public const int TemperatureShapeCos = 5;

    /// <summary>Second harmonic of the temperature cycle, sine.</summary>
    public const int TemperatureShapeSin = 6;

    /// <summary>When the wet season falls, cosine; see <see cref="CoarseChannel.WetSeasonCos"/>.</summary>
    public const int WetSeasonCos = 7;

    /// <summary>When the wet season falls, sine.</summary>
    public const int WetSeasonSin = 8;

    /// <summary>A second rainy season, cosine; see <see cref="CoarseChannel.SecondRainyCos"/>.</summary>
    public const int SecondRainyCos = 9;

    /// <summary>A second rainy season, sine.</summary>
    public const int SecondRainySin = 10;

    /// <summary>Mean diurnal temperature range, °C.</summary>
    public const int DiurnalRange = 11;

    /// <summary>The number of planes a pipeline with this coarse model fills.</summary>
    /// <returns>Five for the annual model, twelve with the seasonal cycle.</returns>
    public static int CountFor(CoarseModelSpec spec) => spec.HasSeasonalCycle ? 12 : 5;
}
