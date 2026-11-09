using System;
using Vintagestory.API.Common;
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
public sealed partial class GenDiffusionTerra
{
    /// <summary>Blocks per chunk edge.</summary>
    private const int ChunkSize = 32;

    /// <summary>The server.</summary>
    private readonly ICoreServerAPI _api;
    /// <summary>The world's terrain tiles.</summary>
    private readonly TerrainDiffusionProvider _provider;
    /// <summary>The game's block IDs for rock, water, ice and mantle.</summary>
    private readonly GlobalConfig _globalConfig;

    /// <summary>Lakes and rivers, or null when the world has them off.</summary>
    private readonly WorldWater _water;

    /// <summary>
    /// Map chunk mod data holding the current of every river water column, for a flowing-water block
    /// to read back later: per column a little-endian ushort index (lz * 32 + lx), a byte heading
    /// (256ths of a turn from east, towards south) and a ushort surface speed in mm/s.
    /// </summary>
    public const string RiverFlowKey = "vsterraindiffusion:riverflow";

    /// <summary>The world's height.</summary>
    private readonly int _mapSizeY;
    /// <summary>The world's sea level.</summary>
    private readonly int _seaLevel;

    /// <summary>Decides which wetland columns hold a pool: patches about a dozen blocks across.</summary>
    private readonly Pipeline.FastNoiseLite _wetlandNoise;

    /// <summary>A generator for one world.</summary>
    /// <param name="api">The server.</param>
    /// <param name="provider">The world's terrain tiles.</param>
    /// <param name="settings">The world's settings, for its height and sea level.</param>
    /// <param name="water">Lakes and rivers, or null when the world has them off.</param>
    public GenDiffusionTerra(ICoreServerAPI api, TerrainDiffusionProvider provider, DiffusionWorldSettings settings,
                             WorldWater water)
    {
        _api = api;
        _provider = provider;
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

    /// <summary>
    /// Fills one chunk column: works out every column's ground and water, cuts the river channels
    /// and wetland pools into that plan, then places the blocks and writes the height maps.
    /// Registered for the Terrain pass in vanilla <c>GenTerra</c>'s place.
    /// </summary>
    /// <param name="request">The chunk column, its neighbours' committed heights and its position.</param>
    public void OnChunkColumnGen(IChunkColumnGenerateRequest request)
    {
        IServerChunk[] chunks = request.Chunks;
        int chunkX = request.ChunkX;
        int chunkZ = request.ChunkZ;
        IMapChunk mapChunk = chunks[0].MapChunk;

        WeightedTaper[] taperMap = request.RequiresChunkBorderSmoothing && !IsSmoothingPrevented(chunkX, chunkZ)
            ? BuildTaperMap(request)
            : null;

        // A peek's chunks are thrown away, so it takes the lakes as far as they are known rather
        // than waiting for them, and no rivers.
        SettledWater water = _water == null ? null
            : ChunkPeekScope.Active ? _water.Peek(chunkX, chunkZ)
            : _water.Settle(chunkX, chunkZ);

        // Rivers, when it is installed and has been told to leave the terrain to us. This also
        // writes the flow vectors and river distances its boats and rendering read back, so it has
        // to happen for every chunk, not only the ones a river runs through.
        Array riverSamples = RiversCompat.SamplesForChunk(chunkX, chunkZ, chunks);

        ColumnPlan plan = PlanColumns(chunkX, chunkZ, taperMap, water?.LakeTop, riverSamples);
        CutWater(plan, water, mapChunk, chunkX, chunkZ);
        PlaceBlocks(chunks, plan);

        ushort[] rainHeightMap = mapChunk.RainHeightMap;
        ushort yMax = 0;
        for (int i = 0; i < rainHeightMap.Length; i++) yMax = Math.Max(yMax, rainHeightMap[i]);
        mapChunk.YMax = yMax;
    }

    /// <summary>One chunk column's ground and water, per column, before any block is placed.</summary>
    private sealed class ColumnPlan
    {
        /// <summary>The ground the model asks for, after the border taper and Rivers' valleys.</summary>
        public readonly int[] SurfaceY = new int[ChunkSize * ChunkSize];

        /// <summary>The ground as built: <see cref="SurfaceY"/> less any channel or pool cut into it.</summary>
        public int[] GroundY;

        /// <summary>Whether the model put the column below sea level.</summary>
        public readonly bool[] IsOcean = new bool[ChunkSize * ChunkSize];

        /// <summary>Whether fresh water at the column's surface freezes as it is placed.</summary>
        public readonly bool[] Freezes = new bool[ChunkSize * ChunkSize];

        /// <summary>The top water block of every column: the sea's flush row, a lake's or a river's surface.</summary>
        public readonly int[] WaterTop = new int[ChunkSize * ChunkSize];

        /// <summary>Whether the column holds lake water.</summary>
        public readonly bool[] IsLake = new bool[ChunkSize * ChunkSize];

        /// <summary>Whether the border taper moved the column, which then holds no lake or pool.</summary>
        public readonly bool[] Tapered = new bool[ChunkSize * ChunkSize];

        /// <summary>Rivers' sample per column, or null when Rivers is not shaping this chunk.</summary>
        public RiversCompat.Sample[] RiverColumn;

        /// <summary>
        /// The lowest river bed or pool floor in the chunk. The bulk fill cannot look at individual
        /// columns, so it has to stop underneath every channel that will be cut out of them.
        /// </summary>
        public int MinChannelFloor = int.MaxValue;

        /// <summary>The lowest of <see cref="SurfaceY"/>.</summary>
        public int MinSurface = int.MaxValue;

        /// <summary>The highest of <see cref="SurfaceY"/>.</summary>
        public int MaxSurface;

        /// <summary>The highest of <see cref="WaterTop"/>, at least the sea's flush row.</summary>
        public int MaxWaterTop;
    }

    /// <summary>
    /// Every column's ground and lake water, from the model's tile, the border taper and Rivers'
    /// valleys.
    /// </summary>
    /// <param name="chunkX">The chunk column's X, in chunks.</param>
    /// <param name="chunkZ">The chunk column's Z, in chunks.</param>
    /// <param name="taperMap">Blend targets towards neighbours' committed heights, or null.</param>
    /// <param name="lakeTop">Each column's lake surface, or null without lakes.</param>
    /// <param name="riverSamples">Rivers' samples for the chunk, or null when Rivers is not shaping it.</param>
    /// <returns>The plan, before channels and pools are cut; <see cref="ColumnPlan.GroundY"/> is still unset.</returns>
    private ColumnPlan PlanColumns(int chunkX, int chunkZ, WeightedTaper[] taperMap, short[] lakeTop,
                                   Array riverSamples)
    {
        var plan = new ColumnPlan { MaxWaterTop = _seaLevel - 1 };
        int[] surfaceY = plan.SurfaceY;
        int[] waterTop = plan.WaterTop;
        bool[] isLake = plan.IsLake;
        bool[] tapered = plan.Tapered;
        RiversCompat.Sample[] riverColumn = riverSamples == null ? null : new RiversCompat.Sample[1024];
        plan.RiverColumn = riverColumn;

        // A chunk normally sits inside a single terrain tile; threading the tile through the loop
        // keeps that to one cache lookup instead of one per block column.
        TerrainTile tile = null;

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
                        plan.MinChannelFloor = Math.Min(plan.MinChannelFloor,
                            RiversCompat.ChannelFloorY(sample, _seaLevel, _mapSizeY));
                    }
                }

                y = GameMath.Clamp(y, 1, _mapSizeY - 2);
                surfaceY[index2d] = y;
                plan.IsOcean[index2d] = tile.ElevationMeters[tileIndex] < 0f;
                plan.Freezes[index2d] = tile.TemperatureC[tileIndex] < WaterFreezingTempOnGen;

                // A column the border taper moved no longer has the height the lake was worked out
                // for, so it holds no lake water rather than risk a spill.
                int lake = lakeTop?[index2d] ?? 0;
                if (tapered[index2d]) lake = Math.Min(lake, y);
                isLake[index2d] = lake >= _seaLevel && lake > y;
                waterTop[index2d] = isLake[index2d] ? lake : _seaLevel - 1;
                if (waterTop[index2d] > plan.MaxWaterTop) plan.MaxWaterTop = waterTop[index2d];

                if (y < plan.MinSurface) plan.MinSurface = y;
                if (y > plan.MaxSurface) plan.MaxSurface = y;
            }
        }
        return plan;
    }

    /// <summary>
    /// Cuts the river channels into the plan, the ground stopping at the bed and river water
    /// filling it up to the river's surface, flush with the banks; saves their current; and, where
    /// Rivers is not shaping the chunk, opens the wetland's pools.
    /// </summary>
    /// <param name="plan">The plan; its <see cref="ColumnPlan.GroundY"/> is set here.</param>
    /// <param name="water">The chunk's settled water, or null without lakes and rivers.</param>
    /// <param name="mapChunk">Receives the rivers' current.</param>
    /// <param name="chunkX">The chunk column's X, in chunks.</param>
    /// <param name="chunkZ">The chunk column's Z, in chunks.</param>
    private void CutWater(ColumnPlan plan, SettledWater water, IMapChunk mapChunk, int chunkX, int chunkZ)
    {
        int[] surfaceY = plan.SurfaceY;
        int[] groundY = (int[])surfaceY.Clone();
        plan.GroundY = groundY;
        int[] waterTop = plan.WaterTop;
        bool[] isOcean = plan.IsOcean;

        Channels channels = water?.Rivers is { Length: > 0 } rivers ? StampChannels(rivers, chunkX, chunkZ) : null;
        if (channels != null)
        {
            for (int i = 0; i < groundY.Length; i++)
            {
                if (channels.Top[i] == int.MaxValue || isOcean[i]) continue;
                int top = Math.Min(channels.Top[i], surfaceY[i]);
                int bed = Math.Max(1, top - channels.Depth[i]);
                groundY[i] = Math.Min(groundY[i], bed);
                plan.MinChannelFloor = Math.Min(plan.MinChannelFloor, bed);
                waterTop[i] = Math.Max(waterTop[i], channels.Wet[i] ? top : bed);
            }
            WriteRiverFlow(mapChunk, channels, groundY, surfaceY, isOcean);
        }

        byte[] wetland = water?.Wetland;
        if (wetland != null && plan.RiverColumn == null)
        {
            AddWetlandPools(chunkX, chunkZ, wetland, water.Rivers, surfaceY, groundY, waterTop, plan.IsLake,
                plan.Tapered, ref plan.MinChannelFloor);
        }
    }

    /// <summary>
    /// Places the plan's blocks: mantle at the bottom, rock up to the ground, water over it, and
    /// lake ice on fresh water that freezes. Writes the terrain and rain height maps as it goes.
    /// </summary>
    /// <param name="chunks">The chunk column, bottom first.</param>
    /// <param name="plan">The finished plan.</param>
    private void PlaceBlocks(IServerChunk[] chunks, ColumnPlan plan)
    {
        int defaultRockId = _globalConfig.defaultRockId;
        int freshWaterId = _globalConfig.waterBlockId;
        int saltWaterId = _globalConfig.saltWaterBlockId;
        int lakeIceId = _globalConfig.lakeIceBlockId;

        IMapChunk mapChunk = chunks[0].MapChunk;
        ushort[] rainHeightMap = mapChunk.RainHeightMap;
        ushort[] terrainHeightMap = mapChunk.WorldGenTerrainHeightMap;
        int[] groundY = plan.GroundY;
        int[] waterTop = plan.WaterTop;
        bool[] isOcean = plan.IsOcean;
        bool[] isLake = plan.IsLake;
        bool[] freezes = plan.Freezes;
        RiversCompat.Sample[] riverColumn = plan.RiverColumn;

        // Layer 0 is always mantle, mirroring vanilla.
        chunks[0].Data.SetBlockBulk(0, ChunkSize, ChunkSize, _globalConfig.mantleBlockId);

        // Bulk-fill every layer that is solid across the whole chunk column.
        int solidTo = Math.Max(1, plan.MinSurface);

        // A channel cut below that level would be filled in again here and never reopened, since
        // the per-column pass below only starts above it.
        if (plan.MinChannelFloor != int.MaxValue) solidTo = Math.Max(1, Math.Min(solidTo, plan.MinChannelFloor));
        IChunkBlocks data = chunks[0].Data;
        for (int y = 1; y <= solidTo; y++)
        {
            if (y % ChunkSize == 0) data = chunks[y / ChunkSize].Data;
            data.SetBlockBulk(y % ChunkSize * ChunkSize * ChunkSize, ChunkSize, ChunkSize, defaultRockId);
        }

        int fillTop = Math.Max(plan.MaxSurface, plan.MaxWaterTop);

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
    }

    /// <summary>Vanilla's freezing threshold (degrees Celsius) for water placed during world generation.</summary>
    private const float WaterFreezingTempOnGen = -15f;

    /// <summary>A block's index within one chunk's data, from local coordinates.</summary>
    private static int ChunkIndex3d(int x, int y, int z) => (y * ChunkSize + z) * ChunkSize + x;
}
