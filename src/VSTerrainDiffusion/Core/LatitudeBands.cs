using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Zonal mean climate: the temperature and rainfall a place has for no other reason than how far
/// it sits from the equator.
///
/// Both curves are fits to the observed zonal means over land, not derivations, and they are
/// deliberately smooth: everything that makes one place in a belt different from another - the
/// coast, the mountain, the rain shadow, the continental interior - is the model's job, and comes
/// out of it far larger than the error in these.
/// </summary>
public static class LatitudeProfile
{
    /// <summary>Mean annual temperature at the equator, in Celsius.</summary>
    public const float EquatorC = 26f;

    /// <summary>Mean annual temperature at the pole, in Celsius.</summary>
    public const float PoleC = -25f;

    /// <summary>
    /// Curvature of the temperature profile. The observed one is flat across the tropics and then
    /// nearly straight from the subtropics to the ice, which a plain square is too gentle to
    /// follow; 1.8 splits the difference, landing within about 4 C of the zonal means everywhere
    /// (26 at the equator, 22 at 20 deg, 19 at 30, 14 at 40, 8 at 50, 1 at 60, -6 at 70, -15 at 80).
    /// </summary>
    private const float TemperatureCurve = 1.8f;

    /// <summary>Mean annual temperature for a place this far from the equator, 0 to 1.</summary>
    public static float TemperatureC(float pole01)
    {
        float a = Math.Clamp(pole01, 0f, 1f);
        return EquatorC - (EquatorC - PoleC) * (float)Math.Pow(a, TemperatureCurve);
    }

    /// <summary>
    /// Annual precipitation for a place this far from the equator, in millimetres.
    ///
    /// Three terms, one per cell of the general circulation. Air rises at the equator and rains
    /// itself out there, which is the tall narrow peak; it comes back down dry at about 25 deg,
    /// which is where every desert on Earth is and which shows up here as the trough between the
    /// two humps; and the polar front throws it up again along the mid-latitude storm track near
    /// 50 deg, which is the second, broader peak. The baseline underneath is the plain fact that
    /// cold air holds less water, so even a wet pole is a dry one by tropical standards.
    /// </summary>
    public static float PrecipitationMm(float pole01)
    {
        float degrees = Math.Clamp(pole01, 0f, 1f) * 90f;

        float itcz = 1700f * Gaussian(degrees, 0f, 13f);
        float stormTrack = 550f * Gaussian(degrees, 50f, 20f);
        float capacity = 150f + 300f * (float)Math.Sqrt(Math.Max(0.0, Math.Cos(degrees * Math.PI / 180.0)));

        return itcz + stormTrack + capacity;
    }

    private static float Gaussian(float x, float centre, float width)
    {
        float t = (x - centre) / width;
        return (float)Math.Exp(-t * t);
    }
}

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

    public LatitudeBandPlan(float conditioningTemperatureC, float conditioningPrecipitationMm,
                            float temperatureOffsetC, float rainfallFactor)
    {
        ConditioningTemperatureC = conditioningTemperatureC;
        ConditioningPrecipitationMm = conditioningPrecipitationMm;
        TemperatureOffsetC = temperatureOffsetC;
        RainfallFactor = rainfallFactor;
    }
}

/// <summary>
/// Puts the model's climate on a north-south axis, so that <c>polarEquatorDistance</c> means
/// something: the tropics are where the game says the equator is, the ice is at the poles, and the
/// deserts are in the subtropical belt between them.
///
/// Which block is at which latitude is not this mod's business to decide. Vintage Story already
/// answers it - <see cref="IGameCalendar.OnGetLatitude"/>, installed by <c>GenMaps</c> from the
/// world's <c>polarEquatorDistance</c> and phased so that the map centre lands on the chosen
/// "Starting climate" - and it is the same answer the game uses for day length, midnight sun and
/// which hemisphere has its summer when. Reading it rather than inventing a second one is what
/// keeps the sun and the snow line agreeing with each other.
///
/// What is this mod's business is what a latitude implies, because vanilla's answer is a straight
/// line from +40 C at the equator to -20 C at the pole and no rainfall gradient at all, which is
/// neither the real world nor anything the model has been trained on.
/// <see cref="LatitudeProfile"/> supplies that instead, and the band is handed to the model as
/// conditioning - so a tropical belt is <em>drawn</em> tropical, with the rainforest, the
/// weathering and the drainage that go with it - with only the part the conditioning cannot reach
/// applied to the output afterwards.
/// </summary>
public sealed class LatitudeBands : ILatitudeSource
{
    /// <summary>
    /// Plans held between the equator and the pole. The profiles are smooth and the model's own
    /// variation dwarfs the interpolation error, so a table this size costs nothing and saves a
    /// quantile search per row.
    /// </summary>
    private const int Samples = 129;

    /// <summary>
    /// Spread of latitude, across four probes a quarter of a polar distance apart, below which the
    /// calendar is taken not to have a real latitude function yet. Vintage Story's own placeholder
    /// returns a constant 0.5 - a world banded on that would be one endless band at 45 degrees.
    /// </summary>
    private const double MinimumProbeSpread = 0.05;

    /// <summary>Block Z to latitude in -1..1, or null when there is no banding to do.</summary>
    private readonly System.Func<double, double> _latitude;

    private readonly double _blocksPerCoarsePixel;
    private readonly double _originBlockZ;

    private readonly float[] _conditioningC;
    private readonly float[] _conditioningMm;
    private readonly float[] _offsetC;
    private readonly float[] _rainfall;

    /// <summary>What each band is aiming for, kept only so the diagnostic commands can say so.</summary>
    private readonly float[] _wantedC;

    private readonly float[] _wantedMm;

    /// <summary>Why the bands are or are not in play, for the log and the /terraindiffusion command.</summary>
    public string Status { get; }

    public bool IsNeutral => _latitude == null;

    private LatitudeBands(string status)
    {
        Status = status;
    }

    private LatitudeBands(System.Func<double, double> latitude, double blocksPerCoarsePixel, double originBlockZ,
                          float[] conditioningC, float[] conditioningMm, float[] offsetC, float[] rainfall,
                          float[] wantedC, float[] wantedMm, string status)
    {
        _latitude = latitude;
        _blocksPerCoarsePixel = blocksPerCoarsePixel;
        _originBlockZ = originBlockZ;
        _conditioningC = conditioningC;
        _conditioningMm = conditioningMm;
        _offsetC = offsetC;
        _rainfall = rainfall;
        _wantedC = wantedC;
        _wantedMm = wantedMm;
        Status = status;
    }

    /// <summary>A world with no latitude banding, which is what the mod did before 0.5.</summary>
    public static LatitudeBands None { get; } = new("off");

    /// <summary>
    /// Builds the bands for a world, or <see cref="None"/> when it has no latitude to band on.
    ///
    /// Resolved once, here, rather than lazily on first use: the coarse conditioning is cached for
    /// the life of the world, so a tile drawn before the calendar was ready would keep its
    /// unbanded climate forever while its neighbours got a banded one. This runs from the mod's own
    /// <c>InitWorldGenerator</c> handler, which is registered after <c>GenMaps</c>' - the mod
    /// depends on <c>game</c> - so by now the calendar has its real function.
    /// </summary>
    /// <param name="strength">How much of the band to apply, 0 to 1.</param>
    public static LatitudeBands ForWorld(ICoreServerAPI api, DiffusionWorldSettings settings,
                                         string worldClimate, int polarEquatorDistance, float strength,
                                         ClimateShift climate)
    {
        if (strength <= 0f) return None;
        if (worldClimate != "realistic")
        {
            return new LatitudeBands($"off, because this world's climate is \"{worldClimate}\" rather than realistic");
        }

        IGameCalendar calendar = api?.World?.Calendar;
        if (calendar?.OnGetLatitude == null) return new LatitudeBands("off, because the calendar has no latitude");

        if (!HasRealLatitude(calendar, polarEquatorDistance))
        {
            return new LatitudeBands("off, because the world's latitude has not been set up");
        }

        double blocksPerCoarsePixel = 32.0 * WorldPipelineModelConfig.Instance.LatentCompression * settings.Scale;
        string status = strength >= 1f
            ? $"on, pole {polarEquatorDistance} blocks from the equator"
            : $"on at {strength:0.##} strength, pole {polarEquatorDistance} blocks from the equator";

        // Read through the calendar rather than caching the delegate, so a mod that installs its
        // own latitude later is still the one that gets honoured.
        return Over(z => calendar.OnGetLatitude(z), blocksPerCoarsePixel, settings.OriginBlockZ,
                    strength, climate, status);
    }

    /// <summary>
    /// Bands over an arbitrary latitude function: block Z in, -1 to 1 out, zero at the equator.
    /// The seam <see cref="ForWorld"/> is built on, and the one the offline checks use.
    /// </summary>
    /// <param name="climate">
    /// The world's global temperature and precipitation settings, applied to the band rather than
    /// to the model's output: a "snowball earth" world is one whose every band is a quarter of the
    /// way up the scale, ice caps and tropics alike.
    /// </param>
    public static LatitudeBands Over(System.Func<double, double> latitude, double blocksPerCoarsePixel,
                                     double originBlockZ, float strength, ClimateShift climate, string status)
    {
        if (latitude == null || strength <= 0f) return None;

        float neutralC, neutralMm;
        try
        {
            (neutralC, neutralMm) = SyntheticMapFactory.NeutralClimate();
        }
        catch (InvalidOperationException)
        {
            // The model's data is not on disk yet; a fresh install reads the world once before
            // anything is downloaded. An unbanded world is the honest answer for that pass.
            return new LatitudeBands("off, because the model data has not been downloaded yet");
        }

        var conditioningC = new float[Samples];
        var conditioningMm = new float[Samples];
        var offsetC = new float[Samples];
        var rainfall = new float[Samples];
        var targetC = new float[Samples];
        var targetMm = new float[Samples];

        for (int i = 0; i < Samples; i++)
        {
            float pole01 = (float)i / (Samples - 1);

            // Partial strength is a walk from the climate the model would have drawn anyway towards
            // the band, in degrees for temperature and in ratio for rainfall, so that 0 is exactly
            // the unbanded world and 1 is exactly the band.
            float bandC = ClimateShift.ApplyTemperature(LatitudeProfile.TemperatureC(pole01), climate.Temperature);
            float bandMm = LatitudeProfile.PrecipitationMm(pole01) * climate.Precipitation;
            // Clamped to the scale the game's climate map can hold, so that the coldest belts ask
            // for -20 C rather than for an ice cap the byte would flatten anyway: an offset that
            // drove every column past the floor would leave the pole one uniform value.
            float wantedC = Math.Clamp(neutralC + strength * (bandC - neutralC),
                                       ClimateShift.ScaleFloorC, ClimateShift.ScaleCeilingC);
            float wantedMm = neutralMm * (float)Math.Pow(Math.Max(1e-3f, bandMm / neutralMm), strength);

            LatitudeBandPlan plan = SyntheticMapFactory.PlanLatitude(wantedC, wantedMm);
            conditioningC[i] = plan.ConditioningTemperatureC;
            conditioningMm[i] = plan.ConditioningPrecipitationMm;
            offsetC[i] = plan.TemperatureOffsetC;
            rainfall[i] = plan.RainfallFactor;
            targetC[i] = wantedC;
            targetMm[i] = wantedMm;
        }

        return new LatitudeBands(latitude, blocksPerCoarsePixel, originBlockZ,
                                 conditioningC, conditioningMm, offsetC, rainfall, targetC, targetMm, status);
    }

    /// <summary>
    /// Whether the calendar's latitude actually varies with Z. Four probes a quarter of a polar
    /// distance apart, which no phase can leave all at the same latitude.
    /// </summary>
    private static bool HasRealLatitude(IGameCalendar calendar, int polarEquatorDistance)
    {
        double step = Math.Max(1, polarEquatorDistance) * 0.25;
        double low = double.MaxValue, high = double.MinValue;
        for (int i = 0; i < 4; i++)
        {
            double value = calendar.OnGetLatitude(i * step);
            low = Math.Min(low, value);
            high = Math.Max(high, value);
        }
        return high - low >= MinimumProbeSpread;
    }

    /// <summary>How far from the equator a block column sits, 0 at the equator and 1 at the pole.</summary>
    public float Pole01(double blockZ)
        => _latitude == null ? 0f : (float)Math.Clamp(Math.Abs(_latitude(blockZ)), 0.0, 1.0);

    public void BandAt(int coarseRow, out float temperatureC, out float precipitationMm)
    {
        if (_latitude == null)
        {
            temperatureC = 0f;
            precipitationMm = 0f;
            return;
        }

        // The middle of the pixel, not its corner: a band read at the edge is half a pixel colder
        // than the ground it is drawn for, all the way down the map.
        double blockZ = _originBlockZ + (coarseRow + 0.5) * _blocksPerCoarsePixel;
        float pole01 = Pole01(blockZ);
        temperatureC = Lookup(_conditioningC, pole01);
        precipitationMm = Lookup(_conditioningMm, pole01);
    }

    /// <summary>
    /// The mean annual temperature this latitude's band is aiming for, in Celsius: the zonal mean
    /// over land, after the world's global temperature setting and the band strength. What the
    /// ground actually reads should scatter around it, colder on high land and warmer on a coast.
    /// </summary>
    public float BandTemperatureC(double blockZ)
        => _latitude == null ? float.NaN : Lookup(_wantedC, Pole01(blockZ));

    /// <summary>The annual rainfall the band is aiming for, in millimetres.</summary>
    public float BandPrecipitationMm(double blockZ)
        => _latitude == null ? float.NaN : Lookup(_wantedMm, Pole01(blockZ));

    /// <summary>Degrees to add to a modelled temperature for the latitude it was drawn at.</summary>
    public float TemperatureOffsetC(double blockZ)
        => _latitude == null ? 0f : Lookup(_offsetC, Pole01(blockZ));

    /// <summary>Multiplier on modelled rainfall for the latitude it was drawn at.</summary>
    public float RainfallFactor(double blockZ)
        => _latitude == null ? 1f : Lookup(_rainfall, Pole01(blockZ));

    private static float Lookup(float[] table, float pole01)
    {
        float position = Math.Clamp(pole01, 0f, 1f) * (Samples - 1);
        int lo = (int)position;
        if (lo >= Samples - 1) return table[Samples - 1];
        float t = position - lo;
        return table[lo] + t * (table[lo + 1] - table[lo]);
    }
}
