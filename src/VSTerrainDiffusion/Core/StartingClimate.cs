using System;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// One of Vintage Story's "Starting climate" choices, as a temperature range in degrees Celsius.
///
/// Vanilla implements the setting by shifting its climate map so that the chosen band lands on the
/// map centre, which is possible because vanilla's climate is a function of position it is free to
/// redefine. The model's climate is not: it predicts a particular world, and warping it to put a
/// desert at the origin would undo the thing the mod exists to do. Moving the player instead gets
/// the same result - you start in the climate you asked for - without touching the world.
///
/// The ranges are vanilla's own, taken from the labels on the dropdown, so picking "Cool (-5 to
/// 1 C)" here means the same thing it means in an unmodded world.
/// </summary>
public readonly struct StartingClimate
{
    /// <summary>The world config value, for logs and messages.</summary>
    public readonly string Code;

    /// <summary>Human-readable name, matching the world creation screen.</summary>
    public readonly string Label;

    public readonly float MinimumC;
    public readonly float MaximumC;

    private StartingClimate(string code, string label, float minimumC, float maximumC)
    {
        Code = code;
        Label = label;
        MinimumC = minimumC;
        MaximumC = maximumC;
    }

    /// <summary>
    /// The five vanilla bands. Note that they do not tile the number line - there is a gap between
    /// "temperate" ending at 14 C and "warm" starting at 19 C - so a place can match none of them,
    /// which is why the search falls back to whichever band edge is nearest.
    /// </summary>
    private static readonly StartingClimate[] All =
    {
        new("hot", "hot", 28f, 32f),
        new("warm", "warm", 19f, 23f),
        new("temperate", "temperate", 6f, 14f),
        new("cool", "cool", -5f, 1f),
        new("icy", "icy", -15f, -10f)
    };

    /// <summary>
    /// Parses a <c>startingClimate</c> world config value, or null if it is not one of the five.
    /// </summary>
    public static StartingClimate? Parse(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        string trimmed = code.Trim().ToLowerInvariant();
        foreach (StartingClimate band in All)
        {
            if (band.Code == trimmed) return band;
        }
        return null;
    }

    public bool Contains(float temperatureC) => temperatureC >= MinimumC && temperatureC <= MaximumC;

    /// <summary>
    /// How far outside the band a temperature falls, in degrees; zero inside it. Used to rank
    /// near misses when the world has no land in the band at all.
    /// </summary>
    public float Miss(float temperatureC) => temperatureC < MinimumC
        ? MinimumC - temperatureC
        : temperatureC > MaximumC ? temperatureC - MaximumC : 0f;

    public override string ToString() =>
        $"{Label} ({MinimumC:0.#} to {MaximumC:0.#} C)";
}
