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

    private const string HarmonyId = "vsterraindiffusion.rivers";
    private static Harmony _harmony;

    /// <summary>How many zones out to sea a river's mouth may be moved looking for open water.</summary>
    private const int MaxMouthShiftZones = 2;


    private static AccessTools.FieldRef<object, Array> _regionZones;
    private static AccessTools.FieldRef<object, object> _regionConfig;
    private static System.Func<object, bool> _zoneIsSea;
    private static int _zoneSize, _zonesInRegion;
    private static double _mouthPadding;

    private static System.Func<int, int, bool> _modelSea;
    private static ICoreServerAPI _api;
    private static int _modelSeaFailed;
    private static FieldInfo _zoneSea, _zoneOceanDistance, _zoneCenter;
    private static PropertyInfo _regionStart;

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
    /// The river sample at one column, asked of the network without generating anything, or null
    /// when it cannot say. It is the same <c>SampleRiver</c> call <see cref="SamplesForChunk"/> makes
    /// for every column of a chunk, minus the flow data that call writes into the chunk.
    /// </summary>
    public static Sample? SampleAt(int worldX, int worldZ) => SampleAt(ChunkContext(worldX >> 5, worldZ >> 5), worldX, worldZ);

    /// <summary>
    /// <see cref="SampleAt(int, int)"/> with the column's chunk context already resolved, for a
    /// walk over many columns that can resolve each chunk once (<see cref="ChunkContext"/>).
    /// </summary>
    public static Sample? SampleAt(object context, int worldX, int worldZ)
    {
        if (context is not object[] { Length: 2 } parts) return null;

        try
        {
            object sample = _sampleRiver.Invoke(null, new[] { worldX, worldZ, parts[1], parts[0] });
            return sample == null ? null : new Sample(_riverDistance(sample), _bankFactor(sample));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The model's ground height at a column brought down into the river valley, if it is in one.
    ///
    /// A river sits just above sea level wherever it runs, so the ground has to come down to meet
    /// it. Outside the valley the weight is 1 and the model's own landscape is untouched.
    ///
    /// Only ever downwards. Ground already below the valley floor is sea bed, and pulling it
    /// *towards* the floor raises it: that walled every river mouth off from the ocean with a bar of
    /// sand at exactly sea level, a valley's width wide, and left the river ending in a lagoon.
    /// </summary>
    public static int ValleyHeight(int y, in Sample sample, int worldX, int worldZ, int seaLevel)
    {
        int valleyFloorY = ValleyFloorY(seaLevel);
        if (!sample.InValley(MaxValleyWidth) || y <= valleyFloorY) return y;

        float keep = ModelWeight(sample, worldX, worldZ);
        return (int)Math.Round(valleyFloorY + (y - valleyFloorY) * keep);
    }

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

    /// <summary>
    /// Makes Rivers start each river from open sea rather than from the first zone it calls sea.
    ///
    /// Rivers seeds a river at a coastal zone - a sea zone with land beside it - and grows it inland.
    /// Both come from the ocean map, 256 blocks at a time, but the model only follows that map to
    /// within a coarse cell, and its own shore can lie hundreds of blocks further out. A river whose
    /// mouth is the map's coast then ends on dry land short of the model's. Starting it from the
    /// nearest zone that is sea on every side, along its own heading, lets its first nodes run out
    /// across that gap; Rivers allows a river's first three nodes to lie in the sea for this.
    /// Failure leaves Rivers' own mouths in place.
    /// </summary>
    private static void PatchRiverMouths(ICoreServerAPI api, object config)
    {
        try
        {
            Type regionType = AccessTools.TypeByName("Rivers.RiverRegion");
            Type zoneType = AccessTools.TypeByName("Rivers.RiverZone");
            MethodInfo generate = regionType == null ? null : AccessTools.Method(regionType, "GenerateRiver");
            if (generate == null || zoneType == null) throw new MissingMethodException("Rivers.RiverRegion.GenerateRiver");

            _regionZones = AccessTools.FieldRefAccess<object, Array>(AccessTools.Field(regionType, "zones"));
            _regionConfig = AccessTools.FieldRefAccess<object, object>(AccessTools.Field(regionType, "config"));
            FieldInfo sea = AccessTools.Field(zoneType, "oceanZone");
            _zoneIsSea = zone => (bool)sea.GetValue(zone);
            _zoneSize = Read<int>(config, "zoneSize");
            _zonesInRegion = Read<int>(config, "zonesInRegion");
            // The margin GenerateRiver keeps every node inside its region by.
            _mouthPadding = Read<double>(config, "segmentOffset") + Read<double>(config, "maxValleyWidth") +
                            Read<float>(config, "maxSize");

            MethodInfo oceanicity = AccessTools.Method(regionType, "SetZoneOceanicity");
            _zoneSea = sea;
            _zoneOceanDistance = AccessTools.Field(zoneType, "oceanDistance");
            _zoneCenter = AccessTools.Field(zoneType, "localZoneCenterPosition");
            _regionStart = AccessTools.Property(regionType, "GlobalRegionStart");
            _api = api;

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(generate, prefix: new HarmonyMethod(typeof(RiversCompat), nameof(BeforeGenerateRiver)));
            if (oceanicity != null && _zoneOceanDistance != null && _zoneCenter != null && _regionStart != null)
                _harmony.Patch(oceanicity, prefix: new HarmonyMethod(typeof(RiversCompat), nameof(BeforeSetZoneOceanicity)));
            else
                api.Logger.Warning("[{0}] Rivers' sea test could not be found, so rivers will follow the ocean map " +
                                   "rather than the model's own coast.", DiffusionPaths.ModId);
        }
        catch (Exception e)
        {
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;
            api.Logger.Warning(
                "[{0}] Rivers' river mouths could not be moved out to open sea, so some rivers may end short " +
                "of the coast: {1}", DiffusionPaths.ModId, e.Message);
        }
    }

    /// <summary>
    /// Harmony prefix on <c>RiverRegion.GenerateRiver</c>. Only the call that seeds a river - stage 0,
    /// no parent - is touched: its start is stepped back against the river's heading, a zone at a
    /// time, to the first zone that is sea on all eight sides, and the river's recorded start with
    /// it, which Rivers sizes the river's sampling radius from.
    /// </summary>
    private static void BeforeGenerateRiver(object __instance, double angle, ref OpenTK.Mathematics.Vector2d startPos,
                                            int stage, object parentNode, object river)
    {
        if (stage != 0 || parentNode != null) return;

        Array zones = _regionZones(__instance);
        double radians = angle * (Math.PI / 180.0);
        var heading = new OpenTK.Mathematics.Vector2d(Math.Cos(radians), Math.Sin(radians));
        double regionSize = (double)_zoneSize * _zonesInRegion;

        for (int step = 1; step <= MaxMouthShiftZones; step++)
        {
            OpenTK.Mathematics.Vector2d candidate = startPos - heading * (_zoneSize * step);
            if (candidate.X < _mouthPadding || candidate.Y < _mouthPadding ||
                candidate.X > regionSize - _mouthPadding || candidate.Y > regionSize - _mouthPadding)
                return;

            if (!OpenSea(zones, (int)(candidate.X / _zoneSize), (int)(candidate.Y / _zoneSize))) continue;

            startPos = candidate;
            AccessTools.Property(river.GetType(), "StartPos").SetValue(river, candidate);
            return;
        }
    }

    /// <summary>
    /// Has Rivers decide sea from the model's own coast (<see cref="TerrainDiffusionProvider.IsCoarseSea"/>)
    /// rather than the world's ocean map. Must be called before any Rivers region is built, which is
    /// before the spawn search: a region built from the map is cached for the session and would not
    /// match its neighbours.
    /// </summary>
    public static void UseModelSea(System.Func<int, int, bool> isSea)
    {
        _modelSea = isSea;
        _modelSeaFailed = 0;
    }

    /// <summary>
    /// Harmony prefix on <c>RiverRegion.SetZoneOceanicity</c>: marks the zone sea exactly as Rivers
    /// would - <c>oceanZone</c> set, <c>oceanDistance</c> -1 - but from the model. Rivers' own test
    /// still runs if the model cannot answer, and that is said once.
    /// </summary>
    private static bool BeforeSetZoneOceanicity(object __instance, object zone)
    {
        System.Func<int, int, bool> isSea = _modelSea;
        if (isSea == null) return true;

        try
        {
            var start = (OpenTK.Mathematics.Vector2d)_regionStart.GetValue(__instance);
            var center = (OpenTK.Mathematics.Vector2d)_zoneCenter.GetValue(zone);
            if (isSea((int)(start.X + center.X), (int)(start.Y + center.Y)))
            {
                _zoneSea.SetValue(zone, true);
                _zoneOceanDistance.SetValue(zone, -1.0);
            }
            return false;
        }
        catch (Exception e)
        {
            if (System.Threading.Interlocked.Exchange(ref _modelSeaFailed, 1) == 0)
            {
                _api?.Logger.Warning("[{0}] The model's coast could not be read for Rivers, so rivers here follow " +
                                     "the ocean map instead: {1}", DiffusionPaths.ModId, e.Message);
            }
            return true;
        }
    }

    /// <summary>Whether the zone at (x, z) and all eight around it are sea.</summary>
    private static bool OpenSea(Array zones, int x, int z)
    {
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int zx = x + dx, zz = z + dz;
                if (zx < 0 || zz < 0 || zx >= _zonesInRegion || zz >= _zonesInRegion) return false;
                if (!_zoneIsSea(zones.GetValue(zz * _zonesInRegion + zx))) return false;
            }
        }
        return true;
    }

    public static void Uninstall()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        _modelSea = null;
        _api = null;
        _resolved = false;
        _available = false;
        Installed = false;
        _samplesForChunk = null;
        _riverDistance = null;
        _bankFactor = null;
        _valleyNoise = null;
    }
}
