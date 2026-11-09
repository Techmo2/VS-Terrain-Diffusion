using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.Core;

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

    /// <summary>Block Z per coarse map row.</summary>
    private readonly double _blocksPerCoarsePixel;

    /// <summary>Block Z of coarse row 0's top edge.</summary>
    private readonly double _originBlockZ;

    /// <summary>Conditioning temperature, °C, at <see cref="Samples"/> steps from equator to pole.</summary>
    private readonly float[] _conditioningC;

    /// <summary>Conditioning rainfall, mm, at the same steps.</summary>
    private readonly float[] _conditioningMm;

    /// <summary>Temperature offset on the model's output, °C, at the same steps.</summary>
    private readonly float[] _offsetC;

    /// <summary>Rainfall factor on the model's output, at the same steps.</summary>
    private readonly float[] _rainfall;

    /// <summary>What each band is aiming for, kept only so the diagnostic commands can say so.</summary>
    private readonly float[] _wantedC;

    /// <summary>See <see cref="_wantedC"/>.</summary>
    private readonly float[] _wantedMm;

    /// <summary>Why the bands are or are not in play, for the log and the /terraindiffusion command.</summary>
    public string Status { get; }

    /// <inheritdoc/>
    public bool IsNeutral => _latitude == null;

    /// <summary>Bands that are off, saying why.</summary>
    /// <param name="status">See <see cref="Status"/>.</param>
    private LatitudeBands(string status)
    {
        Status = status;
    }

    /// <summary>Bands from tables planned by <see cref="Over"/>.</summary>
    /// <param name="latitude">See <see cref="_latitude"/>.</param>
    /// <param name="blocksPerCoarsePixel">See <see cref="_blocksPerCoarsePixel"/>.</param>
    /// <param name="originBlockZ">See <see cref="_originBlockZ"/>.</param>
    /// <param name="conditioningC">See <see cref="_conditioningC"/>.</param>
    /// <param name="conditioningMm">See <see cref="_conditioningMm"/>.</param>
    /// <param name="offsetC">See <see cref="_offsetC"/>.</param>
    /// <param name="rainfall">See <see cref="_rainfall"/>.</param>
    /// <param name="wantedC">See <see cref="_wantedC"/>.</param>
    /// <param name="wantedMm">See <see cref="_wantedMm"/>.</param>
    /// <param name="status">See <see cref="Status"/>.</param>
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
    /// <param name="api">The server, whose calendar says which block is at which latitude.</param>
    /// <param name="settings">The world's settings, for the conditioning grid.</param>
    /// <param name="worldClimate">The world's <c>worldClimate</c>; only "realistic" is banded.</param>
    /// <param name="polarEquatorDistance">Blocks from the equator to a pole.</param>
    /// <param name="strength">How much of the band to apply, 0 to 1.</param>
    /// <param name="climate">The world's global temperature and precipitation settings; see <see cref="Over"/>.</param>
    /// <param name="profileVersion">Which <see cref="LatitudeProfile"/> the world was created with.</param>
    /// <param name="bandedRainfallBias">Whether to plan rainfall with the banded-world bias.</param>
    public static LatitudeBands ForWorld(ICoreServerAPI api, DiffusionWorldSettings settings,
                                         string worldClimate, int polarEquatorDistance, float strength,
                                         ClimateShift climate, int profileVersion, bool bandedRainfallBias = true)
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

        double blocksPerCoarsePixel = settings.BlocksPerConditioningPixel;
        string status = strength >= 1f
            ? $"on, pole {polarEquatorDistance} blocks from the equator"
            : $"on at {strength:0.##} strength, pole {polarEquatorDistance} blocks from the equator";

        // Read through the calendar rather than caching the delegate, so a mod that installs its
        // own latitude later is still the one that gets honoured.
        return Over(z => calendar.OnGetLatitude(z), blocksPerCoarsePixel, settings.OriginBlockZ,
                    strength, climate,
                    (profileVersion >= 2 ? status + $", profile {profileVersion}" : status)
                    + (bandedRainfallBias ? "" : ", no banded rainfall bias"),
                    profileVersion, bandedRainfallBias);
    }

    /// <summary>
    /// Bands over an arbitrary latitude function: block Z in, -1 to 1 out, zero at the equator.
    /// The seam <see cref="ForWorld"/> is built on, and the one the offline checks use.
    /// </summary>
    /// <param name="latitude">Block Z to latitude, -1 to 1, zero at the equator.</param>
    /// <param name="blocksPerCoarsePixel">Block Z per coarse map row.</param>
    /// <param name="originBlockZ">Block Z of coarse row 0.</param>
    /// <param name="strength">How much of the band to apply, 0 to 1.</param>
    /// <param name="climate">
    /// The world's global temperature and precipitation settings, applied to the band rather than
    /// to the model's output: a "snowball earth" world is one whose every band is a quarter of the
    /// way up the scale, ice caps and tropics alike.
    /// </param>
    /// <param name="status">What <see cref="Status"/> reports.</param>
    /// <param name="profileVersion">Which <see cref="LatitudeProfile"/> the world was created with.</param>
    /// <param name="bandedRainfallBias">Whether to plan rainfall with the banded-world bias.</param>
    public static LatitudeBands Over(System.Func<double, double> latitude, double blocksPerCoarsePixel,
                                     double originBlockZ, float strength, ClimateShift climate, string status,
                                     int profileVersion = LatitudeProfile.CurrentVersion, bool bandedRainfallBias = true)
    {
        if (latitude == null || strength <= 0f) return None;

        float neutralC, neutralMm;
        try
        {
            (neutralC, neutralMm) = SyntheticMapFactory.NeutralClimate();
        }
        catch (Exception e)
        {
            // The settings are only ever read once the models are resident, so the data is there.
            throw DiffusionFailure.Fatal(
                "The model's climate data could not be read, so latitude bands cannot be built.", e);
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
            float bandC = ClimateShift.ApplyTemperature(LatitudeProfile.TemperatureC(pole01, profileVersion), climate.Temperature);
            float bandMm = LatitudeProfile.PrecipitationMm(pole01, profileVersion) * climate.Precipitation;
            // Clamped to the scale the game's climate map can hold, so that the coldest belts ask
            // for -20 C rather than for an ice cap the byte would flatten anyway: an offset that
            // drove every column past the floor would leave the pole one uniform value.
            float wantedC = Math.Clamp(neutralC + strength * (bandC - neutralC),
                                       ClimateShift.ScaleFloorC, ClimateShift.ScaleCeilingC);
            float wantedMm = neutralMm * (float)Math.Pow(Math.Max(1e-3f, bandMm / neutralMm), strength);

            LatitudeBandPlan plan = SyntheticMapFactory.PlanLatitude(wantedC, wantedMm, bandedRainfallBias);
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

    /// <inheritdoc/>
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

    /// <summary>
    /// Interpolates a table of <see cref="Samples"/> entries at a distance from the equator.
    /// </summary>
    /// <param name="table">One of the per-latitude tables.</param>
    /// <param name="pole01">0 at the equator, 1 at the pole.</param>
    private static float Lookup(float[] table, float pole01)
    {
        float position = Math.Clamp(pole01, 0f, 1f) * (Samples - 1);
        int lo = (int)position;
        if (lo >= Samples - 1) return table[Samples - 1];
        float t = position - lo;
        return table[lo] + t * (table[lo + 1] - table[lo]);
    }
}
