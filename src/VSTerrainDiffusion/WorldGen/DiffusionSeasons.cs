using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
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
    private float? _winterSeverity;
    private float? _winterShift;
    private float? _latitudeAdherence;

    /// <summary>World setting pulling winters towards vanilla's for the latitude, in percent.</summary>
    public const string LatitudeAdherenceCode = "terraindiffusionLatitudeAdherence";

    /// <summary>Vanilla's seasonal swing per unit of latitude, midwinter to midsummer, in degrees.</summary>
    private const double VanillaSwingPerLatitude = 65.0;

    /// <summary>World setting taking a fixed number of degrees off midwinter, everywhere.</summary>
    public const string WinterShiftCode = "terraindiffusionWinterColdShift";

    /// <summary>World setting multiplying the cold half of the seasonal swing, in hundredths (200 = 2x).</summary>
    public const string WinterSeverityCode = "terraindiffusionWinterSeverity";

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
        double summer = SummerWeight(yearRel, pos);

        float swing = SwingPerSigma * sample.Value.TemperatureSigmaC * _config.SeasonalTemperatureStrength;
        double seasonal = swing * (summer - 0.5);
        if (summer < 0.5)
        {
            // Winters only; the summer half keeps the model's own swing. First towards vanilla's
            // winter for this latitude, so that a latitude shares one kind of winter instead of a
            // frost-free coast beside a frozen interior. Then the multiplier, which deepens winters in
            // proportion to that swing, and the shift, which takes the same degrees off everywhere,
            // all of them at midwinter and none by the equinox.
            double vanillaSwing = Math.Abs(_api.World.Calendar.OnGetLatitude(pos.Z)) * VanillaSwingPerLatitude;
            double winterSwing = swing + LatitudeAdherence * (vanillaSwing - swing);
            seasonal = winterSwing * (summer - 0.5) * WinterSeverity - WinterShift * (1.0 - 2.0 * summer);
        }
        double temperature = climate.WorldGenTemperature + seasonal;

        // Day and night, on vanilla's shape: clear desert air swings far more than damp air,
        // and the coldest hour is just before dawn.
        double hourOfDay = totalDays % 1.0 * calendar.HoursPerDay;
        double diurnalRange = 18.0 - climate.WorldgenRainfall * 13.0;
        double dayPhase = GameMath.SmoothStep(Math.Abs(GameMath.CyclicValueDistance(4.0, hourOfDay, 24.0) / 12.0));
        temperature += (dayPhase - 0.5) * diurnalRange;

        temperature += _yearlyNoise.Noise(totalDays, 0.0) * 3.0;
        temperature += _dailyNoise.Noise(totalDays, 0.0);

        climate.Temperature = (float)temperature;
    }

    /// <summary>
    /// How much deeper than the model's own the cold half of the year runs, from the world's
    /// settings. The model's seasons are real climates', which are mild beside vanilla's
    /// latitude-driven winters. Read on first use, because a client only has the world's settings
    /// once it has joined, and kept: the setting applies from the next restart.
    /// </summary>
    private float WinterSeverity
    {
        get
        {
            if (_winterSeverity is float cached) return cached;
            if (_api.World.Config == null) return 1f;

            float value = Math.Clamp(
                DiffusionWorldSettings.ReadWorldConfig(_api.World.Config, WinterSeverityCode, "100").ToInt(100) / 100f,
                0f, 10f);
            _winterSeverity = value;
            return value;
        }
    }

    /// <summary>How far winters follow vanilla's for the latitude, 0 to 1, from the world's settings.</summary>
    private float LatitudeAdherence
    {
        get
        {
            if (_latitudeAdherence is float cached) return cached;
            if (_api.World.Config == null) return 0f;

            float value = Math.Clamp(
                DiffusionWorldSettings.ReadWorldConfig(_api.World.Config, LatitudeAdherenceCode, "0").ToInt(0) / 100f,
                0f, 1f);
            _latitudeAdherence = value;
            return value;
        }
    }

    /// <summary>Degrees taken off midwinter everywhere, from the world's settings; read like <see cref="WinterSeverity"/>.</summary>
    private float WinterShift
    {
        get
        {
            if (_winterShift is float cached) return cached;
            if (_api.World.Config == null) return 0f;

            // As for every setting, a save from before it existed keeps the weather it had.
            float value = Math.Clamp(
                DiffusionWorldSettings.ReadWorldConfig(_api.World.Config, WinterShiftCode, "0").ToInt(0), 0, 40);
            _winterShift = value;
            return value;
        }
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

        return RainfallFactor(sample.Value.PrecipitationCv,
            SummerWeight(totalDays / _api.World.Calendar.DaysPerYear % 1.0, pos));
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
        => !_config.SeasonalPrecipitation
            ? 1f
            : RainfallFactor(precipitationCv, SummerWeight(totalDays / _api.World.Calendar.DaysPerYear % 1.0, pos));

    /// <summary>
    /// How far into summer the year is, 0 at midwinter and 1 at midsummer, on the same smootherstep
    /// ramp vanilla uses so that the shape of the year is unchanged and only its depth differs.
    /// </summary>
    private double SummerWeight(double yearRel, BlockPos pos)
    {
        float? seasonOverride = _api.World.Calendar.SeasonOverride;
        double month = seasonOverride.HasValue ? seasonOverride.Value * 12f : yearRel * 12.0;

        // The same test Vintage Story's own calendar makes - SurvivalCoreSystem hands it
        // OnGetLatitude and GetSeasonRel shifts the southern year half a turn - so the temperature
        // curve and the season the game reports agree about which way round the year runs.
        bool southern = _config.SeasonHemispheres
                        && !seasonOverride.HasValue
                        && !(_api.World.Calendar.OnGetLatitude(pos.Z) > 0.0);

        return GameMath.Smootherstep(Math.Abs(GameMath.CyclicValueDistance(southern ? 6.5 : 0.5, month, 12.0) / 6.0));
    }
}
