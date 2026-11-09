using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Lets sneeze's Rivers run on modelled terrain.
///
/// Rivers normally generates the world itself: it prefixes <c>GenTerra.StartServerSide</c> so vanilla
/// never registers a terrain handler, and fills chunks from its own <c>NewGenTerra</c>. This mod
/// installs by finding vanilla's handler and taking its place, so with Rivers alone there is nothing
/// to replace and the game stops. With Watersheds installed the question never arises, because that
/// takes the handover path instead - which is why Rivers appeared to work only alongside it.
///
/// Rivers has a public seam for exactly this. <c>RiversApi.TurnOffGeneration</c> makes it stand
/// aside and leave vanilla's handler in place for someone else to take, and the rest of its API
/// hands out the river network so whoever does the filling can cut the channels. Everything else
/// Rivers does - gravel beaches, boat physics, flow rendering, the map layer - is patched
/// separately and keeps working.
///
/// So the arrangement is the mirror of the Watersheds one: there this mod hands its heights over,
/// here it keeps the filling and asks Rivers where the water goes.
///
/// All of it is by reflection. Rivers is not a build dependency and a world without it must not
/// notice this file exists.
/// </summary>
public static partial class RiversCompat
{
    /// <summary>Whether <see cref="TryInstall"/> has run, successfully or not.</summary>
    private static bool _resolved;

    /// <summary>Whether the per-chunk river samples can be asked for.</summary>
    private static bool _available;

    /// <summary><c>RiversApi.GetRiverSamplesForChunkAndSetChunkRiverData</c>.</summary>
    private static MethodInfo _samplesForChunk;

    /// <summary>Reads a boxed <c>RiverSample.riverDistance</c>.</summary>
    private static System.Func<object, double> _riverDistance;

    /// <summary>Reads a boxed <c>RiverSample.bankFactor</c>.</summary>
    private static System.Func<object, double> _bankFactor;

    /// <summary>Rivers' own valley-width noise, so valleys match the ones it would cut.</summary>
    private static System.Func<double, double, float> _valleyNoise;

    /// <summary>The <c>RiversApi</c> instance, for asking about the network ahead of generation.</summary>
    private static object _instance;

    /// <summary><c>RiversApi.GetRiverRegion</c>.</summary>
    private static MethodInfo _getRiverRegion;

    /// <summary><c>RiversApi.GetSegmentsInRangeOfChunk</c>.</summary>
    private static MethodInfo _getSegments;

    /// <summary><c>RiversApi.SampleRiver</c>.</summary>
    private static MethodInfo _sampleRiver;

    /// <summary>
    /// Rivers' <c>maxValleyWidth</c>: blocks from a river within which the ground is pulled down. Read once
    /// at install; Rivers loads its config before any world generates and never reloads it.
    /// </summary>
    private static double _maxValleyWidth;

    /// <summary>Rivers' <c>valleyStrengthMin</c>.</summary>
    private static float _valleyStrengthMin;

    /// <summary>Rivers' <c>valleyStrengthMax</c>.</summary>
    private static float _valleyStrengthMax;

    /// <summary>
    /// Rivers' <c>noiseExpansion</c>: how far the valley noise is stretched before clamping.
    /// </summary>
    private static float _noiseExpansion;

    /// <summary>Rivers' <c>heightBoost</c>: blocks above sea level a channel is centred on.</summary>
    private static int _heightBoost;

    /// <summary>
    /// Rivers' <c>topFactor</c>: how far above its centre a channel is cut, relative to below.
    /// </summary>
    private static float _topFactor;

    /// <summary>
    /// The world-height steps Rivers scales its ocean test by, kept so the threshold can be
    /// reported as the ocean map value a zone actually has to reach.
    /// </summary>
    private static int _oceanThresholdSteps = 1;

    /// <summary>This bridge's Harmony id, so its patches can be removed alone.</summary>
    private const string HarmonyId = "vsterraindiffusion.rivers";

    /// <summary>The patches on Rivers, or null.</summary>
    private static Harmony _harmony;

    /// <summary>True once the bridge is up and river samples can be asked for.</summary>
    public static bool Installed { get; private set; }

    /// <summary>Whether Rivers is loaded in this world at all.</summary>
    public static bool IsPresent(ICoreAPI api) =>
        api?.ModLoader?.IsModEnabled("rivers") == true || ResolveType() != null;

    /// <summary>Rivers' API type, or null without Rivers.</summary>
    /// <returns><c>Rivers.RiversApi</c>.</returns>
    private static Type ResolveType() => AccessTools.TypeByName("Rivers.RiversApi");

    /// <summary>
    /// Tells Rivers to leave terrain generation alone. Must run before any
    /// <c>StartServerSide</c>, because that is where Rivers decides whether vanilla's generator is
    /// allowed to register - so this is called from the mod's own <c>StartPre</c>.
    /// </summary>
    public static bool StandDown(ICoreAPI api)
    {
        Type riversApi = ResolveType();
        if (riversApi == null) return false;

        PropertyInfo flag = AccessTools.Property(riversApi, "TurnOffGeneration");
        if (flag == null)
        {
            api.Logger.Warning(
                "[{0}] Rivers is installed but does not have the switch this mod uses to take over " +
                "terrain generation from it. One of the two will have to be removed.",
                DiffusionPaths.ModId);
            return false;
        }

        flag.SetValue(null, true);
        return true;
    }

    /// <summary>
    /// Resolves the parts of Rivers needed to carve its channels. Failure is not fatal: the world is
    /// still modelled terrain, it just has no rivers in it, and saying so is better than refusing to
    /// start.
    /// </summary>
    public static void TryInstall(ICoreServerAPI api)
    {
        if (_resolved) return;
        _resolved = true;

        Type riversApi = ResolveType();
        Type configType = AccessTools.TypeByName("Rivers.RiverConfig");
        Type sampleType = AccessTools.TypeByName("Rivers.RiverSample");
        if (riversApi == null || configType == null || sampleType == null) return;

        try
        {
            _samplesForChunk = AccessTools.Method(riversApi, "GetRiverSamplesForChunkAndSetChunkRiverData");
            _riverDistance = FieldReader(sampleType, "riverDistance");
            _bankFactor = FieldReader(sampleType, "bankFactor");

            object config = AccessTools.Property(configType, "Loaded")?.GetValue(null);
            if (_samplesForChunk == null || _riverDistance == null || _bankFactor == null || config == null) return;

            ScaleOceanThresholdForWorldHeight(api, configType, config);
            PatchRiverMouths(api, config);

            _maxValleyWidth = Read<double>(config, "maxValleyWidth");
            _valleyStrengthMin = Read<float>(config, "valleyStrengthMin");
            _valleyStrengthMax = Read<float>(config, "valleyStrengthMax");
            _noiseExpansion = Read<float>(config, "noiseExpansion");
            _heightBoost = Read<int>(config, "heightBoost");
            _topFactor = Read<float>(config, "topFactor");

            // Rivers widens its valleys with a noise field of its own. Borrowing the same instance
            // keeps the valleys identical to the ones its own generator would have cut.
            object noise = AccessTools.Field(riversApi, "valleyNoise")?.GetValue(null);
            MethodInfo getNoise = noise == null
                ? null
                : AccessTools.Method(noise.GetType(), "GetNoise", new[] { typeof(double), typeof(double) });
            if (getNoise == null) return;
            _valleyNoise = (x, z) => (float)getNoise.Invoke(noise, new object[] { x, z });

            // The network itself, for conditioning the model on where the rivers will be. Optional:
            // without it the channels are still cut, just into terrain that did not expect them.
            _instance = AccessTools.Property(riversApi, "Instance")?.GetValue(null);
            _getRiverRegion = AccessTools.Method(riversApi, "GetRiverRegion");
            _getSegments = AccessTools.Method(riversApi, "GetSegmentsInRangeOfChunk");
            _sampleRiver = AccessTools.Method(riversApi, "SampleRiver");

            _available = true;
            Installed = true;
            api.Logger.Notification(
                "[{0}] Rivers is generating this world's rivers; their channels are cut into the model's terrain.",
                DiffusionPaths.ModId);
        }
        catch (Exception e)
        {
            api.Logger.Warning(
                "[{0}] Rivers is installed but could not be read, so this world will have no rivers: {1}",
                DiffusionPaths.ModId, e.Message);
        }
    }

    /// <summary>
    /// Undoes the world-height scaling in Rivers' test for where the sea begins, so rivers reach it.
    ///
    /// <c>RiverRegion.SetZoneOceanicity</c> calls a 256-block zone sea, and stops routing through
    /// it, when the ocean map there passes <c>oceanThreshold</c> (30) after being multiplied by
    /// <c>(MapSizeY / 256) * 0.33333</c> - an integer division that grows with world height. At 256
    /// blocks tall that needs an ocean map value of 90 out of 255; at 1024 it needs 22, so a zone
    /// nine tenths land counts as sea and the river stops a whole zone short of the water. The
    /// threshold is calibrated for a 256-tall world, and this mod encourages much taller ones.
    ///
    /// Scaling the threshold by the same factor puts the test back where it was written, whatever
    /// the world height. A value the player has changed is scaled too, so their intent survives.
    /// </summary>
    private static void ScaleOceanThresholdForWorldHeight(ICoreServerAPI api, Type configType, object config)
    {
        FieldInfo threshold = AccessTools.Field(configType, "oceanThreshold");
        if (threshold == null) return;

        int steps = Math.Max(1, api.WorldManager.MapSizeY / 256);
        _oceanThresholdSteps = steps;
        if (steps == 1) return;

        float shipped = (float)threshold.GetValue(config);
        threshold.SetValue(config, shipped * steps);

        api.Logger.Notification(
            "[{0}] Rivers reaches the sea at an ocean map value of {1:0} however tall the world is; " +
            "at this height its own test would have settled for {2:0} and left rivers ending inland.",
            DiffusionPaths.ModId, shipped * steps / ((float)steps * 0.33333f), shipped / ((float)steps * 0.33333f));
    }

    /// <summary>Compiles a field getter once, so reading a sample is not a reflection call.</summary>
    private static System.Func<object, double> FieldReader(Type owner, string field)
    {
        FieldInfo info = AccessTools.Field(owner, field);
        if (info == null) return null;

        ParameterExpression boxed = Expression.Parameter(typeof(object), "sample");
        Expression read = Expression.Field(Expression.Unbox(boxed, owner), info);
        return Expression.Lambda<System.Func<object, double>>(
            Expression.Convert(read, typeof(double)), boxed).Compile();
    }

    /// <summary>Reads a field of a Rivers object by name, converted.</summary>
    /// <typeparam name="T">The type wanted.</typeparam>
    /// <param name="instance">The object.</param>
    /// <param name="field">The field's name.</param>
    /// <returns>The value.</returns>
    private static T Read<T>(object instance, string field) =>
        (T)Convert.ChangeType(AccessTools.Field(instance.GetType(), field).GetValue(instance), typeof(T));

    /// <summary>
    /// Takes Rivers' own terrain generator out of the worldgen pass.
    ///
    /// <c>TurnOffGeneration</c> only governs its patches on vanilla's <c>GenTerra</c> - whether
    /// vanilla is allowed to register a handler at all. <c>NewGenTerra.StartServerSide</c>
    /// registers its own Terrain-pass handler regardless, so standing Rivers down leaves both it
    /// and this mod filling every column: the world comes out as the union of two landscapes, with
    /// only one generator's heightmaps recorded, so the surface layers are buried and what a player
    /// walks on is bare stone with the other terrain's peaks sticking through it.
    ///
    /// Only its terrain fill is removed. The river network, the boulders, the gravel and the block
    /// layer patches are registered separately and all keep working - this mod writes the per-chunk
    /// river data its boats and rendering read back.
    /// </summary>
    /// <returns>True if a handler was removed.</returns>
    public static bool RemoveTerrainHandler(ICoreServerAPI api,
                                            System.Collections.Generic.List<ChunkColumnGenerationDelegate> terrainPass)
    {
        Type newGenTerra = AccessTools.TypeByName("Rivers.NewGenTerra");
        if (newGenTerra == null) return false;

        int removed = terrainPass.RemoveAll(d => d?.Target != null && newGenTerra.IsInstanceOfType(d.Target));
        if (removed == 0) return false;

        api.Logger.Notification(
            "[{0}] Took Rivers' own terrain generator out of the world generator; this mod fills the " +
            "chunks and cuts its rivers into them.", DiffusionPaths.ModId);
        return true;
    }

    /// <summary>Rivers' running config (<c>RiverConfig.Loaded</c>), or null without Rivers.</summary>
    /// <returns>The config.</returns>
    private static object LoadedConfig() =>
        AccessTools.Property(AccessTools.TypeByName("Rivers.RiverConfig"), "Loaded")?.GetValue(null);

    /// <summary>Rivers' config as it is running, as JSON, or null without Rivers.</summary>
    public static string ConfigJson()
    {
        object config = LoadedConfig();
        return config == null ? null : Newtonsoft.Json.JsonConvert.SerializeObject(config);
    }

    /// <summary>Reads an integer from Rivers' running config.</summary>
    /// <param name="field">The field's name.</param>
    /// <returns>The value.</returns>
    private static int ReadInt(string field) => Read<int>(LoadedConfig(), field);

    /// <summary>
    /// Removes the patches and forgets everything resolved, so the next world starts clean.
    /// </summary>
    public static void Uninstall()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        _modelSea = null;
        _api = null;
        _instance = null;
        _resolved = false;
        _available = false;
        Installed = false;
        _samplesForChunk = null;
        _riverDistance = null;
        _bankFactor = null;
        _valleyNoise = null;
    }
}
