using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Applies the model's wet and dry seasons where every precipitation reader can see them.
///
/// The seasonal factor used to be applied in the <c>OnGetClimate</c> handler, which only fires for
/// the "now" modes. <see cref="WeatherSystemBase.GetPrecipitation(double, double, double, double)"/>
/// - what farmland, and most mods that care about rain, actually call - reads its climate with
/// <c>WorldGenValues</c> and so never saw it. The two answers to "is it raining" then disagreed:
/// measured over a year at 78% rainfall and 70% CV, 38% of the rain falling in the wet season was
/// invisible to that call, and three quarters of the rain it reported in the dry season fell from a
/// clear sky.
///
/// <c>GetRainCloudness</c> is the one place both paths meet, so the factor is applied there instead.
/// The rainfall is restored afterwards because callers own their <see cref="ClimateCondition"/> and
/// reuse it - <c>BlockEntitySoilNutrition</c> replays a whole hour against one - and scaling in
/// place would compound.
/// </summary>
public static class SeasonalPrecipitationCompat
{
    private const string HarmonyId = "vsterraindiffusion.seasonalprecipitation";

    private static Harmony _harmony;
    private static int _armed;

    /// <summary>
    /// The seasons of each side, resolved once. <c>IModLoader.GetModSystem</c> is a LINQ scan over
    /// every mod system in the game, and this runs on the render thread once a frame.
    /// </summary>
    private static DiffusionSeasons _clientSeasons;
    private static DiffusionSeasons _serverSeasons;

    /// <summary>True while the patch is in place.</summary>
    public static bool Installed => Volatile.Read(ref _armed) > 0;

    /// <summary>
    /// Arms the patch. Called once per side, so a single-player session arms it twice; one patch
    /// serves both and picks the seasons of whichever side is asking.
    /// </summary>
    public static void Install(ICoreAPI api, DiffusionSeasons seasons)
    {
        if (api.Side == EnumAppSide.Client) _clientSeasons = seasons;
        else _serverSeasons = seasons;

        if (Interlocked.Increment(ref _armed) > 1) return;

        MethodInfo cloudness = AccessTools.Method(
            typeof(WeatherSystemBase), nameof(WeatherSystemBase.GetRainCloudness));
        if (cloudness == null)
        {
            Interlocked.Exchange(ref _armed, 0);
            _clientSeasons = null;
            _serverSeasons = null;
            throw DiffusionFailure.Fatal(api.Logger,
                "Vintage Story's precipitation reader is not where this mod applies its seasons, so " +
                "rain would fall in seasons that crops and other mods could not see.");
        }

        try
        {
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(cloudness,
                prefix: Method(nameof(BeforeRainCloudness)),
                finalizer: Method(nameof(AfterRainCloudness)));
        }
        catch (Exception e)
        {
            Uninstall(api);
            throw DiffusionFailure.Fatal(api.Logger,
                "Vintage Story's precipitation reader could not be patched, so rain would fall in " +
                "seasons that crops and other mods could not see.", e);
        }

        Patches info = Harmony.GetPatchInfo(cloudness);
        if (info == null || !info.Owners.Contains(HarmonyId))
        {
            Uninstall(api);
            throw DiffusionFailure.Fatal(api.Logger,
                "The seasonal precipitation patch did not attach, so rain would fall in seasons that " +
                "crops and other mods could not see.");
        }

        api.Logger.Notification("[{0}] Seasonal rainfall applied to every precipitation reader.",
            DiffusionPaths.ModId);
    }

    private static HarmonyMethod Method(string name) =>
        new(AccessTools.Method(typeof(SeasonalPrecipitationCompat), name));

    /// <summary>Removes the patch once every side that armed it has gone. Safe to call unarmed.</summary>
    public static void Uninstall(ICoreAPI api = null)
    {
        if (api == null || api.Side == EnumAppSide.Client) _clientSeasons = null;
        if (api == null || api.Side == EnumAppSide.Server) _serverSeasons = null;

        int armed = Volatile.Read(ref _armed);
        if (armed > 0) armed = Interlocked.Decrement(ref _armed);
        if (armed > 0) return;

        try
        {
            _harmony?.UnpatchAll(HarmonyId);
        }
        catch (Exception)
        {
            // Nothing left to log to, and a patch that outlives the session is harmless: it reads
            // the seasons of whatever world is loaded next, or does nothing if none is.
        }

        _harmony = null;
    }

    /// <summary>
    /// Swings the annual rainfall into the season before vanilla turns it into cloud cover.
    /// </summary>
    private static void BeforeRainCloudness(WeatherSystemBase __instance, ClimateCondition conds,
                                            double posX, double posZ, double totalDays,
                                            out float? __state)
    {
        __state = null;
        if (conds == null || __instance?.api == null) return;

        // The /weather override ignores the climate entirely.
        if (__instance.OverridePrecipitation.HasValue) return;

        DiffusionSeasons seasons =
            __instance.api.Side == EnumAppSide.Client ? _clientSeasons : _serverSeasons;
        float factor = seasons?.SeasonalRainfallFactor(posX, posZ, totalDays) ?? 1f;
        if (factor == 1f) return;

        // Not clamped to [0, 1]: this value exists only to be turned into wgenRain on the next
        // line of the method being patched, and vanilla clamps that itself. Clamping here would cap
        // the wet season at the annual average. The finalizer puts the caller's value back.
        __state = conds.Rainfall;
        conds.Rainfall *= factor;
    }

    /// <summary>
    /// Puts the caller's annual rainfall back. A finalizer rather than a postfix so a throw
    /// upstream cannot leave a scaled value in an object the caller keeps.
    /// </summary>
    private static void AfterRainCloudness(ClimateCondition conds, float? __state)
    {
        if (__state.HasValue) conds.Rainfall = __state.Value;
    }
}
