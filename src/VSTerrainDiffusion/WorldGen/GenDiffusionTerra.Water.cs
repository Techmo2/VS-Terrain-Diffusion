using System;
using Vintagestory.API.Common;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// River channels and wetland pools: where each cuts into a chunk's ground and holds water, and the
/// current saved for each river column.
/// </summary>
public sealed partial class GenDiffusionTerra
{
    /// <summary>Per column of a chunk, what the river channels covering it ask for.</summary>
    private sealed class Channels
    {
        /// <summary>
        /// The river's surface Y from the nearest centreline; <see cref="int.MaxValue"/> where no channel
        /// reaches.
        /// </summary>
        public readonly int[] Top = new int[ChunkSize * ChunkSize];
        /// <summary>
        /// Channel depth below <see cref="Top"/>, the deepest of the cross-sections covering the column.
        /// </summary>
        public readonly int[] Depth = new int[ChunkSize * ChunkSize];
        /// <summary>Whether any channel covering the column carries water.</summary>
        public readonly bool[] Wet = new bool[ChunkSize * ChunkSize];
        /// <summary>Surface current from the nearest centreline, m/s.</summary>
        public readonly float[] Speed = new float[ChunkSize * ChunkSize];
        /// <summary>Direction of that current, radians from east towards south.</summary>
        public readonly float[] Heading = new float[ChunkSize * ChunkSize];
        /// <summary>
        /// Distance to the nearest centreline, in blocks; <see cref="double.MaxValue"/> where none reaches.
        /// </summary>
        public readonly double[] Nearest = new double[ChunkSize * ChunkSize];
        /// <summary>Whether any channel reached the chunk.</summary>
        public bool Any;

        /// <summary>Empty: no column has a channel.</summary>
        public Channels()
        {
            Array.Fill(Top, int.MaxValue);
            Array.Fill(Nearest, double.MaxValue);
        }
    }

    /// <summary>
    /// Lays every river cell's channel, a capsule along its stretch of smoothed centreline, over the
    /// chunk's columns. A column takes the water surface and current of the nearest centreline (the
    /// current parabolic across the channel, a fifth of the mean at the bank), and the deepest of
    /// the rounded cross-sections covering it.
    /// </summary>
    private static Channels StampChannels(RiverCell[] rivers, int chunkX, int chunkZ)
    {
        var channels = new Channels();
        int x0 = chunkX * ChunkSize, z0 = chunkZ * ChunkSize;
        foreach (RiverCell cell in rivers)
        {
            double reach = cell.Reach;
            int minX = Math.Max(x0, (int)Math.Ceiling(Math.Min(cell.X, cell.NextX) - reach));
            int maxX = Math.Min(x0 + ChunkSize - 1, (int)Math.Floor(Math.Max(cell.X, cell.NextX) + reach));
            int minZ = Math.Max(z0, (int)Math.Ceiling(Math.Min(cell.Z, cell.NextZ) - reach));
            int maxZ = Math.Min(z0 + ChunkSize - 1, (int)Math.Floor(Math.Max(cell.Z, cell.NextZ) + reach));
            if (minX > maxX || minZ > maxZ) continue;

            double half = Math.Max(0.5, cell.WidthBlocks / 2.0);
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                double distance = cell.DistanceTo(x, z);
                if (distance > reach) continue;

                int i = (z - z0) * ChunkSize + x - x0;
                channels.Any = true;
                channels.Depth[i] = Math.Max(channels.Depth[i], cell.DepthAt(distance));
                channels.Wet[i] |= !cell.Dry;
                if (distance < channels.Nearest[i])
                {
                    channels.Nearest[i] = distance;
                    channels.Top[i] = cell.TopY;
                    double across = Math.Min(1.0, distance / half);
                    channels.Speed[i] = (float)Math.Max(0.2 * cell.Speed, 1.5 * cell.Speed * (1.0 - across * across));
                    channels.Heading[i] = cell.Heading;
                }
            }
        }
        return channels.Any ? channels : null;
    }

    /// <summary>Saves the current of the chunk's river water columns (see <see cref="RiverFlowKey"/>).</summary>
    private static void WriteRiverFlow(IMapChunk mapChunk, Channels channels, int[] groundY, int[] surfaceY, bool[] isOcean)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < channels.Top.Length; i++)
        {
            if (channels.Top[i] == int.MaxValue || !channels.Wet[i] || isOcean[i]) continue;
            if (Math.Min(channels.Top[i], surfaceY[i]) <= groundY[i]) continue;

            double turns = channels.Heading[i] / (2.0 * Math.PI);
            int heading = (int)Math.Round((turns - Math.Floor(turns)) * 256.0) & 0xFF;
            int speed = (int)Math.Clamp(Math.Round(channels.Speed[i] * 1000.0), 0, ushort.MaxValue);
            bytes.Add((byte)i);
            bytes.Add((byte)(i >> 8));
            bytes.Add((byte)heading);
            bytes.Add((byte)speed);
            bytes.Add((byte)(speed >> 8));
        }
        if (bytes.Count > 0) mapChunk.SetModdata(RiverFlowKey, bytes.ToArray());
    }

    /// <summary>
    /// Pools in a wetland: the top block of a wetland column becomes water, flush with the ground
    /// around it, in patches that are larger where more water stood before the lake dropped. Only
    /// where no neighbouring ground is lower, so a pool never spills, and away from river channels,
    /// whose water may stand lower. Vanilla's waterside plants then grow in and around them.
    /// </summary>
    private void AddWetlandPools(int chunkX, int chunkZ, byte[] wetland, RiverCell[] rivers, int[] surfaceY,
                                 int[] groundY, int[] waterTop, bool[] isLake, bool[] tapered, ref int minFloor)
    {
        int x0 = chunkX * ChunkSize, z0 = chunkZ * ChunkSize;
        TerrainTile tile = null;
        for (int lz = 0; lz < ChunkSize; lz++)
        for (int lx = 0; lx < ChunkSize; lx++)
        {
            int i = lz * ChunkSize + lx;
            if (wetland[i] == 0 || isLake[i] || tapered[i] || groundY[i] != surfaceY[i]) continue;

            int x = x0 + lx, z = z0 + lz;
            double noise = (_wetlandNoise.GetNoise(x, z) + 1.0) * 0.5;
            if (noise >= WetlandPoolsMin + WetlandPoolsWet * (wetland[i] / 255.0)) continue;

            int y = surfaceY[i];
            bool contained = true;
            for (int dz = -1; dz <= 1 && contained; dz++)
            for (int dx = -1; dx <= 1 && contained; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                int nx = lx + dx, nz = lz + dz;
                int ground;
                if ((uint)nx < ChunkSize && (uint)nz < ChunkSize)
                {
                    int n = nz * ChunkSize + nx;
                    ground = isLake[n] ? int.MinValue : surfaceY[n];
                }
                else
                {
                    _provider.GetTileAt(x + dx, z + dz, ref tile);
                    ground = tile.SurfaceY[tile.Index(x + dx - tile.BlockX, z + dz - tile.BlockZ)];
                }
                contained = ground >= y;
            }
            if (!contained) continue;

            foreach (RiverCell cell in rivers)
            {
                if (cell.DistanceTo(x, z) <= cell.Reach + 1.5)
                {
                    contained = false;
                    break;
                }
            }
            if (!contained) continue;

            groundY[i] = Math.Max(1, y - 1);
            waterTop[i] = Math.Max(waterTop[i], y);
            minFloor = Math.Min(minFloor, groundY[i]);
        }
    }

    /// <summary>Share of wetland columns that are pools at the driest.</summary>
    private const double WetlandPoolsMin = 0.2;

    /// <summary>
    /// Further share that are pools at the wettest, on top of <see cref="WetlandPoolsMin"/>.
    /// </summary>
    private const double WetlandPoolsWet = 0.4;
}
