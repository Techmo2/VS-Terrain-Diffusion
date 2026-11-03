using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Stops vanilla's block layers filling dry-climate water in with land.
///
/// Before it lays soil, <c>GenBlockLayers.OnChunkColumnGeneration</c> takes every column whose
/// ground is below sea level and, where rain is under half, raises it by <c>(0.5 - rain) x 40</c>
/// blocks - up to 20 - in rock, replacing the water, with the heightmap moved up to match. Vanilla
/// uses it to keep shallow seas out of its deserts. On this mod's terrain it turned every shallow
/// sea along a dry coast, and every inland depression the model drew below sea level, into a flat
/// plain at the waterline: at 7.5 m per block, water up to 150 m deep. Found on a player's desert:
/// three blocks of salt water over rock after the terrain pass, gravel at the waterline after
/// block layers (execute order 0.4).
///
/// The 40 is replaced with <see cref="RaisePerRain"/>, which is 0 unless the world keeps vanilla's
/// fill (a world made before this, whose dry coasts already have it).
/// </summary>
public static class DryShallowsCompat
{
    private const string HarmonyId = "vsterraindiffusion.dryshallows";
    private const float VanillaRaisePerRain = 40f;

    private static Harmony _harmony;
    private static float _raisePerRain = VanillaRaisePerRain;

    /// <summary>Blocks of raise per unit of rain shortfall: vanilla's 40, or 0.</summary>
    public static float RaisePerRain() => _raisePerRain;

    public static void Install(bool keepVanillaFill, ILogger logger)
    {
        _raisePerRain = keepVanillaFill ? VanillaRaisePerRain : 0f;
        if (_harmony != null) return;
        MethodInfo target = AccessTools.Method(typeof(GenBlockLayers), "OnChunkColumnGeneration");
        if (target == null)
        {
            logger.Warning("[{0}] Could not find vanilla's block layers; dry-climate shallows will be filled in as vanilla does.",
                DiffusionPaths.ModId);
            return;
        }
        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, transpiler: new HarmonyMethod(typeof(DryShallowsCompat), nameof(Transpile)));
    }

    public static void Uninstall()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        _raisePerRain = VanillaRaisePerRain;
    }

    /// <summary>
    /// Replaces the 40 in <c>(0.5f - rainRel) * 40</c>: a <c>ldc.r4 40</c> directly followed by
    /// <c>mul</c>, with <c>ldc.r4 0.5</c> just before it. Anything else is left exactly as it was.
    /// </summary>
    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        MethodInfo getter = AccessTools.Method(typeof(DryShallowsCompat), nameof(RaisePerRain));
        for (int i = 3; i + 1 < code.Count; i++)
        {
            if (code[i].opcode != OpCodes.Ldc_R4 || code[i].operand is not float f || f != VanillaRaisePerRain) continue;
            if (code[i + 1].opcode != OpCodes.Mul) continue;
            if (code[i - 3].opcode != OpCodes.Ldc_R4 || code[i - 3].operand is not float half || half != 0.5f) continue;
            code[i] = new CodeInstruction(OpCodes.Call, getter).MoveLabelsFrom(code[i]);
            break;
        }
        return code;
    }
}
