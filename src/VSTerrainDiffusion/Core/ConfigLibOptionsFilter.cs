using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Cuts the settings screen's inference device and precision lists down to what this machine can
/// run (<see cref="InferenceCompatibility"/>).
///
/// ConfigLib reads <c>config/configlib-patches.json</c> in its own <c>AssetsLoaded</c>, at execute
/// order 0.01, so this rewrites the loaded asset's bytes just before that. On a server ConfigLib then
/// sends the definition it parsed to every client, which is how a remote admin's settings screen
/// offers the server's options rather than those of the machine they are sitting at.
/// </summary>
public class ConfigLibOptionsFilter : ModSystem
{
    private static readonly AssetLocation PatchesAsset = new(DiffusionPaths.ModId, "config/configlib-patches.json");

    /// <summary>Before ConfigLib's 0.01.</summary>
    public override double ExecuteOrder() => 0.005;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void AssetsLoaded(ICoreAPI api)
    {
        if (!ConfigLibCompat.IsPresent(api)) return;

        IAsset asset = api.Assets.TryGet(PatchesAsset);
        if (asset?.Data == null)
        {
            api.Logger.Warning("[{0}] {1} is missing; the settings screen will not be filtered to this machine.",
                DiffusionPaths.ModId, PatchesAsset);
            return;
        }

        InferenceCompatibility compatibility = InferenceCompatibility.Current;
        var allowed = new Dictionary<string, List<string>>
        {
            [nameof(DiffusionConfig.InferenceDevice)] = compatibility.CompatibleDevices(),
            [nameof(DiffusionConfig.CoarsePrecision)] =
                compatibility.CompatiblePrecisions(InferenceCompatibility.AllCoarsePrecisions),
            [nameof(DiffusionConfig.BasePrecision)] =
                compatibility.CompatiblePrecisions(InferenceCompatibility.AllBasePrecisions),
            [nameof(DiffusionConfig.DecoderPrecision)] =
                compatibility.CompatiblePrecisions(InferenceCompatibility.AllDecoderPrecisions)
        };

        JObject root;
        try
        {
            root = JObject.Parse(Encoding.UTF8.GetString(asset.Data));
        }
        catch (Exception e)
        {
            // ConfigLib would fail on the same bytes and show no screen at all, so there is nothing
            // an incompatible option could be picked from.
            api.Logger.Warning("[{0}] Could not read {1}: {2}", DiffusionPaths.ModId, PatchesAsset, e.Message);
            return;
        }

        var filtered = new HashSet<string>();
        if (root["settings"] is JArray settings)
        {
            foreach (JToken setting in settings)
            {
                string code = setting["code"]?.Value<string>();
                if (code == null || !allowed.TryGetValue(code, out List<string> values)) continue;
                setting["values"] = new JArray(values);
                filtered.Add(code);
            }
        }

        // A setting this mod expects to filter but the asset no longer lists would be offered
        // unfiltered, so any drift between the two is worth saying loudly.
        foreach (string code in allowed.Keys)
        {
            if (!filtered.Contains(code))
                api.Logger.Warning("[{0}] {1} has no '{2}' setting to filter.", DiffusionPaths.ModId, PatchesAsset, code);
        }

        asset.Data = Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));

        var summary = new List<string>();
        foreach ((string code, List<string> values) in allowed) summary.Add($"{code} [{string.Join(", ", values)}]");
        api.Logger.Debug("[{0}] Settings screen limited to this machine: {1}", DiffusionPaths.ModId, string.Join("; ", summary));
    }
}
