using System;
using System.Globalization;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// The world's climate settings, and the one place the model's temperature and rainfall become what
/// the world reads.
/// </summary>
public sealed partial class DiffusionWorldSettings
{
    /// <summary>
    /// The world's "Starting climate" choice, or null when the search for it is switched off or the
    /// setting holds something unrecognised. The spawn search uses it to pick where to put the
    /// player; nothing else reads it.
    /// </summary>
    public StartingClimate? StartingClimate { get; private set; }

    /// <summary>
    /// The world's "Global temperature" and "Global precipitation" settings, as read. What the
    /// model makes of them is decided by the plan they were turned into, not here.
    /// </summary>
    public ClimateShift Climate { get; private set; }

    /// <summary>
    /// The share of <c>globalTemperature</c> the conditioning did not deliver, applied to the
    /// model's output. One when the model was asked for all of it, or when neither is in play.
    /// </summary>
    public float TemperatureCorrection { get; private set; } = 1f;

    /// <summary>The same for <c>globalPrecipitation</c>.</summary>
    public float RainfallCorrection { get; private set; } = 1f;

    /// <summary>
    /// Where the equator and the poles are, and what climate that implies. Never null;
    /// <see cref="LatitudeBands.None"/> for a world with no latitude gradient.
    /// </summary>
    public LatitudeBands Latitude { get; private set; } = LatitudeBands.None;

    /// <summary>
    /// A model temperature as the world will actually read it, once whatever is left of the
    /// world's global setting and the config's offset are in. Every column's temperature passes
    /// through here on its way into a tile, so the climate map, the freeze line, the surface rules
    /// and the spawn search all read one number.
    ///
    /// The Z coordinate is what decides latitude in Vintage Story, and the latitude band the model
    /// could not be conditioned all the way into is added here.
    /// </summary>
    public float WorldTemperature(float modelTemperatureC, int blockZ)
    {
        // Latitude first, because the world's global setting scales the whole climate including
        // its north-south gradient: half a world is half its tropics and half its ice.
        float banded = modelTemperatureC + Latitude.TemperatureOffsetC(blockZ);
        float celsius = ClimateShift.ApplyTemperature(banded, TemperatureCorrection)
                        + _shaping.TemperatureOffsetC;

        // The far ends of the temperature setting ask for climates that are not on any scale the
        // game has: four times a 15 C world is 120 C. Vanilla arrives at the same place from the
        // other direction, its climate byte having saturated, so the honest reading of the setting
        // there is the top of the scale rather than a number nothing downstream can hold.
        return Math.Clamp(celsius, ClimateShift.ScaleFloorC, ClimateShift.ScaleCeilingC);
    }

    /// <summary>Annual rainfall as the world will read it, in millimetres.</summary>
    public float WorldPrecipitation(float modelPrecipitationMm, int blockZ)
        => modelPrecipitationMm * RainfallCorrection * Latitude.RainfallFactor(blockZ);

    /// <summary>
    /// Highest ground at <paramref name="temperatureC"/> whose temperature the climate map can still
    /// represent, in metres.
    ///
    /// The map holds a sea-level temperature in one byte, which tops out at 40 C, and a summit's
    /// sea-level equivalent is its own temperature plus the lapse rate over its height. Ground high
    /// and warm enough for that sum to pass 40 C reads back colder than the model said.
    ///
    /// This used to depend on the vertical scale, and badly - the byte also had to carry the game's
    /// whole altitude correction, which is what ruled out fine resolutions. Since
    /// <see cref="ClimateScale"/> moved that correction out of the byte, the ceiling is a property
    /// of the lapse rate alone and the vertical scale is free.
    /// </summary>
    public float TemperatureCeilingMeters(float temperatureC)
    {
        float headroom = 40f - temperatureC;
        return headroom <= 0f ? 0f : headroom / (ClimateScale.ReferenceLapseCPerKm * AltitudeCooling) * 1000f;
    }
}
