using System;
using System.IO;
using Newtonsoft.Json;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// How a world's climate settings and latitude bands are split between the conditioning the model
/// is asked for and a correction applied to what it produces, from the model's measured response.
/// </summary>
public sealed partial class SyntheticMapFactory
{
    /// <summary>
    /// Bounds on the rank warp the climate settings are allowed to apply. Five squeezes the world
    /// into roughly the driest or coldest twentieth of the real distribution and a fifth into the
    /// wettest or hottest sixth, which is as far as a warp can go before it stops being a warp:
    /// past that it flattens the channel to one value and the model loses the variation that makes
    /// a climate a climate.
    /// </summary>
    private const float MinRankExponent = 0.2f;

    /// <summary>See <see cref="MinRankExponent"/>.</summary>
    private const float MaxRankExponent = 5f;

    /// <summary>
    /// How hard the model leans on a climate it is conditioned towards, as an exponent: shift the
    /// conditioning by a factor r and the world comes out shifted by about r to this power. The
    /// model amplifies rather than follows — a fifth of the rain in the conditioning is a seventh
    /// of the rain on the ground — so a setting handed straight to it arrives overdone, and these
    /// are what the request is divided by on the way in.
    ///
    /// Measured over 48x48 coarse cells on two seeds, comparing the median of the model's own land
    /// against the median it was conditioned towards, over the full range of both settings. With
    /// these values every setting the game offers lands within 2 C of what it asked for, and
    /// within 10% for rainfall in a region of ordinary wetness. Two places do worse and both do so
    /// by running out of world rather than by arithmetic: "Hot" comes out about 5 C over because
    /// the model has no hotter climate left to draw, and an already very wet or very dry region
    /// moves perhaps half as far as asked, being near the end of what the model will draw there.
    /// </summary>
    private const float TemperatureResponse = 1.25f;

    /// <summary>The same for precipitation.</summary>
    private const float PrecipitationResponse = 1.15f;

    /// <summary>
    /// Works out how much of a world's climate settings the model can be asked for and how much
    /// has to be applied to what it produces.
    ///
    /// The model is asked for a conditioning shift of <c>m^(1/response)</c>, which is the shift
    /// that comes back out as <c>m</c>; then the anchor is clamped to what the real climate
    /// distribution can express, and the correction carries whatever that clamp cost. Most
    /// settings need no correction at all. The ones that do are the hot end of the temperature
    /// scale, where there is nothing left to ask for: the world's warmest mean annual temperature
    /// is about 30 C, so "very hot" and "scorching hot" both draw the hottest climate on Earth and
    /// then have the difference between that and what was asked for scaled onto the result — which
    /// is what vanilla does too, its own climate byte having saturated at 40 C long before.
    /// </summary>
    /// <returns>
    /// The division of labour, or <see cref="ClimatePlan.Unconditioned"/> if the model's data is
    /// not on disk yet — on a fresh install the world is read once before anything is downloaded,
    /// and a world that cannot consult the model still has to honour its settings.
    /// </returns>
    public static ClimatePlan PlanClimate(ClimateShift climate)
    {
        float strength = DiffusionConfig.Instance.WorldGen.GlobalClimateStrength;
        if (strength <= 0f || climate.IsNeutral) return ClimatePlan.Unconditioned(climate);

        PipelineData data;
        try
        {
            data = LoadData();
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(
                "pipeline_data.json could not be read, so this world's climate settings cannot " +
                "be applied.", e);
        }

        (float exponent, float temperatureDelivered) = PlanTemperature(
            data.DataQuantileTables[TemperatureChannel], climate.Temperature, strength);

        // Rainfall needs no clamping and so no correction: the conditioning is scaled outright and
        // the model returns the scale it was given, raised to its response.
        float scale = Asked(climate.Precipitation, strength, PrecipitationResponse);
        float precipitationDelivered = (float)Math.Pow(scale, PrecipitationResponse);

        return new ClimatePlan(
            exponent, scale,
            climate.Temperature / temperatureDelivered,
            climate.Precipitation / precipitationDelivered);
    }

    /// <summary>
    /// The conditioning shift to ask for, being the one that comes back out as the shift the world
    /// wants — or a fraction of it, where the conditioning has been turned down.
    /// </summary>
    private static float Asked(float multiplier, float strength, float response)
        => (float)Math.Pow(Math.Max(1e-4f, multiplier), strength / response);

    /// <summary>
    /// What the model actually puts on the ground when it is conditioned on a given climate,
    /// measured rather than derived.
    ///
    /// Two things separate the two numbers. The land the model draws stands above sea level, and
    /// its own lapse rate cools it; and the model amplifies, leaning about 16% further from its
    /// neutral climate than the conditioning asked. Both are steady enough to invert: the slope
    /// held to within 3% across three seeds, and it is the slope that decides whether a latitude
    /// gradient survives the model at all.
    ///
    /// The offset is not steady, and deliberately not corrected for. It ran from -5 C to -9 C
    /// between the three seeds, because it is mostly the relief of whichever continents that seed
    /// drew, and a world of high plateaus really is colder than a world of coastal plains - the
    /// same reason Siberia is colder than Ireland. These are the mean over the three, so an
    /// ordinary world lands on its bands and a mountainous one comes out a few degrees under.
    ///
    /// Measured over a 64x64 coarse box (about 250 km square at the default resolution) on seeds
    /// 1234, 777 and 424242, taking the median of every land pixel, with the whole map conditioned
    /// on one climate at a time.
    /// </summary>
    private static readonly float[] ResponseConditioningC = { -7f, -2f, 2f, 6f, 10f, 14f, 18f, 22f, 26f, 30f };

    /// <summary>
    /// Median land temperature, °C, the model drew for each entry of <see cref="ResponseConditioningC"/>.
    /// </summary>
    private static readonly float[] ResponseLandC =
        { -14.33f, -11.40f, -7.63f, -3.03f, 1.87f, 6.77f, 11.57f, 16.23f, 20.70f, 24.87f };

    /// <summary>
    /// The same for rainfall, which the model damps rather than amplifies: land comes out at about
    /// two thirds of the rain the conditioning carries, near enough proportionally that the curve is
    /// straight in the log of both.
    /// </summary>
    private static readonly float[] ResponseConditioningMm = { 250f, 400f, 600f, 900f, 1400f, 2200f };

    /// <summary>
    /// Median land rainfall, mm, the model drew for each entry of <see cref="ResponseConditioningMm"/>.
    /// </summary>
    private static readonly float[] ResponseLandMm = { 166f, 286f, 439f, 679f, 1073f, 1681f };

    /// <summary>
    /// How much wetter a banded world comes out than the curve above predicts.
    ///
    /// The curve was measured with one climate over the whole map. A banded world is not that: its
    /// conditioning runs from a rainforest to an ice cap down one meridian, and the model returns
    /// about a third more rain across it than the flat case does - most of it at the cold end,
    /// where it declines to dry out a polar belt as far as the zonal means say it should. Measured
    /// as the median of world-against-band over three full equator-to-pole transects (1.21, 1.35,
    /// 1.47), and applied to the curve in both directions so that the ask comes down and the answer
    /// lands on the band.
    /// </summary>
    private const float BandedRainfallBias = 1.34f;

    /// <summary>The rainfall curve in the log of both axes, with the banded bias folded in.</summary>
    private static readonly float[] LogResponseConditioningMm = Log(ResponseConditioningMm, 1f);

    /// <summary>See <see cref="LogResponseConditioningMm"/>.</summary>
    private static readonly float[] LogResponseLandMm = Log(ResponseLandMm, BandedRainfallBias);

    /// <summary>The rainfall curve as measured, without the banded bias.</summary>
    private static readonly float[] LogResponseLandMmUnbiased = Log(ResponseLandMm, 1f);

    /// <summary>Natural logs of <paramref name="values"/> times <paramref name="bias"/>.</summary>
    private static float[] Log(float[] values, float bias)
    {
        var result = new float[values.Length];
        for (int i = 0; i < values.Length; i++) result[i] = (float)Math.Log(values[i] * bias);
        return result;
    }

    /// <summary>
    /// Works out how one latitude's climate is divided between the model and its output.
    ///
    /// Most of it the model can simply be asked for, once the ask is put through the inverse of its
    /// measured response. What it cannot be asked for is a climate it has never seen: mean annual
    /// temperature in the model's world tops out at 35.6 C and bottoms at -7.5 C, which after the
    /// response is land between about -14 C and 31 C. From roughly 80 degrees poleward the band
    /// wants an ice cap colder than that, so the conditioning is pinned to the coldest climate there
    /// is and the last few degrees are an offset on the output. That is the same bargain the hot end
    /// of <c>globalTemperature</c> strikes, and the same one vanilla strikes when its climate byte
    /// saturates.
    ///
    /// Called once per entry of <see cref="LatitudeBands"/>' table rather than per pixel, which is
    /// why it can afford to search the quantile tables.
    /// </summary>
    /// <param name="wantedC">Mean annual temperature the band wants on the ground, in Celsius.</param>
    /// <param name="wantedMm">Annual rainfall the band wants on the ground, in millimetres.</param>
    /// <exception cref="InvalidOperationException">The model's data is not on disk.</exception>
    /// <param name="bandedRainfallBias">
    /// Whether to apply <see cref="BandedRainfallBias"/>. It was measured on latitude profile 1;
    /// world setting <c>terraindiffusionBandedRainfallBias</c>, off for new worlds.
    /// </param>
    public static LatitudeBandPlan PlanLatitude(float wantedC, float wantedMm, bool bandedRainfallBias = true)
    {
        float[] logLandMm = bandedRainfallBias ? LogResponseLandMm : LogResponseLandMmUnbiased;
        PipelineData data = LoadData();
        float[] temperature = data.DataQuantileTables[TemperatureChannel];
        float[] rainfall = data.DataQuantileTables[PrecipitationChannel];

        float conditioningC = Math.Clamp(
            Interp(wantedC, ResponseLandC, ResponseConditioningC, true),
            temperature[0], temperature[temperature.Length - 1]);
        float conditioningMm = Math.Clamp(
            (float)Math.Exp(Interp((float)Math.Log(Math.Max(1f, wantedMm)), logLandMm,
                                   LogResponseConditioningMm, true)),
            rainfall[0], rainfall[rainfall.Length - 1]);

        float deliveredC = Interp(conditioningC, ResponseConditioningC, ResponseLandC, true);
        float deliveredMm = (float)Math.Exp(
            Interp((float)Math.Log(Math.Max(1f, conditioningMm)), LogResponseConditioningMm,
                   logLandMm, true));

        return new LatitudeBandPlan(
            conditioningC, conditioningMm,
            wantedC - deliveredC,
            deliveredMm > 0f ? wantedMm / deliveredMm : 1f);
    }

    /// <summary>The climate the model draws when nothing has told it otherwise.</summary>
    public static (float TemperatureC, float PrecipitationMm) NeutralClimate()
    {
        PipelineData data = LoadData();
        float[] temperature = data.DataQuantileTables[TemperatureChannel];
        float[] rainfall = data.DataQuantileTables[PrecipitationChannel];
        return (SampleTable(temperature, (temperature.Length - 1) * 0.5f),
                SampleTable(rainfall, (rainfall.Length - 1) * 0.5f));
    }

    /// <summary>
    /// The temperature warp, and the shift the world will actually come out with because of it.
    ///
    /// Temperature is warped by rank where rainfall is scaled outright, because the two
    /// distributions are shaped nothing alike. Annual rainfall runs from nothing to six metres and
    /// scaling it lands inside that range whatever the setting asks for. Mean annual temperature
    /// occupies about forty degrees, from -7 C to 36 C, with no room above: scaling it by anything
    /// over 1.1 walks straight off the end of the world's climate and takes every pixel with it,
    /// where a rank warp merely moves the world towards its hot end and stops when it gets there.
    /// </summary>
    private static (float Exponent, float Delivered) PlanTemperature(
        float[] table, float multiplier, float strength)
    {
        // Temperature scales from -20 C, the bottom of the scale Vintage Story multiplies on.
        const float origin = ClimateShift.ScaleFloorC;

        float span = table.Length - 1f;
        float median = SampleTable(table, span * 0.5f);
        float asked = Asked(multiplier, strength, TemperatureResponse);
        float wanted = origin + (median - origin) * asked;
        if (Math.Abs(wanted - median) < 1e-3f) return (1f, 1f);

        // Clamped off both ends of the table: a rank of exactly 0 has no logarithm, and one of
        // exactly 1 has no warp that reaches it. The exponent bounds then decide how far the warp
        // is allowed to go before it stops being a warp and starts being a flat field.
        float rank = Math.Clamp(TablePosition(wanted, table) / span, 1e-3f, 1f - 1e-3f);
        float exponent = Math.Clamp(
            (float)(Math.Log(rank) / Math.Log(0.5)), MinRankExponent, MaxRankExponent);

        float reached = SampleTable(table, span * (float)Math.Pow(0.5, exponent));
        float delivered = (float)Math.Pow((reached - origin) / (median - origin), TemperatureResponse);
        return (exponent, delivered);
    }
}
