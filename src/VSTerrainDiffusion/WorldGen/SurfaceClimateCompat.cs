using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.Server;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Makes the parts of Vintage Story that read a column's climate at sea level read it at the
/// surface instead.
///
/// Vintage Story stores one climate byte per column and takes it to mean the temperature at sea
/// level, subtracting <c>distToSealevel / 1.5</c> on read to get the temperature where you actually
/// are. That fixed rate works out to 0.157 C per block, which at the shipped 15 m per block is
/// 10.5 C per km - well over the real atmospheric lapse rate of about 6.5. So the model's surface
/// temperature and a sane sea-level temperature cannot both fit in the one byte, and this mod
/// stores whatever makes the surface come out right: <c>surfaceTemperature + surfaceDistance/1.5</c>.
///
/// Everything that applies the game's own correction at the real surface then reads the temperature
/// the model predicted - tree and shrub species, ground plant patches, block layers, tall grass. But
/// the stored byte on its own no longer means anything: on a 5 C peak 169 blocks above the sea it
/// holds 219, which reads back as 27 C at sea level, and above about 250 blocks it saturates at 255
/// and reads as a flat 40 C no matter how cold the summit really is.
///
/// Five places read it that way, and each of them is patched here to use the column's surface
/// instead. Without this, high ground grows tropical.
///
/// <list type="bullet">
/// <item><c>ServerSystemEntitySpawner.GetSuitableClimateTemperatureRainfall</c> deliberately
/// discards the spawn altitude and samples at <c>seaLevel * 1.09</c>, which is why tropical animals
/// turn up on temperate mountains. Vetoing them afterwards through <c>OnTrySpawnEntity</c> would not
/// help: the spawner climate-filters every candidate species against that one reading, so the
/// cold-climate animals that belong there have already been rejected before any veto runs, and the
/// mountain ends up empty rather than right.</item>
/// <item><c>Climate.GetFertilityFromUnscaledTemp</c> takes the raw byte. Three callers rely on it -
/// the server's own <c>getWorldGenClimateAt</c>, <c>GenBlockLayers</c> and
/// <c>BlockSchematicStructure</c> - and all three hand it a <c>posYRel</c> computed the same way,
/// which is enough to recover the altitude and undo the inflation.</item>
/// <item><c>GenPonds</c> and <c>GenRivulets</c> ask for the temperature at distance zero, i.e. at
/// sea level, to decide how much water a chunk gets before it freezes.</item>
/// <item><c>GenVegetationAndPatches.genTrees</c> divides the raw byte by 255 and treats the result
/// as "how hot is this chunk", thinning the forest where it is hot and dry. On inflated ground that
/// term saturates and removes four trees in five.</item>
/// </list>
///
/// One reader cannot be fixed from a mod: the client tints blocks through <c>colormap.vsh</c>, which
/// hardcodes both the 1.5 and the 4.25, so foliage colour on high ground stays keyed to the stored
/// byte.
///
/// Only installed when the mod is actually writing the climate map. With model climate switched off
/// the map holds vanilla's own sea-level temperatures, which these readers are already right about.
/// </summary>
public static class SurfaceClimateCompat
{
    private const string HarmonyId = "vsterraindiffusion.surfaceclimate";

    private static Harmony _harmony;
    private static ICoreServerAPI _api;
    private static int _seaLevel;
    private static int _mapSizeY;

    /// <summary>True while the patches are in place.</summary>
    public static bool Installed { get; private set; }

    /// <summary>
    /// Surface height a column's climate was stored against. Over water that is the sea surface
    /// rather than the sea bed, matching <c>DiffusionClimateMapLayer.ValueAt</c> - compensating
    /// against a bed hundreds of blocks down would read tens of degrees too cold.
    /// </summary>
    private static int ClimateSurfaceY(int terrainY) => Math.Max(terrainY, _seaLevel - 1);

    /// <summary>
    /// Armed by the per-chunk generators below, consumed by the first sea-level temperature read
    /// that follows. A one-shot token rather than a window, so nothing else that happens to ask for
    /// the temperature at sea level while a chunk is generating picks it up by accident.
    /// </summary>
    private static readonly ThreadLocal<int> PendingChunkDistance = new(() => int.MinValue);

    public static void Install(ICoreServerAPI api)
    {
        Uninstall();

        _api = api;
        _seaLevel = api.World.SeaLevel;
        _mapSizeY = api.WorldManager.MapSizeY;

        MethodInfo spawnerClimate = AccessTools.Method(
            typeof(ServerSystemEntitySpawner), "GetSuitableClimateTemperatureRainfall");
        MethodInfo fertility = AccessTools.Method(
            typeof(Climate), nameof(Climate.GetFertilityFromUnscaledTemp));
        MethodInfo scaledTemperature = AccessTools.Method(
            typeof(Climate), nameof(Climate.GetScaledAdjustedTemperatureFloat));

        Type ponds = AccessTools.TypeByName("Vintagestory.ServerMods.GenPonds");
        Type rivulets = AccessTools.TypeByName("Vintagestory.ServerMods.GenRivulets");
        Type vegetation = AccessTools.TypeByName("Vintagestory.ServerMods.GenVegetationAndPatches");

        MethodInfo pondColumn = ponds == null ? null : AccessTools.Method(ponds, "OnChunkColumnGen");
        MethodInfo rivuletColumn = rivulets == null ? null : AccessTools.Method(rivulets, "OnChunkColumnGen");
        MethodInfo trees = vegetation == null ? null : AccessTools.Method(vegetation, "genTrees");

        if (spawnerClimate == null || fertility == null || scaledTemperature == null
            || pondColumn == null || rivuletColumn == null || trees == null)
        {
            throw DiffusionFailure.Fatal(api.Logger,
                "Vintage Story's climate readers are not where this mod corrects them, so high ground " +
                "would be generated and populated as though it were at sea level.");
        }

        _treeHeightMap = AccessTools.Field(vegetation, "heightmap");
        _spawnerTmpPos = AccessTools.Field(typeof(ServerSystemEntitySpawner), "tmpPos");
        if (_treeHeightMap == null || _spawnerTmpPos == null)
        {
            throw DiffusionFailure.Fatal(api.Logger,
                "Vintage Story's world generator no longer carries the fields this mod reads surface " +
                "heights from, so high ground would be populated as though it were at sea level.");
        }

        try
        {
            _harmony = new Harmony(HarmonyId);

            _harmony.Patch(spawnerClimate,
                prefix: Method(nameof(BeforeSpawnerClimate)));
            _harmony.Patch(fertility,
                prefix: Method(nameof(BeforeFertility)));
            _harmony.Patch(scaledTemperature,
                prefix: Method(nameof(BeforeScaledTemperature)));

            // Arm the token around the two generators that ask at sea level, and clear it again
            // however they return so a chunk that took an early exit cannot leave it lying about.
            foreach (MethodInfo column in new[] { pondColumn, rivuletColumn })
            {
                _harmony.Patch(column,
                    prefix: Method(nameof(BeforeChunkColumn)),
                    finalizer: Method(nameof(AfterChunkColumn)));
            }

            _harmony.Patch(trees,
                prefix: Method(nameof(BeforeTrees)),
                finalizer: Method(nameof(AfterChunkColumn)),
                transpiler: Method(nameof(TranspileTrees)));
        }
        catch (Exception e)
        {
            Uninstall();
            throw DiffusionFailure.Fatal(api.Logger,
                "Vintage Story's climate readers could not be patched, so high ground would be " +
                "generated and populated as though it were at sea level.", e);
        }

        // Harmony reports success even when a patch ends up attached to nothing, and a correction
        // that quietly did not happen is exactly the silent wrong world this mod refuses to make.
        foreach (MethodInfo target in new[]
                 { spawnerClimate, fertility, scaledTemperature, pondColumn, rivuletColumn, trees })
        {
            Patches info = Harmony.GetPatchInfo(target);
            if (info != null && info.Owners.Contains(HarmonyId)) continue;

            Uninstall();
            throw DiffusionFailure.Fatal(api.Logger,
                $"The surface climate correction for {target.DeclaringType?.Name}.{target.Name} did not " +
                "attach, so high ground would be populated as though it were at sea level.");
        }

        Installed = true;
        api.Logger.Notification("[{0}] Climate reads at sea level corrected to the column surface " +
                                "(spawning, fertility, ponds, rivulets, tree density).", DiffusionPaths.ModId);
    }

    private static HarmonyMethod Method(string name) =>
        new(AccessTools.Method(typeof(SurfaceClimateCompat), name));

    /// <summary>Removes the patches. Safe to call when nothing is installed.</summary>
    public static void Uninstall()
    {
        try
        {
            _harmony?.UnpatchAll(HarmonyId);
        }
        catch (Exception e)
        {
            _api?.Logger.Warning("[{0}] Could not remove the surface climate patches: {1}",
                DiffusionPaths.ModId, e.Message);
        }

        _harmony = null;
        _api = null;
        Installed = false;
        PendingChunkDistance.Value = int.MinValue;
    }

    private static FieldInfo _treeHeightMap;
    private static FieldInfo _spawnerTmpPos;

    // ------------------------------------------------------------------ spawning

    /// <summary>
    /// Replaces the spawner's climate lookup so a creature is matched against the climate of the
    /// ground it is standing on rather than the climate a column of air at sea level would have.
    ///
    /// This decides the fauna of caves too. Vanilla read those at sea level as well, and the
    /// column's own surface is the closest thing to that which still means something here: reading
    /// at the cave floor would make deep ground under a mountain hotter the further down it went,
    /// because the stored byte carries that mountain's whole altitude correction.
    /// </summary>
    private static bool BeforeSpawnerClimate(ServerSystemEntitySpawner __instance, ServerWorldMap worldMap,
                                             RuntimeSpawnConditions sc, ref ClimateCondition __result)
    {
        var pos = (Vintagestory.API.MathTools.BlockPos)_spawnerTmpPos.GetValue(__instance);
        int restoreY = pos.Y;

        try
        {
            // Right-shift rather than divide: chunk coordinates have to floor on negatives.
            ushort[] heights = worldMap.GetMapChunk(pos.X >> 5, pos.Z >> 5)?.WorldGenTerrainHeightMap;
            if (heights == null)
            {
                // The column is not loaded. Vanilla answers null here too, and nothing spawns.
                __result = null;
                return false;
            }

            pos.Y = ClimateSurfaceY(heights[(pos.Z & 31) * 32 + (pos.X & 31)]);

            ClimateCondition climate = worldMap.getWorldGenClimateAt(pos, temperatureRainfallOnly: true);
            if (climate == null)
            {
                __result = null;
                return false;
            }

            if (sc.ClimateValueMode != EnumGetClimateMode.WorldGenValues)
            {
                worldMap.GetClimateAt(pos, climate, sc.ClimateValueMode, _api.World.Calendar.TotalDays);
            }

            __result = sc.MatchesClimate(climate) ? climate : null;
            return false;
        }
        finally
        {
            pos.Y = restoreY;
        }
    }

    // ------------------------------------------------------------------ fertility

    /// <summary>
    /// Undoes the altitude inflation before the raw byte is used as a temperature.
    ///
    /// <paramref name="posYRel"/> is the caller's height as a fraction of the world above sea level,
    /// and every caller computes it as <c>(y - seaLevel) / (mapSizeY - seaLevel)</c>, so multiplying
    /// it back out gives exactly the distance the game would have subtracted had it been asked for a
    /// temperature rather than a fertility.
    /// </summary>
    private static void BeforeFertility(int rain, ref int unscaledTemp, float posYRel)
    {
        unscaledTemp = Climate.GetAdjustedTemperature(
            unscaledTemp, (int)(posYRel * (_mapSizeY - _seaLevel)));
    }

    // ------------------------------------------------- per-chunk sea level reads

    /// <summary>
    /// Substitutes the chunk's own surface for a read at sea level, once, for the generator that
    /// just armed it. Every other caller passes a real distance and is left alone.
    /// </summary>
    private static void BeforeScaledTemperature(int unscaledTemp, ref int distToSealevel)
    {
        if (distToSealevel != 0) return;

        int pending = PendingChunkDistance.Value;
        if (pending == int.MinValue) return;

        PendingChunkDistance.Value = int.MinValue;
        distToSealevel = pending;
    }

    private static void BeforeChunkColumn(IChunkColumnGenerateRequest request)
    {
        ushort[] heights = request?.Chunks?[0]?.MapChunk?.WorldGenTerrainHeightMap;
        if (heights == null) return;

        // The chunk centre, which is the column these generators sample their climate at.
        PendingChunkDistance.Value = ClimateSurfaceY(heights[16 * 32 + 16]) - _seaLevel;
    }

    private static void BeforeTrees(object __instance)
    {
        var heights = (ushort[])_treeHeightMap.GetValue(__instance);
        if (heights == null) return;

        PendingChunkDistance.Value = ClimateSurfaceY(heights[16 * 32 + 16]) - _seaLevel;
    }

    /// <summary>Clears the token whichever way the generator returned, exception included.</summary>
    private static void AfterChunkColumn() => PendingChunkDistance.Value = int.MinValue;

    /// <summary>
    /// Corrects the one place tree density reads the temperature byte by hand.
    ///
    /// <c>genTrees</c> computes <c>(climate &gt;&gt; 16) &amp; 0xFF</c> and divides by 255 with no
    /// altitude correction anywhere, so there is no call to intercept - the deflation has to be
    /// spliced in after the byte is unpacked. It is the only such expression in the method, and the
    /// patch refuses to install if that stops being true.
    /// </summary>
    private static IEnumerable<CodeInstruction> TranspileTrees(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        MethodInfo deflate = AccessTools.Method(typeof(SurfaceClimateCompat), nameof(DeflateChunkTemperature));

        int found = 0;
        for (int i = codes.Count - 3; i >= 1; i--)
        {
            if (codes[i].opcode != OpCodes.Shr) continue;
            if (!IsLoadConstant(codes[i - 1], 16)) continue;
            if (!IsLoadConstant(codes[i + 1], 255)) continue;
            if (codes[i + 2].opcode != OpCodes.And) continue;

            codes.Insert(i + 3, new CodeInstruction(OpCodes.Call, deflate));
            found++;
        }

        if (found != 1)
        {
            throw DiffusionFailure.Fatal(
                $"Vintage Story's tree generator unpacks the climate temperature in {found} places " +
                "rather than one, so this mod cannot tell which to correct for altitude.");
        }

        return codes;
    }

    private static bool IsLoadConstant(CodeInstruction code, int value)
    {
        if (code.opcode == OpCodes.Ldc_I4) return code.operand is int i && i == value;
        if (code.opcode == OpCodes.Ldc_I4_S) return Convert.ToInt32(code.operand) == value;
        return false;
    }

    /// <summary>Spliced into <c>genTrees</c> by <see cref="TranspileTrees"/>.</summary>
    public static int DeflateChunkTemperature(int unscaledTemp)
    {
        int pending = PendingChunkDistance.Value;
        if (pending == int.MinValue) return unscaledTemp;

        PendingChunkDistance.Value = int.MinValue;
        return Climate.GetAdjustedTemperature(unscaledTemp, pending);
    }
}
