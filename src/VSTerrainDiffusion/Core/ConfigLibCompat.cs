using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Optional integration with ConfigLib, which gives every mod that describes its settings an
/// in-game settings screen.
///
/// There is no compile-time or run-time dependency on ConfigLib here, and there deliberately is no
/// C# API call either. ConfigLib picks up <c>assets/vsterraindiffusion/config/configlib-patches.json</c>
/// on its own and edits <c>ModConfig/vsterraindiffusion.json</c> in place - the same file this mod
/// reads at startup, keys and nesting untouched - so with ConfigLib absent nothing changes and with
/// it present the file simply gains a GUI. All this class does is listen on the event bus so the
/// handful of settings that can take effect without a restart do.
///
/// Because ConfigLib owns the file once it is installed, nothing here writes
/// <see cref="DiffusionConfig.Instance"/> back to disk, and nothing here applies a world-shaping
/// setting to the running world: terrain tiles are cached, so a value that changed halfway through
/// a session would leave a seam between the chunks generated before it and the ones after. Those
/// settings are read from the file on the next start, which is what the GUI's hover text says.
/// </summary>
public static class ConfigLibCompat
{
    private const string ModDomain = "configlib";

    private static ICoreServerAPI _api;
    private static EventBusListenerDelegate _listener;

    /// <summary>Whether ConfigLib is installed, and therefore whether the settings screen exists.</summary>
    public static bool IsPresent(ICoreAPI api) => api.ModLoader.IsModEnabled(ModDomain);

    public static void Install(ICoreServerAPI api)
    {
        if (_listener != null || !IsPresent(api)) return;

        _api = api;
        _listener = OnSettingChanged;

        // Pushed on the server bus both when a client with controlserver saves the settings screen
        // and when the server applies the change to its own copy, so the value has already landed
        // in ConfigLib by the time this runs on either path.
        api.Event.RegisterEventBusListener(_listener, 0.5, $"configlib:{DiffusionPaths.ModId}:setting-changed");

        api.Logger.Notification("[{0}] ConfigLib is installed; the settings screen can edit {1}.",
            DiffusionPaths.ModId, DiffusionPaths.ModId + ".json");
    }

    public static void Uninstall()
    {
        if (_listener == null) return;
        _api?.Event.UnregisterEventBusListener(_listener);
        _listener = null;
        _api = null;
    }

    private static void OnSettingChanged(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (data is not ITreeAttribute tree) return;

        // The setting code is the path into the config file, so a nested one arrives as
        // "WorldGen/ForestDensityMultiplier".
        string code = tree.GetAsString("setting");
        if (string.IsNullOrEmpty(code)) return;

        try
        {
            Apply(code, tree);
        }
        catch (Exception e)
        {
            _api?.Logger.Warning("[{0}] Could not apply the '{1}' setting from ConfigLib: {2}",
                DiffusionPaths.ModId, code, e.Message);
        }
    }

    private static void Apply(string code, ITreeAttribute tree)
    {
        ILogger logger = _api?.Logger;

        switch (code)
        {
            case nameof(DiffusionConfig.GpuUtilizationPercent):
            {
                int percent = tree.GetAsInt("value", DiffusionConfig.Instance.GpuUtilizationPercent);
                InferenceThrottle.UtilizationPercent = percent;
                DiffusionConfig.Instance.GpuUtilizationPercent = InferenceThrottle.UtilizationPercent;
                logger?.Notification("[{0}] Inference is now limited to {1}.",
                    DiffusionPaths.ModId, InferenceThrottle.Describe());
                return;
            }

            case nameof(DiffusionConfig.VerboseInference):
            {
                bool verbose = tree.GetAsBool("value", DiffusionConfig.Instance.VerboseInference);
                DiffusionConfig.Instance.VerboseInference = verbose;
                logger?.Notification("[{0}] Verbose inference logging {1}.",
                    DiffusionPaths.ModId, verbose ? "on" : "off");
                return;
            }

            default:
                logger?.Notification(
                    "[{0}] '{1}' was changed in the settings screen and saved to {2}. It is read when the " +
                    "world generator starts, so it takes effect the next time the server starts.",
                    DiffusionPaths.ModId, code, DiffusionPaths.ModId + ".json");
                return;
        }
    }
}
