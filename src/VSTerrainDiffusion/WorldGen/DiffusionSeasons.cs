using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Swings temperature and rainfall through the year using the model's own seasonality, in place of
/// Vintage Story's latitude bands.
///
/// Vanilla decides how strongly a place swings from its latitude alone: <c>ModTemperature</c> takes
/// an amplitude of <c>|latitude| * 65</c> degrees, so the equator has no seasons and the poles have
/// enormous ones, and nothing else about the location matters. That is a reasonable stand-in when
/// the only thing you know about a spot is how far north it is. Here we know a great deal more: the
/// model predicts WorldClim BIO4 (how far monthly temperatures actually spread) and BIO15 (how
/// unevenly the rain falls across the year), which is why a maritime coast can stay mild at the
/// same temperature as a continental interior that freezes solid every winter, and why a monsoon
/// climate gets a real dry season.
///
/// This runs on both sides. Vintage Story sends map region mod data to clients, so a client with
/// the mod installed reads the same seasonality the server does and its rain, snow and temperature
/// readout agree with what the server simulates. A vanilla client simply keeps vanilla's seasons.
/// </summary>
public class DiffusionSeasons : ModSystem
{
    private ICoreAPI _api;
    private WorldGenConfig _config;
    private SimplexNoise _yearlyNoise;
    private SimplexNoise _dailyNoise;

    /// <summary>
    /// Peak-to-peak swing of a sine wave with unit standard deviation. A sine of amplitude A has
    /// standard deviation A / sqrt(2), so a season with sigma degrees of spread runs 2*sqrt(2)
    /// sigma from midwinter to midsummer.
    /// </summary>
    private const float SwingPerSigma = 2.8284271f;

    /// <summary>
    /// Relative amplitude of the wet season per unit coefficient of variation.
    ///
    /// Not the sine value sqrt(2) the temperature swing above uses, because rainfall does not reach
    /// the ground linearly. Vanilla turns it into <c>wgenRain</c> and then into
    /// <c>max(0, noise + wgenRain - 0.5)</c>, a rectified threshold that amplifies a relative
    /// rainfall swing about 2.4 times in precipitation terms. Measured against the game's own noise
    /// field over 30-year monthly normals, the gain is 1.70 CV per unit of relative swing and is
    /// near enough independent of the rainfall itself, so inverting it puts BIO15 back on the
    /// ground: a place the model gives 57% comes out at 50-56%. sqrt(2) gave every climate an
    /// 80% monsoon regardless of what was asked for.
    /// </summary>
    private const float WetSeasonPerCv = 1f / 1.70f;

    /// <summary>
    /// Rain multiplier departure per unit departure of the seasonal model's monthly rain from its
    /// mean, on a seasonal-model world. The same measured transfer as <see cref="WetSeasonPerCv"/>,
    /// restated per standard deviation instead of per amplitude of vanilla's smootherstep year (whose
    /// <c>2s - 1</c> has an RMS of 0.753): realised CV = 2.26 × the multiplier's standard deviation,
    /// so the model's monthly rain shape is scaled by 1 / 2.26 to come out at the model's own CV.
    /// </summary>
    private const double RainShapeGain = 1.0 / 2.2575;

    /// <summary>
    /// Loads on the client as well as the server. Only the climate hook runs there; everything that
    /// touches the model or generates terrain lives in server-side systems.
    /// </summary>
    public override bool ShouldLoad(EnumAppSide side) => true;

    /// <summary>
    /// After the survival mod, whose <c>ModTemperature</c> registers the same event. Handlers run in
    /// registration order and this one replaces the temperature that one computed, so it has to be
    /// the later of the two.
    /// </summary>
    public override double ExecuteOrder() => 0.9;

    public override void Start(ICoreAPI api)
    {
        _api = api;
        _config = DiffusionConfig.Load(api).WorldGen;

        if (!_config.SeasonalTemperature && !_config.SeasonalPrecipitation) return;

        // Noise seeds differ from vanilla's so the two do not add up to double the wobble on a
        // world where both are somehow active.
        _yearlyNoise = SimplexNoise.FromDefaultOctaves(3, 0.001, 0.95, api.World.Seed + 41221);
        _dailyNoise = SimplexNoise.FromDefaultOctaves(3, 1.0, 0.95, api.World.Seed + 41222);

        // Temperature only. The handler fires for the "now" modes alone, and GetPrecipitation -
        // what farmland and most rain-aware mods call - asks with WorldGenValues, so rainfall is
        // swung further down in SeasonalPrecipitationCompat where both paths meet.
        if (_config.SeasonalTemperature) api.Event.OnGetClimate += OnGetClimate;
        if (_config.SeasonalPrecipitation) SeasonalPrecipitationCompat.Install(api, this);
    }

    public override void Dispose()
    {
        if (_config != null && _config.SeasonalPrecipitation) SeasonalPrecipitationCompat.Uninstall(_api);
    }

    /// <summary>
    /// Swings the temperature into the season. World generation wants annual averages, so only the
    /// "now" and "for this date" modes are asking what the weather is actually doing.
    /// </summary>
    private void OnGetClimate(ref ClimateCondition climate, BlockPos pos, EnumGetClimateMode mode, double totalDays)
    {
        if (mode == EnumGetClimateMode.WorldGenValues) return;

        SeasonalityMap.Sample? sample = SeasonalityMap.At(_api.World.BlockAccessor, pos);
        if (sample == null) return;

        IGameCalendar calendar = _api.World.Calendar;
        double yearRel = totalDays / calendar.DaysPerYear % 1.0;
        SeasonalCycleMap.Sample? cycle = SeasonalCycleMap.At(_api.World.BlockAccessor, pos);

        double temperature = climate.WorldGenTemperature + SeasonalDeviation(
            Month(yearRel), IsSouthern(pos), sample.Value.TemperatureSigmaC, _config.SeasonalTemperatureStrength, cycle,
            WinterGain(_api.World, pos.Z));

        // Day and night: the coldest hour is just before dawn. The seasonal model knows each place's
        // swing (BIO2, the mean of each month's maximum minus minimum); otherwise vanilla's
        // stand-in, where clear desert air swings far more than damp air.
        double hourOfDay = totalDays % 1.0 * calendar.HoursPerDay;
        double diurnalRange = cycle?.DiurnalRangeC ?? 18.0 - climate.WorldgenRainfall * 13.0;
        double dayPhase = GameMath.SmoothStep(Math.Abs(GameMath.CyclicValueDistance(4.0, hourOfDay, 24.0) / 12.0));
        temperature += (dayPhase - 0.5) * diurnalRange;

        temperature += _yearlyNoise.Noise(totalDays, 0.0) * 3.0;
        temperature += _dailyNoise.Noise(totalDays, 0.0);

        climate.Temperature = (float)temperature;
    }

    /// <summary>
    /// The seasonal rainfall multiplier at a world position, for
    /// <see cref="SeasonalPrecipitationCompat"/>. 1 where this world has no seasonality map.
    /// </summary>
    internal float SeasonalRainfallFactor(double posX, double posZ, double totalDays)
    {
        if (!_config.SeasonalPrecipitation) return 1f;

        var pos = new BlockPos((int)posX, 0, (int)posZ);
        SeasonalityMap.Sample? sample = SeasonalityMap.At(_api.World.BlockAccessor, pos);
        if (sample == null) return 1f;

        double yearRel = totalDays / _api.World.Calendar.DaysPerYear % 1.0;
        SeasonalCycleMap.Sample? cycle = SeasonalCycleMap.At(_api.World.BlockAccessor, posX, posZ);
        return cycle == null
            ? RainfallFactor(sample.Value.PrecipitationCv, SummerWeight(Month(yearRel), IsSouthern(pos)))
            : RainfallFactor(cycle.Value, SeasonTheta(Month(yearRel), IsSouthern(pos)));
    }

    /// <summary>
    /// How much of a place's usual rain is falling at this point in the year. Wet season in
    /// summer, which is where monsoons and continental convective rain sit.
    ///
    /// Zero-mean over the year, and deliberately not capped above: the wet season is meant to
    /// carry more than the annual average, and clamping it at a factor of one was cutting the
    /// wettest months off. The dry season is held above
    /// <see cref="WorldGenConfig.SeasonalPrecipitationFloor"/> instead, because a real drought
    /// stalls every unirrigated crop rather than merely slowing it.
    /// </summary>
    private float RainfallFactor(float precipitationCv, double summer)
    {
        float cv = precipitationCv / 100f * _config.SeasonalPrecipitationStrength;
        return (float)Math.Max(_config.SeasonalPrecipitationFloor,
            1.0 + WetSeasonPerCv * cv * (2.0 * summer - 1.0));
    }

    /// <summary>
    /// The seasonal rainfall multiplier at a position and date, for the <c>/tdiff season</c>
    /// readout. What the game reports as rainfall in the moment is whether it happens to be raining
    /// just then, which says nothing about the shape of the year.
    /// </summary>
    public float RainfallFactorAt(BlockPos pos, double totalDays, float precipitationCv)
    {
        if (!_config.SeasonalPrecipitation) return 1f;
        double yearRel = totalDays / _api.World.Calendar.DaysPerYear % 1.0;
        SeasonalCycleMap.Sample? cycle = SeasonalCycleMap.At(_api.World.BlockAccessor, pos);
        return cycle == null
            ? RainfallFactor(precipitationCv, SummerWeight(Month(yearRel), IsSouthern(pos)))
            : RainfallFactor(cycle.Value, SeasonTheta(Month(yearRel), IsSouthern(pos)));
    }

    /// <summary>
    /// The seasonal model's rain at this point in the year: its monthly shape, scaled by
    /// <see cref="RainShapeGain"/> for vanilla's rain transfer and by the configured strength, and
    /// held above <see cref="WorldGenConfig.SeasonalPrecipitationFloor"/> as the annual path is.
    /// </summary>
    private float RainfallFactor(SeasonalCycleMap.Sample cycle, double theta)
    {
        double shape = cycle.RelativeRain(theta) - 1.0;
        return (float)Math.Max(_config.SeasonalPrecipitationFloor,
            1.0 + RainShapeGain * _config.SeasonalPrecipitationStrength * shape);
    }

    /// <summary>
    /// How far a day's mean temperature sits from the annual mean, at <paramref name="month"/>
    /// (0 to 12 through the calendar year) for a place whose monthly temperatures spread by
    /// <paramref name="sigmaC"/>. With the seasonal model's cycle it is the model's own two-harmonic
    /// year, peaking at the game's midsummer; without, vanilla's smootherstep ramp, deepened to the
    /// place's spread. Shared with the spawn search, which needs the first day's temperature.
    /// </summary>
    internal static double SeasonalDeviation(double month, bool southern, float sigmaC, float strength,
                                             SeasonalCycleMap.Sample? cycle, double winterGain = 1.0)
    {
        double deviation;
        if (cycle != null)
        {
            deviation = strength * cycle.Value.TemperatureDeviation(sigmaC, SeasonTheta(month, southern));
        }
        else
        {
            double summer = SummerWeight(month, southern);
            float swing = SwingPerSigma * sigmaC * strength;
            deviation = -swing / 2.0 + swing * summer;
        }
        // Colder than the annual mean: deepened by the winter gain. Summers are left as they are.
        return deviation < 0.0 ? deviation * winterGain : deviation;
    }

    /// <summary>World setting: how much deeper winters get per unit of latitude, in percent.</summary>
    public const string WinterAmplificationCode = "terraindiffusionWinterAmplification";

    /// <summary>
    /// The default, fitted so a world's coldest month at a latitude matches vanilla's. Vanilla's
    /// midwinter sits 32.5 C x |latitude| below the annual mean (its <c>ModTemperature</c>: a swing
    /// of |latitude| x 65 peak to peak). Measured end to end on seed 777 (the game's own sea-level
    /// temperature through a year, 21 latitudes), the model's own winters came out 0.65-0.90 of
    /// vanilla's drop from |latitude| 0.3 poleward: k = 0.58 would match the drop, 0.78 the coldest
    /// month itself, our annual means running a few degrees warmer around |latitude| 0.4-0.5.
    /// </summary>
    public const int DefaultWinterAmplificationPercent = 80;

    /// <summary>
    /// The factor winter deviations are multiplied by at a block Z: 1 + k x |latitude|, so winters
    /// deepen with distance from the equator, the way vanilla's do, while each place keeps its own
    /// shape - a maritime coast still milder than the interior. Read from the world config, which
    /// clients have too, and changeable after a world is created.
    /// </summary>
    internal static double WinterGain(IWorldAccessor world, int blockZ)
    {
        double k = (world.Config?.GetAsInt(WinterAmplificationCode, DefaultWinterAmplificationPercent)
                    ?? DefaultWinterAmplificationPercent) / 100.0;
        if (k <= 0.0 || world.Calendar?.OnGetLatitude == null) return 1.0;
        return 1.0 + k * Math.Min(1.0, Math.Abs(world.Calendar.OnGetLatitude(blockZ)));
    }

    /// <summary>The calendar month, 0 to 12, honouring a season override.</summary>
    private double Month(double yearRel)
    {
        float? seasonOverride = _api.World.Calendar.SeasonOverride;
        return seasonOverride.HasValue ? seasonOverride.Value * 12f : yearRel * 12.0;
    }

    /// <summary>
    /// Whether the year runs the other way round here: the same test Vintage Story's own calendar
    /// makes - SurvivalCoreSystem hands it OnGetLatitude and GetSeasonRel shifts the southern year
    /// half a turn - so the temperature curve and the season the game reports agree.
    /// </summary>
    private bool IsSouthern(BlockPos pos) =>
        _config.SeasonHemispheres
        && !_api.World.Calendar.SeasonOverride.HasValue
        && !(_api.World.Calendar.OnGetLatitude(pos.Z) > 0.0);

    /// <summary>
    /// Angle through the year for the seasonal model's harmonics, 0 at midsummer: the peak of
    /// <see cref="SummerWeight"/> (month 6.5, or 0.5 in the southern half), so the model's warm season
    /// lands where the game's calendar puts summer.
    /// </summary>
    private static double SeasonTheta(double month, bool southern) =>
        2.0 * Math.PI * (month - (southern ? 0.5 : 6.5)) / 12.0;

    /// <summary>
    /// How far into summer the year is, 0 at midwinter and 1 at midsummer, on the same smootherstep
    /// ramp vanilla uses so that the shape of the year is unchanged and only its depth differs.
    /// </summary>
    private static double SummerWeight(double month, bool southern) =>
        GameMath.Smootherstep(Math.Abs(GameMath.CyclicValueDistance(southern ? 6.5 : 0.5, month, 12.0) / 6.0));
}
