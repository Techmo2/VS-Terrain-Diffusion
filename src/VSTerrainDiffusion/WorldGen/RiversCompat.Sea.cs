using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Where Rivers thinks the sea is: river mouths moved out to open water, and zones judged sea from
/// the model's own coast.
/// </summary>
public static partial class RiversCompat
{
    /// <summary>How many zones out to sea a river's mouth may be moved looking for open water.</summary>
    private const int MaxMouthShiftZones = 2;

    /// <summary>Reads a <c>RiverRegion</c>'s zones.</summary>
    private static AccessTools.FieldRef<object, Array> _regionZones;

    /// <summary>Reads a <c>RiverZone.oceanZone</c>.</summary>
    private static System.Func<object, bool> _zoneIsSea;

    /// <summary>Rivers' zone size and zones per region side.</summary>
    private static int _zoneSize, _zonesInRegion;

    /// <summary>The margin <c>GenerateRiver</c> keeps every node inside its region by.</summary>
    private static double _mouthPadding;

    /// <summary>Whether the model calls a block position sea; set by <see cref="UseModelSea"/>.</summary>
    private static System.Func<int, int, bool> _modelSea;

    /// <summary>The server API, for the one warning the sea patch may give.</summary>
    private static ICoreServerAPI _api;

    /// <summary>1 once the model's coast could not be read; the warning is given once.</summary>
    private static int _modelSeaFailed;

    /// <summary><c>RiverZone</c>'s sea flag, ocean distance and centre fields.</summary>
    private static FieldInfo _zoneSea, _zoneOceanDistance, _zoneCenter;

    /// <summary><c>RiverRegion.GlobalRegionStart</c>.</summary>
    private static PropertyInfo _regionStart;

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
}
