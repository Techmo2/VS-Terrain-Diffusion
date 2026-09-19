using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Replaces the lapse rate Vintage Story applies to its climate map with the real one.
///
/// Vintage Story stores one byte per column, takes it to mean the temperature at sea level, and
/// subtracts <c>distToSealevel / 1.5</c> on read. With <c>TemperatureScaleConversion = 4.25</c> that
/// is a fixed 0.157 C per block, which is a physical lapse rate only at one particular vertical
/// scale:
///
/// <code>0.157 C/block / 6.5 C/km = 24.1 m per block</code>
///
/// Anywhere finer than that the game over-cools high ground - at 15 m per block by a factor of 1.6,
/// at 3.75 m per block by 6.4 - and the only way to land the surface on the temperature the model
/// predicted was to write <c>surfaceTemperature + surfaceDistance/1.5</c> into the byte and let the
/// game subtract it straight back off. That worked, but it made the stored value a fiction: it no
/// longer meant anything at sea level, and worse, it <em>saturated</em>. The altitude correction ate
/// the byte's whole range, which is what put a hard floor under how fine the vertical scale could be.
///
/// Scaling the game's lapse rate instead removes both problems at once. The byte goes back to
/// holding a genuine sea-level temperature - coherent with latitude, and small enough that a 0 C
/// summit six kilometres up still fits - and every reader that applies the correction lands on the
/// model's surface temperature, because the correction is now the right size.
///
/// Rainfall gets the same treatment for the same reason. The game adds <c>(y - seaLevel) / 2</c> on
/// read plus a shoreline bonus, neither of which is a fact about weather, and both of which the
/// model has already accounted for in its own precipitation field. At a fine vertical scale that
/// altitude term drives the stored value to zero just as surely. Here it is simply removed, so the
/// stored byte is the precipitation the model predicted and reads back as such at any height.
///
/// Patching <c>Climate</c> reaches the client as well as the server, which is the point: block
/// tinting goes through <c>ChunkTesselator</c> -> <c>Climate.GetAdjustedTemperature</c> and
/// <c>Climate.GetRainFall</c> before it ever reaches the shader, so a client running this mod
/// colours mountains correctly without touching <c>colormap.vsh</c>.
/// </summary>
public static class ClimateScale
{
    private const string HarmonyId = "vsterraindiffusion.climatescale";

    /// <summary>
    /// Lapse rate the stored temperature is defined against, in degrees per kilometre. The global
    /// mean over land is about 6.5; the model's own fitted per-column rates vary either side of it,
    /// but one fixed reference is what makes the round trip through the map exact.
    /// </summary>
    public const float ReferenceLapseCPerKm = 6.5f;

    /// <summary>
    /// Vertical scale at which Vintage Story's own lapse rate is already
    /// <see cref="ReferenceLapseCPerKm"/>, so nothing needs correcting. About 24.1 m per block.
    /// </summary>
    public static float NeutralMetersPerBlock =>
        1f / (1.5f * Climate.TemperatureScaleConversion) / (ReferenceLapseCPerKm / 1000f);

    private static Harmony _harmony;
    private static ILogger _logger;

    /// <summary>
    /// How much of the game's built-in altitude correction to keep. One leaves it alone, which is
    /// what a world at <see cref="NeutralMetersPerBlock"/> wants; below that it shrinks.
    /// </summary>
    public static float LapseScale { get; private set; } = 1f;

    /// <summary>True once the game's climate readers are running on the model's lapse rate.</summary>
    public static bool Installed { get; private set; }

    /// <summary>The correction for a world whose blocks are this tall, in metres.</summary>
    public static float ScaleFor(float metersPerBlockVertical) =>
        Math.Clamp(metersPerBlockVertical / NeutralMetersPerBlock, 0.001f, 8f);

    /// <summary>
    /// The altitude correction, in the units the game applies it in. Shared by the map layer that
    /// writes the byte and the readers that undo it, so the round trip is exact rather than nearly.
    /// </summary>
    public static int ScaleDistance(int distToSeaLevel) =>
        (int)MathF.Round(distToSeaLevel * LapseScale);

    /// <summary>
    /// Puts the correction in place, or updates it. Idempotent: in single player the server and the
    /// client share one process and both will ask for it.
    /// </summary>
    public static void Install(ILogger logger, float lapseScale)
    {
        _logger = logger;
        LapseScale = lapseScale;

        if (Installed) return;

        MethodInfo[] temperatureReaders =
        {
            AccessTools.Method(typeof(Climate), nameof(Climate.GetAdjustedTemperature)),
            AccessTools.Method(typeof(Climate), nameof(Climate.GetScaledAdjustedTemperature)),
            AccessTools.Method(typeof(Climate), nameof(Climate.GetScaledAdjustedTemperatureFloat)),
            AccessTools.Method(typeof(Climate), nameof(Climate.GetScaledAdjustedTemperatureFloatClient))
        };
        MethodInfo rainfall = AccessTools.Method(typeof(Climate), nameof(Climate.GetRainFall));

        if (Array.Exists(temperatureReaders, m => m == null) || rainfall == null)
        {
            throw DiffusionFailure.Fatal(logger,
                "Vintage Story's climate conversions are not where this mod corrects them, so every " +
                "reading taken above sea level would be wrong.");
        }

        try
        {
            _harmony = new Harmony(HarmonyId);
            foreach (MethodInfo reader in temperatureReaders)
            {
                // Last, so that anything which rewrites the distance first - SurfaceClimateCompat
                // swaps a sea-level read for the column's own surface - still has its distance
                // corrected rather than sidestepping the correction.
                _harmony.Patch(reader, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(ClimateScale), nameof(BeforeTemperatureRead)))
                { priority = Priority.Last });
            }
            _harmony.Patch(rainfall, prefix: Method(nameof(BeforeRainfallRead)));
        }
        catch (Exception e)
        {
            Uninstall();
            throw DiffusionFailure.Fatal(logger,
                "Vintage Story's climate conversions could not be patched, so every reading taken " +
                "above sea level would be wrong.", e);
        }

        Installed = true;
        logger?.Notification(
            "[{0}] Climate lapse rate set to {1:0.#} C/km ({2:0.###}x the game's own; it is already " +
            "right at {3:0.#} m per block).",
            DiffusionPaths.ModId, ReferenceLapseCPerKm, LapseScale, NeutralMetersPerBlock);
    }

    private static HarmonyMethod Method(string name) =>
        new(AccessTools.Method(typeof(ClimateScale), name));

    /// <summary>Puts the game's own lapse rate back. Safe to call when nothing is installed.</summary>
    public static void Uninstall()
    {
        try
        {
            _harmony?.UnpatchAll(HarmonyId);
        }
        catch (Exception e)
        {
            _logger?.Warning("[{0}] Could not remove the climate lapse rate patches: {1}",
                DiffusionPaths.ModId, e.Message);
        }

        _harmony = null;
        _logger = null;
        LapseScale = 1f;
        Installed = false;
    }

    /// <summary>
    /// Shrinks the distance the game is about to apply its lapse rate over, which is the same thing
    /// as shrinking the rate. Done by rewriting the argument rather than the result so the game's
    /// own formula, clamps included, stays the one in charge.
    /// </summary>
    private static void BeforeTemperatureRead(ref int distToSealevel)
    {
        distToSealevel = ScaleDistance(distToSealevel);
    }

    /// <summary>
    /// Drops the game's altitude and shoreline adjustments to rainfall. The model's precipitation
    /// field already has orographic rain and coastal humidity in it, measured rather than assumed,
    /// and adding a second helping on read only pushes the byte out of range on high ground.
    /// </summary>
    private static bool BeforeRainfallRead(int rainfall, ref int __result)
    {
        __result = Math.Clamp(rainfall, 0, 255);
        return false;
    }
}
