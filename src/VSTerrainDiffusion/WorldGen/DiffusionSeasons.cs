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
    /// Relative amplitude of the wet season per unit coefficient of variation, again for a sine.
    /// </summary>
    private const float WetSeasonPerCv = 1.4142136f;

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

        api.Event.OnGetClimate += OnGetClimate;
    }

    private void OnGetClimate(ref ClimateCondition climate, BlockPos pos, EnumGetClimateMode mode, double totalDays)
    {
        // World generation wants annual averages; only the "now" and "for this date" modes are
        // asking what the weather is actually doing.
        if (mode == EnumGetClimateMode.WorldGenValues) return;

        SeasonalityMap.Sample? sample = SeasonalityMap.At(_api.World.BlockAccessor, pos);
        if (sample == null) return;

        IGameCalendar calendar = _api.World.Calendar;
        double yearRel = totalDays / calendar.DaysPerYear % 1.0;
        double summer = SummerWeight(yearRel, pos);

        if (_config.SeasonalTemperature)
        {
            float swing = SwingPerSigma * sample.Value.TemperatureSigmaC * _config.SeasonalTemperatureStrength;
            double temperature = climate.WorldGenTemperature - swing / 2.0 + swing * summer;

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

        if (_config.SeasonalPrecipitation)
        {
            climate.Rainfall = GameMath.Clamp(
                climate.Rainfall * RainfallFactor(sample.Value.PrecipitationCv, summer), 0f, 1f);
        }
    }

    /// <summary>
    /// How much of a place's usual rain is falling at this point in the year. Wet season in
    /// summer, which is where monsoons and continental convective rain sit. Clamped at zero rather
    /// than allowed to go negative: a dry season is no rain, not anti-rain, and a coefficient of
    /// variation much above 70% would otherwise overshoot.
    /// </summary>
    private float RainfallFactor(float precipitationCv, double summer)
    {
        float cv = precipitationCv / 100f * _config.SeasonalPrecipitationStrength;
        return (float)Math.Max(0.0, 1.0 + WetSeasonPerCv * cv * (2.0 * summer - 1.0));
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

        // Without a latitude temperature gradient, two hemispheres only mean the calendar
        // disagrees with itself halfway across the map, so this is off unless asked for.
        bool southern = _config.SeasonHemispheres
                        && !seasonOverride.HasValue
                        && _api.World.Calendar.OnGetLatitude(pos.Z) < 0.0;

        return GameMath.Smootherstep(Math.Abs(GameMath.CyclicValueDistance(southern ? 6.5 : 0.5, month, 12.0) / 6.0));
    }
}
