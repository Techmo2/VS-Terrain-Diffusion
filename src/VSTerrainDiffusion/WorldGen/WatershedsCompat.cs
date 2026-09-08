using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods.NoObf;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Makes Algernon's Watersheds generate its rivers on the diffusion model's landscape instead of
/// its own.
///
/// Watersheds is a second terrain generator: it disables vanilla <c>GenTerra</c> outright and fills
/// every chunk column itself, so the two mods cannot simply be layered. Left alone they produce a
/// world that is the union of both terrains with only Watersheds' heightmaps recorded, which buries
/// the surface block layers under whatever diffusion rock stands above them.
///
/// Rather than evict it, this hands it our heights. The rule is that Watersheds must never learn
/// the height of its own terrain, because a single answer coming from the wrong landscape is enough
/// to put a stream's water somewhere its bed is not. It asks in five places, and all five are
/// answered from the model:
///
/// <list type="bullet">
/// <item><c>GenTerraSampler.SampleHeightWithContextCore</c> - the sole height source for the
/// watershed analysis: flow accumulation, drainage basins, stream routing and erosion. Answering it
/// from the model is what puts rivers in the valleys that are really there.</item>
/// <item><c>GetPreWatershedsBlockColumnHeight</c> and <c>GetPostErosionBlockColumnHeight</c> - the
/// ground height a stream's own longitudinal profile is laid out against, so its bed follows the
/// modelled valley floor downhill instead of some other terrain's.</item>
/// <item><c>CalculateColumnHeightWithStreamEffects</c> - the ground height *after* the stream has
/// cut into it, which is what decides where the water surface and the banks go. This one keeps the
/// carve depth Watersheds computed for the column.</item>
/// <item><c>GenerateWorldGenTerrainForColumn</c> - which blocks of one column are solid, i.e. the
/// terrain that actually gets built, with the same carve depth applied the same way.</item>
/// </list>
///
/// The last two use one expression between them, and that is the whole point: the bed the water sits
/// in and the bed the ground is built to are the same number. Getting that wrong is not subtle -
/// water strands itself in the air at a stream's head where the other terrain stood higher, and the
/// channel below runs dry with pools where the two happened to cross.
///
/// One thing is deliberately dropped: Watersheds' ridge and gully erosion filter. It exists to cut
/// valley detail into fractal noise, the model's landscape already has erosion in it, and it is
/// computed privately inside two of the five answers above - so keeping it would reintroduce
/// exactly the disagreement this is here to remove.
///
/// Everything else in Watersheds - stream water, banks, rapids, groundwater, its block layer pass -
/// then runs unchanged on terrain it believes it made, and this mod's own terrain handler stays out
/// of the world entirely.
///
/// This reaches into a closed mod's private methods, so it is written to fail loudly and safely: if
/// any of them cannot be found the caller is told, and the sane thing to do then is to leave the
/// world to Watersheds rather than generate a broken one.
/// </summary>
public static class WatershedsCompat
{
    public const string WatershedsModId = "watersheds";

    private const string GenTerraTypeName = "Watersheds.WorldGen.Terrain.WatershedsGenTerra";
    private const string SamplerTypeName = "Watersheds.WorldGen.Terrain.GenTerraSampler";

    private const string HarmonyId = DiffusionPaths.ModId + ".watersheds";

    private static Harmony _harmony;

    private static ICoreServerAPI _api;
    private static TerrainDiffusionProvider _provider;
    private static int _maxTerrainY;
    private static int _freshWaterId;
    private static int _saltWaterId;

    /// <summary>The <c>columnResults</c> scratch array and the two fields of its element struct.</summary>
    private static FieldInfo _columnResultsField;
    private static FieldInfo _soliditiesField;
    private static FieldInfo _waterBlockIdField;

    /// <summary>Reads X and Z off a boxed <c>WorldMapCoordinate</c>, whose type we cannot name.</summary>
    private static Func<object, int> _coordinateX;
    private static Func<object, int> _coordinateZ;

    /// <summary>
    /// Position of <c>heightDisplacementFromStream</c> in the argument list of
    /// <c>CalculateColumnHeightWithStreamEffects</c>, resolved by name rather than assumed.
    /// </summary>
    private static int _streamDisplacementArgIndex;

    /// <summary>
    /// Which chunk column Watersheds is generating. Its per-column work runs on
    /// <see cref="System.Threading.Tasks.Parallel"/> workers that know only their index within the
    /// chunk, so the coordinates have to come from the call that started them. An
    /// <see cref="AsyncLocal{T}"/> rather than a static because the execution context flows into
    /// those workers, which keeps this correct even if the game ever generates two columns at once.
    /// </summary>
    private static readonly AsyncLocal<ChunkRef> CurrentChunk = new();

    /// <summary>Reused per worker thread, so a chunk's 1024 columns share one tile lookup.</summary>
    [ThreadStatic]
    private static TerrainTile _tile;


    private sealed class ChunkRef
    {
        public int X;
        public int Z;
    }

    /// <summary>True when Watersheds is present and this world therefore needs the handover.</summary>
    public static bool IsPresent(ICoreServerAPI api) => api.ModLoader.IsModEnabled(WatershedsModId);

    /// <summary>Whether the patches are currently in place.</summary>
    public static bool Installed { get; private set; }

    /// <summary>
    /// Points Watersheds at the diffusion heightmap. Returns false with a reason when its terrain
    /// generator is not shaped the way this expects, which happens when it updates; nothing is
    /// patched in that case.
    /// </summary>
    public static bool TryInstall(ICoreServerAPI api, TerrainDiffusionProvider provider, out string failure)
    {
        Uninstall();

        _api = api;
        _provider = provider;
        _maxTerrainY = api.WorldManager.MapSizeY - 2;

        GlobalConfig globalConfig = GlobalConfig.GetInstance(api);
        _freshWaterId = globalConfig.waterBlockId;
        _saltWaterId = globalConfig.saltWaterBlockId;

        Type genTerra = FindType(GenTerraTypeName, "WatershedsGenTerra");
        Type sampler = FindType(SamplerTypeName, "GenTerraSampler");
        if (genTerra == null || sampler == null)
        {
            failure = "its terrain generator classes could not be found";
            return false;
        }

        MethodInfo chunk = AccessTools.Method(genTerra, "Generate");
        MethodInfo column = AccessTools.Method(genTerra, "GenerateWorldGenTerrainForColumn");
        MethodInfo sample = AccessTools.Method(sampler, "SampleHeightWithContextCore");
        MethodInfo preWatersheds = AccessTools.Method(genTerra, "GetPreWatershedsBlockColumnHeight");
        MethodInfo postErosion = AccessTools.Method(genTerra, "GetPostErosionBlockColumnHeight");
        MethodInfo postStream = AccessTools.Method(genTerra, "CalculateColumnHeightWithStreamEffects");

        if (chunk == null || column == null || sample == null
            || preWatersheds == null || postErosion == null || postStream == null)
        {
            failure = "its terrain generator no longer has the methods this mod answers heights through";
            return false;
        }

        if (!ResolveColumnBuffer(genTerra) || !ResolveCoordinateAccessors(postStream)
            || !ResolveStreamDisplacementArgument(postStream)
            || !TakesSameCoordinate(postStream, preWatersheds, postErosion))
        {
            failure = "its terrain generator does not carry heights and columns the way this mod writes them";
            return false;
        }

        try
        {
            _harmony = new Harmony(HarmonyId);
            Patch(chunk, nameof(BeforeChunk));
            Patch(column, nameof(BeforeColumn));
            Patch(sample, nameof(BeforeSample));
            Patch(preWatersheds, nameof(BeforeGroundHeight));
            Patch(postErosion, nameof(BeforeGroundHeight));
            Patch(postStream, nameof(BeforeCarvedGroundHeight));
        }
        catch (Exception e)
        {
            Uninstall();
            failure = "its terrain generator could not be patched: " + e.Message;
            return false;
        }

        Installed = true;
        failure = null;
        return true;
    }

    private static void Patch(MethodInfo target, string prefixName)
    {
        _harmony.Patch(target, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(WatershedsCompat), prefixName)));
    }

    /// <summary>Removes the patches. Safe to call when nothing is installed.</summary>
    public static void Uninstall()
    {
        try
        {
            _harmony?.UnpatchAll(HarmonyId);
        }
        catch (Exception e)
        {
            _api?.Logger.Warning("[{0}] Could not remove the Watersheds patches: {1}",
                DiffusionPaths.ModId, e.Message);
        }

        _harmony = null;
        Installed = false;
    }

    // ---------------------------------------------------------------- the answers

    /// <summary>The model's ground height at a column, before anything is cut into it.</summary>
    private static int SurfaceAt(int worldX, int worldZ)
    {
        TerrainTile tile = _provider.GetTileAt(worldX, worldZ, ref _tile);
        int index = tile.Index(worldX - tile.BlockX, worldZ - tile.BlockZ);
        return GameMath.Clamp(tile.SurfaceY[index], 1, _maxTerrainY);
    }

    /// <summary>
    /// The model's ground height with a stream cut into it.
    ///
    /// The displacement is in blocks and is subtracted: Watersheds adds it to the height distortion
    /// its landform threshold curve is sampled at, and that curve is sampled at
    /// <c>y + distortion</c>, so a positive distortion is ground pushed <em>down</em>. Vanilla uses
    /// the same convention - its geological upheaval term is never positive, because upheaval
    /// raises mountains.
    /// </summary>
    private static int CarvedSurfaceAt(int worldX, int worldZ, float streamDisplacement)
    {
        int carved = SurfaceAt(worldX, worldZ) - (int)MathF.Round(streamDisplacement);
        return GameMath.Clamp(carved, 1, _maxTerrainY);
    }

    /// <summary>Records which chunk column the per-column work below belongs to.</summary>
    private static void BeforeChunk(int chunkX, int chunkZ)
    {
        CurrentChunk.Value = new ChunkRef { X = chunkX, Z = chunkZ };
    }

    /// <summary>
    /// Fills one column's solidity from the model, keeping the stream carve Watersheds computed
    /// for it. This is the terrain that actually gets built, and it has to be the same expression
    /// <see cref="BeforeCarvedGroundHeight"/> answers with, or the water and its bed part company.
    /// </summary>
    private static bool BeforeColumn(
        object __instance,
        int chunkIndex2d,
        float heightDisplacementFromStream)
    {
        ChunkRef chunk = CurrentChunk.Value
            // Should not happen: Watersheds only reaches here from the call that sets it. Letting
            // its own terrain generate this column would put a column of its landscape in the
            // middle of ours, saved and indistinguishable from the rest.
            ?? throw DiffusionFailure.Fatal(
                "Algernon's Watersheds generated a terrain column with no chunk position, so the " +
                "model cannot say what belongs there. Update Watersheds, or this mod.");

        int worldX = chunk.X * 32 + chunkIndex2d % 32;
        int worldZ = chunk.Z * 32 + chunkIndex2d / 32;

        TerrainTile tile = _provider.GetTileAt(worldX, worldZ, ref _tile);
        int index = tile.Index(worldX - tile.BlockX, worldZ - tile.BlockZ);
        int surface = CarvedSurfaceAt(worldX, worldZ, heightDisplacementFromStream);

        var results = (Array)_columnResultsField.GetValue(__instance);
        object result = results.GetValue(chunkIndex2d);

        var solid = (BitArray)_soliditiesField.GetValue(result);
        solid.SetAll(false);
        for (int y = 1; y <= surface; y++) solid[y] = true;

        // Salt below the model's own sea, fresh everywhere else, which is the rule this mod's
        // terrain generator uses too. Watersheds turns the topmost block of a cold fresh column
        // into lake ice on its way in, so that still happens.
        _waterBlockIdField.SetValue(result, tile.ElevationMeters[index] < 0f ? _saltWaterId : _freshWaterId);
        results.SetValue(result, chunkIndex2d);

        return false;
    }

    /// <summary>
    /// Answers the height question the whole watershed analysis is built on. Every slope, flow
    /// direction, drainage basin and erosion estimate in Watersheds comes through here, so this is
    /// what puts its rivers in the model's valleys rather than in its own noise's.
    /// </summary>
    private static bool BeforeSample(int worldX, int worldZ, ref int __result)
    {
        __result = SurfaceAt(worldX, worldZ);
        return false;
    }

    /// <summary>
    /// Answers the uncut ground height at a column. Watersheds lays a stream's longitudinal profile
    /// out against this, so if it came from another landscape the bed would be cut to follow that
    /// one's gradient down a hillside shaped by ours.
    /// </summary>
    private static bool BeforeGroundHeight(object[] __args, ref int __result)
    {
        (int worldX, int worldZ) = Coordinate(__args);
        __result = SurfaceAt(worldX, worldZ);
        return false;
    }

    /// <summary>
    /// Answers the ground height once the stream has cut into it. This is where the water surface
    /// and the bank tops come from, and it deliberately returns exactly what
    /// <see cref="BeforeColumn"/> builds.
    /// </summary>
    private static bool BeforeCarvedGroundHeight(object[] __args, ref int __result)
    {
        (int worldX, int worldZ) = Coordinate(__args);
        var displacement = (float)__args[_streamDisplacementArgIndex];
        __result = CarvedSurfaceAt(worldX, worldZ, displacement);
        return false;
    }

    /// <summary>
    /// Reads the world coordinate out of a boxed height query argument. Letting the original method
    /// run instead would answer the query from Watersheds' own landscape, so there is no soft
    /// outcome here.
    /// </summary>
    private static (int X, int Z) Coordinate(object[] args)
    {
        object coordinate = args.Length > 0 ? args[0] : null;
        if (coordinate == null)
        {
            throw DiffusionFailure.Fatal(
                "Algernon's Watersheds asked for a ground height with no world coordinate. " +
                "Update Watersheds, or this mod.");
        }

        return (_coordinateX(coordinate), _coordinateZ(coordinate));
    }

    // ---------------------------------------------------------------- reflection

    /// <summary>Locates the per-column solidity buffer the terrain pass writes into.</summary>
    private static bool ResolveColumnBuffer(Type genTerra)
    {
        _columnResultsField = AccessTools.Field(genTerra, "columnResults");
        Type columnResult = _columnResultsField?.FieldType.GetElementType();
        _soliditiesField = columnResult == null ? null : AccessTools.Field(columnResult, "ColumnBlockSolidities");
        _waterBlockIdField = columnResult == null ? null : AccessTools.Field(columnResult, "WaterBlockID");

        return _soliditiesField?.FieldType == typeof(BitArray) && _waterBlockIdField?.FieldType == typeof(int);
    }

    /// <summary>
    /// Builds readers for the world coordinate struct Watersheds passes its height queries. Its
    /// type lives in that assembly, so a patch method cannot name it and the argument arrives
    /// boxed; X and Z come off it by reflection instead.
    /// </summary>
    private static bool ResolveCoordinateAccessors(MethodInfo heightQuery)
    {
        ParameterInfo[] parameters = heightQuery.GetParameters();
        if (parameters.Length == 0) return false;

        Type coordinate = parameters[0].ParameterType;
        _coordinateX = MemberReader(coordinate, "X");
        _coordinateZ = MemberReader(coordinate, "Z");
        return _coordinateX != null && _coordinateZ != null;
    }

    private static Func<object, int> MemberReader(Type type, string name)
    {
        PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property?.PropertyType == typeof(int) && property.CanRead)
        {
            return instance => (int)property.GetValue(instance);
        }

        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
        if (field?.FieldType == typeof(int))
        {
            return instance => (int)field.GetValue(instance);
        }

        return null;
    }

    /// <summary>
    /// Every height query has to take the same world coordinate type in the same position, because
    /// one pair of readers is used for all of them.
    /// </summary>
    private static bool TakesSameCoordinate(MethodInfo reference, params MethodInfo[] others)
    {
        Type coordinate = reference.GetParameters()[0].ParameterType;
        foreach (MethodInfo other in others)
        {
            ParameterInfo[] parameters = other.GetParameters();
            if (parameters.Length == 0 || parameters[0].ParameterType != coordinate) return false;
        }

        return true;
    }

    private static bool ResolveStreamDisplacementArgument(MethodInfo heightQuery)
    {
        ParameterInfo[] parameters = heightQuery.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].Name == "heightDisplacementFromStream" && parameters[i].ParameterType == typeof(float))
            {
                _streamDisplacementArgIndex = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a type by full name, falling back to a scan by simple name so that a namespace
    /// rename in Watersheds is survivable.
    /// </summary>
    private static Type FindType(string fullName, string simpleName)
    {
        Type type = AccessTools.TypeByName(fullName);
        if (type != null) return type;

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != "Watersheds") continue;

            foreach (Type candidate in assembly.GetTypes())
            {
                if (candidate.Name == simpleName) return candidate;
            }
        }

        return null;
    }
}
