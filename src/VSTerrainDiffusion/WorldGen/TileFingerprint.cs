using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Everything that decides what a world's stored tiles contain, one hashed entry per input. The tile
/// database (<see cref="TerrainTileStore"/>) empties itself when any entry changes, and logs which.
/// </summary>
/// <remarks>
/// <para>Included: the terrain version and the models, the seed, the world's terrain settings, this
/// mod's terrain config, the settings as resolved (calibration included), and the two things a tile
/// takes from outside this mod - the ocean map, read the way the model reads it, and Rivers' version
/// and config.</para>
/// <para>Left out: settings that only decide how fast tiles are made, and other mods, which reach a
/// tile only through the ocean map. Also the mod's version (<see cref="TerrainTileStore.TerrainVersion"/>
/// stands for the code that shapes a tile) and the device and precisions the models ran at, so a
/// world's tiles can be pregenerated on one machine and served from another.</para>
/// <para>The ocean map is sampled near the model's origin, so a mod that reshapes it only far from
/// there is not seen; that is what <c>/tdiff tiles clear</c> is for.</para>
/// </remarks>
public static class TileFingerprint
{
    /// <summary>
    /// The game's own world settings a tile depends on: the climate ones this mod reads, and the two
    /// that shape the ocean map. Every other vanilla setting is gameplay, and changing it with
    /// /worldconfig keeps the tiles.
    /// </summary>
    private static readonly string[] TileShapingVanillaCodes =
    {
        "globalTemperature", "globalPrecipitation", "polarEquatorDistance", "startingClimate", "worldClimate",
        "landcover", "oceanscale"
    };

    /// <summary>Mod config fields that never reach a tile: the translocator search and the spawn search.</summary>
    private static readonly string[] TileNeutralWorldGenFields =
    {
        nameof(WorldGenConfig.TranslocatorSearchTimeoutSeconds), nameof(WorldGenConfig.TranslocatorPeekPauseSeconds),
        nameof(WorldGenConfig.TranslocatorMaxRangeBlocks), nameof(WorldGenConfig.TranslocatorStoredTerrainOnly),
        nameof(WorldGenConfig.StartingClimateSearch),
        nameof(WorldGenConfig.StartingClimateSearchRadiusBlocks), nameof(WorldGenConfig.StartingClimateNorthSouthCost),
        nameof(WorldGenConfig.SpawnMaxBlocksAboveSea)
    };

    /// <summary>Builds the fingerprint of the world being loaded.</summary>
    /// <param name="api">The server API.</param>
    /// <param name="settings">The world's resolved settings, calibration applied.</param>
    /// <param name="landmask">The ocean map as the coarse model reads it, or null when the model draws the coast.</param>
    /// <param name="conditioningSites">Story structure conditioning, or null when that experiment is off.</param>
    /// <returns>Input name to the first 16 hex digits of the SHA-256 of its value.</returns>
    public static Dictionary<string, string> Build(ICoreServerAPI api, DiffusionWorldSettings settings,
        ILandmaskSource landmask, IReadOnlyList<CoarseConditioningSite> conditioningSites)
    {
        var inputs = new Dictionary<string, string>
        {
            ["terrain version"] = TerrainTileStore.TerrainVersion.ToString(CultureInfo.InvariantCulture),
            ["models"] = ModelAssetManager.ModelIdentity,
            ["seed"] = api.WorldManager.Seed.ToString(CultureInfo.InvariantCulture),
            ["world settings"] = WorldSettings(api.WorldManager.SaveGame.WorldConfiguration),
            ["terrain config"] = TerrainConfig(),
            ["resolved settings"] = ResolvedSettings(api, settings),
            ["ocean map"] = OceanMap(landmask),
            ["rivers"] = RiversCompat.Installed
                ? (api.ModLoader.GetMod("rivers")?.Info.Version ?? "") + "\n" + (RiversCompat.ConfigJson() ?? "")
                : "none"
        };

        // Only when the experiment is on, so every other world keeps its fingerprint.
        if (conditioningSites != null)
            inputs["story structures"] = CoarseConditioningSite.Fingerprint(conditioningSites);

        return inputs.ToDictionary(entry => entry.Key, entry => Hash(entry.Value));
    }

    /// <summary>
    /// The world settings that shape a tile, sorted, as <c>code=json</c> lines: this mod's own except
    /// the speed-only and water ones, and <see cref="TileShapingVanillaCodes"/>.
    /// </summary>
    private static string WorldSettings(ITreeAttribute worldConfiguration)
    {
        var neutral = new HashSet<string>(InferenceSettings.AllCodes, StringComparer.Ordinal);
        neutral.UnionWith(WorldValuePins.TileNeutralCodes);

        var shapingEntries = new List<string>();
        foreach (KeyValuePair<string, IAttribute> entry in worldConfiguration)
        {
            bool shaping = entry.Key.StartsWith(InferenceSettings.CodePrefix, StringComparison.Ordinal)
                ? !neutral.Contains(entry.Key)
                : Array.IndexOf(TileShapingVanillaCodes, entry.Key) >= 0;
            if (!shaping) continue;

            // A value pinned onto an older world as what it always had is no change to its tiles.
            if (WorldValuePins.LegacyValues.TryGetValue(entry.Key, out string legacy)
                && entry.Value?.GetValue()?.ToString() == legacy) continue;

            shapingEntries.Add(entry.Key + "=" + entry.Value?.ToJsonToken());
        }
        shapingEntries.Sort(StringComparer.Ordinal);
        return string.Join("\n", shapingEntries);
    }

    /// <summary>The <c>worldGen</c> mod config as JSON, minus <see cref="TileNeutralWorldGenFields"/>.</summary>
    private static string TerrainConfig()
    {
        JObject worldGen = JObject.FromObject(DiffusionConfig.Instance.WorldGen);
        foreach (string field in TileNeutralWorldGenFields) worldGen.Remove(field);
        return worldGen.ToString(Formatting.None);
    }

    /// <summary>The resolved settings as JSON, or their description if they do not serialise.</summary>
    private static string ResolvedSettings(ICoreServerAPI api, DiffusionWorldSettings settings)
    {
        string resolved;
        try
        {
            resolved = JsonConvert.SerializeObject(settings);
        }
        catch (Exception)
        {
            resolved = settings.Describe();
        }
        api.Logger.VerboseDebug("[{0}] Tile database fingerprint, resolved settings: {1}", DiffusionPaths.ModId, resolved);
        return resolved;
    }

    /// <summary>
    /// 16 by 16 coarse cells, about 120 km across, of the ocean map as the coarse model is handed it,
    /// as hex float bits; "none" without a landmask.
    /// </summary>
    private static string OceanMap(ILandmaskSource landmask)
    {
        float[] sea = landmask?.SeaFraction(-8, -8, 8, 8);
        return sea == null ? "none" : Convert.ToHexString(MemoryMarshal.AsBytes(sea.AsSpan()));
    }

    /// <summary>The first 16 hex digits of the SHA-256 of <paramref name="value"/>'s UTF-8 bytes.</summary>
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
