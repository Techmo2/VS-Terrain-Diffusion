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
public static class RiversCompat
{
    private static bool _resolved;
    private static bool _available;

    private static MethodInfo _samplesForChunk;
    private static System.Func<object, double> _riverDistance;
    private static System.Func<object, double> _bankFactor;
    private static System.Func<double, double, float> _valleyNoise;

    private static object _instance;
    private static MethodInfo _getRiverRegion;
    private static MethodInfo _getSegments;
    private static MethodInfo _sampleRiver;
    private static System.Func<object, double> _sampleDistance;

    // Read once at install: Rivers loads its config before any world generates and never reloads it.
    private static double _maxValleyWidth;
    private static float _valleyStrengthMin;
    private static float _valleyStrengthMax;
    private static float _noiseExpansion;
    private static int _heightBoost;
    private static float _topFactor;

    /// <summary>
    /// The world-height steps Rivers scales its ocean test by, kept so the threshold can be
    /// reported as the ocean map value a zone actually has to reach.
    /// </summary>
    private static int _oceanThresholdSteps = 1;

    /// <summary>True once the bridge is up and river samples can be asked for.</summary>
    public static bool Installed { get; private set; }

    /// <summary>Whether Rivers is loaded in this world at all.</summary>
    public static bool IsPresent(ICoreAPI api) =>
        api?.ModLoader?.IsModEnabled("rivers") == true || ResolveType() != null;

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
            _sampleDistance = _riverDistance;

            _available = true;
            Installed = true;
            api.Logger.Notification(
                "[{0}] Rivers is generating this world's rivers; the model's landscape is valleyed " +
                "and cut to carry them.", DiffusionPaths.ModId);
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

    private static T Read<T>(object instance, string field) =>
        (T)Convert.ChangeType(AccessTools.Field(instance.GetType(), field).GetValue(instance), typeof(T));

    /// <summary>
    /// The river network over one chunk, as a boxed <c>RiverSample[1024]</c>, or null when Rivers is
    /// not carving this world. Also writes the flow vectors and river distances that its boat
    /// physics and rendering read back, so this has to be called for every chunk generated.
    /// </summary>
    public static Array SamplesForChunk(int chunkX, int chunkZ, IServerChunk[] chunks)
    {
        if (!_available) return null;

        try
        {
            return (Array)_samplesForChunk.Invoke(null, new object[] { chunkX, chunkZ, chunks });
        }
        catch (Exception)
        {
            // A river region that will not build is Rivers' business; the chunk still gets terrain.
            return null;
        }
    }

    /// <summary>
    /// How far from a channel Rivers strips the soil to make a shore, from
    /// <c>BlockLayersPatches.GetRiverPower</c>: none at the water's edge, full soil again ten
    /// blocks out. The ground under that strip has to be at the water, or the bare rock it leaves
    /// is a pavement along the top of a cliff rather than a beach.
    /// </summary>
    private const double ShoreBlocks = 10.0;

    /// <summary>One column's river network, in the terms the terrain generator needs.</summary>
    public readonly struct Sample
    {
        /// <summary>Blocks to the nearest river; 0 or less is inside one.</summary>
        public readonly double Distance;

        /// <summary>Half the channel's depth, as a fraction of the world above sea level.</summary>
        public readonly double BankFactor;

        public Sample(double distance, double bankFactor)
        {
            Distance = distance;
            BankFactor = bankFactor;
        }

        public bool InValley(double maxValleyWidth) => Distance < maxValleyWidth;
    }

    public static Sample At(Array samples, int index)
    {
        object boxed = samples.GetValue(index);
        return new Sample(_riverDistance(boxed), _bankFactor(boxed));
    }

    public static double MaxValleyWidth => _maxValleyWidth;

    /// <summary>
    /// How much of the model's own height survives at a column, from 0 in the channel to 1 outside
    /// the valley.
    ///
    /// This is <c>RiversApi.ModifyLandformWeights</c> rearranged. That bends vanilla's landform
    /// weights towards a river landform whose key positions sit at sea level; there are no
    /// landforms here, so the same curve is applied to the height directly and the result is the
    /// same shape of valley.
    /// </summary>
    public static float ModelWeight(in Sample sample, int worldX, int worldZ)
    {
        if (!_available || sample.Distance >= _maxValleyWidth) return 1f;

        double noise = _valleyNoise(worldX, worldZ);
        noise = Math.Clamp(noise * _noiseExpansion, -1.0, 1.0);
        noise = (noise + 1.0) / 2.0;
        noise = Map(noise, 0.0, 1.0, 1f - _valleyStrengthMin, 1f - _valleyStrengthMax);
        if (noise < 0.02) noise = 0.02;

        double weight = Lerp(noise, 1.0, InverseLerp(sample.Distance, 0.0, _maxValleyWidth));
        weight *= weight;

        // Rivers' own curve leaves up to a third of the original height standing right at the
        // channel, which is harmless when the land beside it was already near sea level and is a
        // cliff when it was a hillside. Over the strip Rivers bares anyway, bring the ground all
        // the way down so that strip is the shore.
        return (float)(weight * InverseLerp(sample.Distance, 0.0, ShoreBlocks));
    }

    /// <summary>
    /// The level a valley floor is pulled down to: sea level, which is where Rivers' own river
    /// landform pins the ground beside a channel (<c>TerrainYKeyPositions[0]</c> is 22/51 of the
    /// world, the same fraction the game puts the sea at).
    ///
    /// Not <c>seaLevel + heightBoost</c>, which is where the *carve* is centred - that is the
    /// middle of the channel, not the top of its bank. Aiming the banks there left them nine
    /// blocks above the water, and since Rivers strips the soil within ten blocks of a channel to
    /// make a shore, the bare strip came out as a stone pavement along a clifftop instead of a
    /// beach at the water's edge.
    /// </summary>
    public static int ValleyFloorY(int seaLevel) => seaLevel;

    /// <summary>
    /// The highest block still solid beneath a channel - the river bed. Below this the column is
    /// ordinary ground, which is what lets the chunk fill still be done in bulk.
    /// </summary>
    public static int ChannelFloorY(in Sample sample, int seaLevel, int mapSizeY)
    {
        int bankHalf = (int)(sample.BankFactor * (mapSizeY - seaLevel));
        return seaLevel + _heightBoost - bankHalf;
    }

    /// <summary>
    /// Whether a block is inside the channel and should be left out. Mirrors
    /// <c>RiversApi.ShouldBeCarved</c> rather than calling it, because this is asked once per block
    /// rather than once per column.
    /// </summary>
    public static bool Carved(in Sample sample, int y, int seaLevel, int mapSizeY)
    {
        if (sample.Distance > 0.0) return false;

        int bankHalf = (int)(sample.BankFactor * (mapSizeY - seaLevel));
        int riverY = seaLevel + _heightBoost;
        return y > riverY - bankHalf && y < riverY + bankHalf * _topFactor;
    }

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

    /// <summary>
    /// A readable account of the river network around a position: how much of it Rivers calls sea,
    /// how many rivers it seeded there and how far each one got.
    ///
    /// For working out why rivers come up short. The two answers look completely different - too
    /// few coastal zones means nothing is being seeded, while plenty of rivers with two or three
    /// nodes each means they are being cut off as they grow.
    /// </summary>
    public static string DescribeRegion(int blockX, int blockZ)
    {
        if (!CanSampleNetwork) return "Rivers is not supplying a network for this world.";

        try
        {
            object region = _getRiverRegion.Invoke(_instance, new object[] { blockX >> 5, blockZ >> 5 });
            if (region == null) return "Rivers has no region there.";

            Array zones = AccessTools.Field(region.GetType(), "zones")?.GetValue(region) as Array;
            var rivers = AccessTools.Field(region.GetType(), "rivers")?.GetValue(region) as System.Collections.IEnumerable;
            if (zones == null || rivers == null) return "Rivers' region does not carry the fields this reads.";

            Type zoneType = null;
            int ocean = 0, coastal = 0;
            double farthest = 0;
            foreach (object zone in zones)
            {
                if (zone == null) continue;
                zoneType ??= zone.GetType();
                if ((bool)AccessTools.Field(zoneType, "oceanZone").GetValue(zone)) ocean++;
                if ((bool)AccessTools.Field(zoneType, "coastalZone").GetValue(zone)) coastal++;
                double d = (double)AccessTools.Field(zoneType, "oceanDistance").GetValue(zone);
                if (d > farthest) farthest = d;
            }

            var nodeCounts = new System.Collections.Generic.List<int>();
            Type riverType = null, nodeType = null;
            double longest = 0;
            foreach (object river in rivers)
            {
                riverType ??= river.GetType();
                var nodes = AccessTools.Field(riverType, "nodes").GetValue(river) as System.Collections.IList;
                if (nodes == null) continue;
                nodeCounts.Add(nodes.Count);

                double length = 0;
                foreach (object node in nodes)
                {
                    nodeType ??= node.GetType();
                    object a = AccessTools.Field(nodeType, "startPos").GetValue(node);
                    object b = AccessTools.Field(nodeType, "endPos").GetValue(node);
                    length += Distance(a, b);
                }
                if (length > longest) longest = length;
            }

            nodeCounts.Sort();
            int total = zones.Length;
            string nodes4 = nodeCounts.Count == 0
                ? "none"
                : $"{nodeCounts[0]} to {nodeCounts[nodeCounts.Count - 1]}, median {nodeCounts[nodeCounts.Count / 2]}";

            return
                $"Rivers region at ({blockX}, {blockZ})\n" +
                $"  zones: {total} total, {ocean} sea ({100.0 * ocean / Math.Max(1, total):0.0}%), {coastal} coastal\n" +
                $"  a zone counts as sea at an ocean map value of {OceanValueNeeded():0} of 255 " +
                $"(oceanThreshold {Read<float>(LoadedConfig(), "oceanThreshold"):0}); higher means less sea and more land to run through\n" +
                $"  farthest any zone is from the sea: {farthest:0} blocks\n" +
                $"  rivers seeded and kept: {nodeCounts.Count}, nodes {nodes4} (minNodes discards below {ReadInt("minNodes")})\n" +
                $"  longest river here: {longest:0} blocks, cap is maxNodes {ReadInt("maxNodes")} x up to " +
                $"{ReadInt("minLength") + ReadInt("lengthVariation")} = {ReadInt("maxNodes") * (ReadInt("minLength") + ReadInt("lengthVariation"))}\n" +
                $"  downhillError {ReadInt("downhillError")} - how many nodes may fail to lead away from the sea before a river stops";
        }
        catch (Exception e)
        {
            return "Could not read Rivers' region: " + e.Message;
        }
    }

    private static double Distance(object a, object b)
    {
        Type v = a.GetType();
        double ax = (double)AccessTools.Field(v, "X").GetValue(a), az = (double)AccessTools.Field(v, "Y").GetValue(a);
        double bx = (double)AccessTools.Field(v, "X").GetValue(b), bz = (double)AccessTools.Field(v, "Y").GetValue(b);
        return Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
    }

    private static object LoadedConfig() =>
        AccessTools.Property(AccessTools.TypeByName("Rivers.RiverConfig"), "Loaded")?.GetValue(null);

    private static int ReadInt(string field) => Read<int>(LoadedConfig(), field);

    /// <summary>
    /// The ocean map value, out of 255, a zone has to reach before Rivers calls it sea.
    ///
    /// Rivers multiplies the map value by <c>(MapSizeY / 256) * 0.33333</c> before testing it
    /// against <c>oceanThreshold</c>, and this mod multiplies the threshold by the same steps, so
    /// the two cancel and what a zone needs is three times the value in the config whatever the
    /// world height.
    /// </summary>
    private static float OceanValueNeeded()
    {
        float scale = Math.Max(1, _oceanThresholdSteps) * 0.33333f;
        return Read<float>(LoadedConfig(), "oceanThreshold") / scale;
    }

    /// <summary>Whether the river network can be asked about ground no chunk has been made for.</summary>
    public static bool CanSampleNetwork =>
        _available && _instance != null && _getRiverRegion != null && _getSegments != null && _sampleRiver != null;

    /// <summary>
    /// The river network around one chunk: the region it belongs to and the segments near it.
    ///
    /// Resolving this is an R-tree search that allocates, and it can generate a whole river region
    /// the first time a plate is touched, so a walk over many sample points has to pay for each
    /// chunk once rather than once per point. Null when the network cannot say.
    /// </summary>
    public static object ChunkContext(int chunkX, int chunkZ)
    {
        if (!CanSampleNetwork) return null;

        try
        {
            object region = _getRiverRegion.Invoke(_instance, new object[] { chunkX, chunkZ });
            object segments = _getSegments.Invoke(_instance, new object[] { chunkX, chunkZ });
            return region == null || segments == null ? null : new[] { region, segments };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Blocks from a world position to the nearest river in <paramref name="context"/>, or -1 when
    /// it cannot say.
    ///
    /// This asks about ground that has not been generated, which Rivers is happy to answer because
    /// it routes from the ocean map rather than from terrain - there is no circularity in letting
    /// the rivers decide where the valleys go.
    /// </summary>
    public static double DistanceToRiver(object context, int worldX, int worldZ)
    {
        if (context is not object[] { Length: 2 } parts) return -1.0;

        try
        {
            object sample = _sampleRiver.Invoke(null, new[] { worldX, worldZ, parts[1], parts[0] });
            return sample == null ? -1.0 : _sampleDistance(sample);
        }
        catch (Exception)
        {
            return -1.0;
        }
    }

    private static double Map(double value, double fromMin, double fromMax, double toMin, double toMax)
        => toMin + (value - fromMin) / (fromMax - fromMin) * (toMax - toMin);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double InverseLerp(double value, double min, double max)
        => Math.Clamp((value - min) / (max - min), 0.0, 1.0);

    public static void Uninstall()
    {
        _resolved = false;
        _available = false;
        Installed = false;
        _samplesForChunk = null;
        _riverDistance = null;
        _bankFactor = null;
        _valleyNoise = null;
    }
}
