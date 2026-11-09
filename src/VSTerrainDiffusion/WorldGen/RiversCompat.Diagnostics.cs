using System;
using HarmonyLib;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>A readable account of Rivers' network, for /tdiff rivers.</summary>
public static partial class RiversCompat
{
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

    /// <summary>The distance between two boxed OpenTK <c>Vector2d</c>s.</summary>
    /// <param name="a">One point.</param>
    /// <param name="b">The other.</param>
    /// <returns>The distance.</returns>
    private static double Distance(object a, object b)
    {
        Type v = a.GetType();
        double ax = (double)AccessTools.Field(v, "X").GetValue(a), az = (double)AccessTools.Field(v, "Y").GetValue(a);
        double bx = (double)AccessTools.Field(v, "X").GetValue(b), bz = (double)AccessTools.Field(v, "Y").GetValue(b);
        return Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
    }
}
