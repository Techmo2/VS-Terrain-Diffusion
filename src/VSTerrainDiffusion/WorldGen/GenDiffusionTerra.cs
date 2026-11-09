using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods.NoObf;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Fills chunk columns from the diffusion heightmap. This takes the place of vanilla
/// <c>GenTerra</c> in the Terrain pass; every later pass (rock strata, caves, block layers,
/// vegetation, structures) runs unchanged on top of the heightmap this produces.
/// </summary>
public sealed class GenDiffusionTerra
{
    private const int ChunkSize = 32;

    private readonly ICoreServerAPI _api;
    private readonly TerrainDiffusionProvider _provider;
    private readonly DiffusionWorldSettings _settings;
    private readonly GlobalConfig _globalConfig;

    /// <summary>Lakes and rivers, or null when the world has them off.</summary>
    private readonly WorldWater _water;

    /// <summary>
    /// Map chunk mod data holding the current of every river water column, for a flowing-water block
    /// to read back later: per column a little-endian ushort index (lz * 32 + lx), a byte heading
    /// (256ths of a turn from east, towards south) and a ushort surface speed in mm/s.
    /// </summary>
    public const string RiverFlowKey = "vsterraindiffusion:riverflow";

    private readonly int _mapSizeY;
    private readonly int _seaLevel;

    /// <summary>Per-column blend targets used when a neighbouring chunk already fixed its heights.</summary>
    private struct WeightedTaper
    {
        public float TerrainYPos;
        public float Weight;
    }

    public GenDiffusionTerra(ICoreServerAPI api, TerrainDiffusionProvider provider, DiffusionWorldSettings settings,
                             WorldWater water)
    {
        _api = api;
        _provider = provider;
        _settings = settings;
        _water = water;
        _globalConfig = GlobalConfig.GetInstance(api);
        _mapSizeY = settings.MapSizeY;
        _seaLevel = settings.SeaLevel;

        // Patches about a dozen blocks across: pools cluster into open water and leave marsh between.
        _wetlandNoise = new Pipeline.FastNoiseLite(api.WorldManager.Seed ^ 0x3E71A4D);
        _wetlandNoise.SetNoiseType(Pipeline.FastNoiseLite.NoiseType.OpenSimplex2);
        _wetlandNoise.SetFrequency(1f / 12f);
        _wetlandNoise.SetFractalType(Pipeline.FastNoiseLite.FractalType.FBm);
        _wetlandNoise.SetFractalOctaves(2);
    }

    private readonly Pipeline.FastNoiseLite _wetlandNoise;

    public void OnChunkColumnGen(IChunkColumnGenerateRequest request)
    {
        IServerChunk[] chunks = request.Chunks;
        int chunkX = request.ChunkX;
        int chunkZ = request.ChunkZ;

        IMapChunk mapChunk = chunks[0].MapChunk;
        ushort[] rainHeightMap = mapChunk.RainHeightMap;
        ushort[] terrainHeightMap = mapChunk.WorldGenTerrainHeightMap;

        WeightedTaper[] taperMap = request.RequiresChunkBorderSmoothing && !IsSmoothingPrevented(chunkX, chunkZ)
            ? BuildTaperMap(request)
            : null;

        int defaultRockId = _globalConfig.defaultRockId;
        int freshWaterId = _globalConfig.waterBlockId;
        int saltWaterId = _globalConfig.saltWaterBlockId;
        int lakeIceId = _globalConfig.lakeIceBlockId;

        var surfaceY = new int[ChunkSize * ChunkSize];
        var isOcean = new bool[ChunkSize * ChunkSize];
        var freezes = new bool[ChunkSize * ChunkSize];

        // A peek's chunks are thrown away, so it takes the lakes as far as they are known rather
        // than waiting for them, and no rivers.
        SettledWater water = _water == null ? null
            : ChunkPeekScope.Active ? _water.Peek(chunkX, chunkZ)
            : _water.Settle(chunkX, chunkZ);
        short[] lakeTop = water?.LakeTop;
        byte[] wetland = water?.Wetland;

        // The top water block of every column: the sea's flush row, a lake's or a river's surface.
        var waterTop = new int[ChunkSize * ChunkSize];
        var isLake = new bool[ChunkSize * ChunkSize];
        var tapered = new bool[ChunkSize * ChunkSize];
        int maxWaterTop = _seaLevel - 1;

        int minSurface = int.MaxValue;
        int maxSurface = 0;

        // A chunk normally sits inside a single terrain tile; threading the tile through the loop
        // keeps that to one cache lookup instead of one per block column.
        TerrainTile tile = null;

        // Rivers, when it is installed and has been told to leave the terrain to us. This also
        // writes the flow vectors and river distances its boats and rendering read back, so it has
        // to happen for every chunk, not only the ones a river runs through.
        Array riverSamples = RiversCompat.SamplesForChunk(chunkX, chunkZ, chunks);
        RiversCompat.Sample[] riverColumn = riverSamples == null ? null : new RiversCompat.Sample[1024];

        // The lowest river bed in this chunk. The bulk fill below cannot look at individual
        // columns, so it has to stop underneath every channel that will be cut out of them.
        int minChannelFloor = int.MaxValue;

        for (int lz = 0; lz < ChunkSize; lz++)
        {
            int worldZ = chunkZ * ChunkSize + lz;
            for (int lx = 0; lx < ChunkSize; lx++)
            {
                int worldX = chunkX * ChunkSize + lx;
                int index2d = lz * ChunkSize + lx;

                _provider.GetTileAt(worldX, worldZ, ref tile);
                int tileIndex = tile.Index(worldX - tile.BlockX, worldZ - tile.BlockZ);

                int y = tile.SurfaceY[tileIndex];
                if (taperMap != null)
                {
                    WeightedTaper taper = taperMap[index2d];
                    if (taper.Weight > 0f)
                    {
                        int blended = (int)GameMath.Lerp(y, taper.TerrainYPos, taper.Weight);
                        tapered[index2d] = blended != y;
                        y = blended;
                    }
                }

                if (riverColumn != null)
                {
                    RiversCompat.Sample sample = RiversCompat.At(riverSamples, index2d);
                    riverColumn[index2d] = sample;
                    y = RiversCompat.ValleyHeight(y, sample, worldX, worldZ, _seaLevel);

                    if (sample.Distance <= 0.0)
                    {
                        minChannelFloor = Math.Min(minChannelFloor,
                            RiversCompat.ChannelFloorY(sample, _seaLevel, _mapSizeY));
                    }
                }

                y = GameMath.Clamp(y, 1, _mapSizeY - 2);
                surfaceY[index2d] = y;
                isOcean[index2d] = tile.ElevationMeters[tileIndex] < 0f;
                freezes[index2d] = tile.TemperatureC[tileIndex] < WaterFreezingTempOnGen;

                // A column the border taper moved no longer has the height the lake was worked out
                // for, so it holds no lake water rather than risk a spill.
                int lake = lakeTop?[index2d] ?? 0;
                if (tapered[index2d]) lake = Math.Min(lake, y);
                isLake[index2d] = lake >= _seaLevel && lake > y;
                waterTop[index2d] = isLake[index2d] ? lake : _seaLevel - 1;
                if (waterTop[index2d] > maxWaterTop) maxWaterTop = waterTop[index2d];

                if (y < minSurface) minSurface = y;
                if (y > maxSurface) maxSurface = y;
            }
        }

        int[] groundY = (int[])surfaceY.Clone();

        // The rivers' channels: the ground stops at the bed, and river water fills it up to the
        // surface of the river, flush with the banks.
        Channels channels = water?.Rivers is { Length: > 0 } rivers ? StampChannels(rivers, chunkX, chunkZ) : null;
        if (channels != null)
        {
            for (int i = 0; i < groundY.Length; i++)
            {
                if (channels.Top[i] == int.MaxValue || isOcean[i]) continue;
                int top = Math.Min(channels.Top[i], surfaceY[i]);
                int bed = Math.Max(1, top - channels.Depth[i]);
                groundY[i] = Math.Min(groundY[i], bed);
                minChannelFloor = Math.Min(minChannelFloor, bed);
                waterTop[i] = Math.Max(waterTop[i], channels.Wet[i] ? top : bed);
            }
            WriteRiverFlow(mapChunk, channels, groundY, surfaceY, isOcean);
        }

        if (wetland != null && riverColumn == null)
        {
            AddWetlandPools(chunkX, chunkZ, wetland, water.Rivers, surfaceY, groundY, waterTop, isLake, tapered,
                ref minChannelFloor);
        }

        // Layer 0 is always mantle, mirroring vanilla.
        chunks[0].Data.SetBlockBulk(0, ChunkSize, ChunkSize, _globalConfig.mantleBlockId);

        // Bulk-fill every layer that is solid across the whole chunk column.
        int solidTo = Math.Max(1, minSurface);

        // A channel cut below that level would be filled in again here and never reopened, since
        // the per-column pass below only starts above it.
        if (minChannelFloor != int.MaxValue) solidTo = Math.Max(1, Math.Min(solidTo, minChannelFloor));
        IChunkBlocks data = chunks[0].Data;
        for (int y = 1; y <= solidTo; y++)
        {
            if (y % ChunkSize == 0) data = chunks[y / ChunkSize].Data;
            data.SetBlockBulk(y % ChunkSize * ChunkSize * ChunkSize, ChunkSize, ChunkSize, defaultRockId);
        }

        int fillTop = Math.Max(maxSurface, maxWaterTop);

        for (int lz = 0; lz < ChunkSize; lz++)
        {
            for (int lx = 0; lx < ChunkSize; lx++)
            {
                int index2d = lz * ChunkSize + lx;
                int surface = groundY[index2d];
                int top = waterTop[index2d];
                int waterId = isOcean[index2d] && !isLake[index2d] ? saltWaterId : freshWaterId;

                for (int y = solidTo + 1; y <= fillTop; y++)
                {
                    IChunkBlocks blocks = chunks[y / ChunkSize].Data;
                    int index3d = ChunkIndex3d(lx, y % ChunkSize, lz);

                    if (y <= surface)
                    {
                        // Inside a channel the rock is left out, which is what makes the river a
                        // river rather than a damp line on a hillside.
                        if (riverColumn != null &&
                            RiversCompat.Carved(riverColumn[index2d], y, _seaLevel, _mapSizeY))
                        {
                            if (y <= top)
                            {
                                blocks.SetFluid(index3d, freshWaterId);
                            }
                            continue;
                        }

                        blocks[index3d] = defaultRockId;
                    }
                    else if (y <= top)
                    {
                        // Fresh water can freeze over; open ocean uses the salt water block.
                        int fluid = (y == top && freezes[index2d] && waterId != saltWaterId)
                            ? lakeIceId
                            : waterId;
                        blocks.SetFluid(index3d, fluid);
                    }
                }

                // In a channel the ground stops at the bed; laying soil and grass at the valley
                // floor would leave them hanging over the water.
                if (riverColumn != null && riverColumn[index2d].Distance <= 0.0)
                {
                    surface = Math.Clamp(
                        Math.Min(surface, RiversCompat.ChannelFloorY(riverColumn[index2d], _seaLevel, _mapSizeY)),
                        1, _mapSizeY - 2);
                }

                // Rain lands on the water surface where there is water, and on the ground otherwise.
                terrainHeightMap[index2d] = (ushort)surface;
                rainHeightMap[index2d] = (ushort)Math.Clamp(Math.Max(surface, top), 1, _mapSizeY - 2);
            }
        }

        ushort yMax = 0;
        for (int i = 0; i < rainHeightMap.Length; i++) yMax = Math.Max(yMax, rainHeightMap[i]);
        mapChunk.YMax = yMax;
    }

    /// <summary>Per column of a chunk, what the river channels covering it ask for.</summary>
    private sealed class Channels
    {
        public readonly int[] Top = new int[ChunkSize * ChunkSize];        // int.MaxValue: no channel
        public readonly int[] Depth = new int[ChunkSize * ChunkSize];
        public readonly bool[] Wet = new bool[ChunkSize * ChunkSize];
        public readonly float[] Speed = new float[ChunkSize * ChunkSize];  // at the surface, m/s
        public readonly float[] Heading = new float[ChunkSize * ChunkSize];
        public readonly double[] Nearest = new double[ChunkSize * ChunkSize];
        public bool Any;

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

    /// <summary>Share of wetland columns that are pools: this many at the driest, plus up to this many more at the wettest.</summary>
    private const double WetlandPoolsMin = 0.2, WetlandPoolsWet = 0.4;

    /// <summary>Vanilla's freezing threshold (degrees Celsius) for water placed during world generation.</summary>
    private const float WaterFreezingTempOnGen = -15f;

    private bool IsSmoothingPrevented(int chunkX, int chunkZ)
    {
        var flag = new BoolRef();
        _api.Event.IsTerrainHeightSmoothingPrevented(chunkX, chunkZ, flag);
        return flag.GetValue();
    }

    /// <summary>
    /// Reproduces vanilla's border taper: where a neighbouring chunk already committed to a
    /// terrain height (usually because a structure flattened it), blend towards it so the seam
    /// is not a cliff.
    /// </summary>
    private WeightedTaper[] BuildTaperMap(IChunkColumnGenerateRequest request)
    {
        ushort[][] neighbours = request.NeighbourTerrainHeight;

        // Vanilla drops the diagonals whenever the adjacent cardinal is present.
        if (neighbours[Cardinal.North.Index] != null)
        {
            neighbours[Cardinal.NorthEast.Index] = null;
            neighbours[Cardinal.NorthWest.Index] = null;
        }
        if (neighbours[Cardinal.East.Index] != null)
        {
            neighbours[Cardinal.NorthEast.Index] = null;
            neighbours[Cardinal.SouthEast.Index] = null;
        }
        if (neighbours[Cardinal.South.Index] != null)
        {
            neighbours[Cardinal.SouthWest.Index] = null;
            neighbours[Cardinal.SouthEast.Index] = null;
        }
        if (neighbours[Cardinal.West.Index] != null)
        {
            neighbours[Cardinal.SouthWest.Index] = null;
            neighbours[Cardinal.NorthWest.Index] = null;
        }

        var taperMap = new WeightedTaper[ChunkSize * ChunkSize];
        var borderIndices = new int[8];
        borderIndices[Cardinal.NorthEast.Index] = 992;
        borderIndices[Cardinal.SouthEast.Index] = 0;
        borderIndices[Cardinal.SouthWest.Index] = 31;
        borderIndices[Cardinal.NorthWest.Index] = 1023;

        for (int i = 0; i < ChunkSize; i++)
        {
            borderIndices[Cardinal.North.Index] = 992 + i;
            borderIndices[Cardinal.South.Index] = i;

            for (int j = 0; j < ChunkSize; j++)
            {
                double weightSum = 0;
                double heightSum = 0;
                float maxWeight = 0f;

                borderIndices[Cardinal.East.Index] = j * ChunkSize;
                borderIndices[Cardinal.West.Index] = j * ChunkSize + ChunkSize - 1;

                for (int k = 0; k < Cardinal.ALL.Length; k++)
                {
                    ushort[] heights = neighbours[k];
                    if (heights == null) continue;

                    float distance = k switch
                    {
                        0 => j / 32f,
                        1 => 1f - (i + 1f) / 32f + j / 32f,
                        2 => 1f - (i + 1f) / 32f,
                        3 => 1f - (i + 1f) / 32f + 1f - (j + 1f) / 32f,
                        4 => 1f - (j + 1f) / 32f,
                        5 => i / 32f + 1f - (j + 1f) / 32f,
                        6 => i / 32f,
                        _ => i / 32f + j / 32f
                    };

                    float linear = Math.Max(0f, 1f - distance);
                    float weight = linear * linear;
                    float height = heights[borderIndices[k]] + 0.5f;

                    heightSum += height * Math.Max(0.0001, weight);
                    weightSum += weight;
                    maxWeight = Math.Max(maxWeight, weight);
                }

                taperMap[j * ChunkSize + i] = new WeightedTaper
                {
                    TerrainYPos = (float)(heightSum / Math.Max(0.0001, weightSum)),
                    Weight = maxWeight
                };
            }
        }
        return taperMap;
    }

    private static int ChunkIndex3d(int x, int y, int z) => (y * ChunkSize + z) * ChunkSize + x;
}
