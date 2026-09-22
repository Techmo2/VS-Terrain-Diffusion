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

    private readonly int _mapSizeY;
    private readonly int _seaLevel;

    /// <summary>Per-column blend targets used when a neighbouring chunk already fixed its heights.</summary>
    private struct WeightedTaper
    {
        public float TerrainYPos;
        public float Weight;
    }

    public GenDiffusionTerra(ICoreServerAPI api, TerrainDiffusionProvider provider, DiffusionWorldSettings settings)
    {
        _api = api;
        _provider = provider;
        _settings = settings;
        _globalConfig = GlobalConfig.GetInstance(api);
        _mapSizeY = api.WorldManager.MapSizeY;
        _seaLevel = api.World.SeaLevel;
    }

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

        int minSurface = int.MaxValue;
        int maxSurface = 0;

        // A chunk normally sits inside a single terrain tile; threading the tile through the loop
        // keeps that to one cache lookup instead of one per block column.
        TerrainTile tile = null;

        // Rivers, when it is installed and has been told to leave the terrain to us. This also
        // writes the flow vectors and river distances its boats and rendering read back, so it has
        // to happen for every chunk, not only the ones a river runs through.
        Array riverSamples = RiversCompat.SamplesForChunk(chunkX, chunkZ, chunks);
        int valleyFloorY = riverSamples == null ? 0 : RiversCompat.ValleyFloorY(_seaLevel);
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
                        y = (int)GameMath.Lerp(y, taper.TerrainYPos, taper.Weight);
                    }
                }

                if (riverColumn != null)
                {
                    RiversCompat.Sample sample = RiversCompat.At(riverSamples, index2d);
                    riverColumn[index2d] = sample;

                    // A river sits just above sea level wherever it runs, so the ground has to come
                    // down to meet it. Outside the valley the weight is 1 and the model's own
                    // landscape is untouched.
                    //
                    // Only ever downwards. Ground already below the valley floor is sea bed, and
                    // pulling it *towards* the floor raises it: that walled every river mouth off
                    // from the ocean with a bar of sand at exactly sea level, a valley's width
                    // wide, and left the river ending in a lagoon.
                    if (sample.InValley(RiversCompat.MaxValleyWidth) && y > valleyFloorY)
                    {
                        float keep = RiversCompat.ModelWeight(sample, worldX, worldZ);
                        y = (int)Math.Round(valleyFloorY + (y - valleyFloorY) * keep);
                    }

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

                if (y < minSurface) minSurface = y;
                if (y > maxSurface) maxSurface = y;
            }
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

        int fillTop = Math.Max(maxSurface, _seaLevel - 1);

        for (int lz = 0; lz < ChunkSize; lz++)
        {
            for (int lx = 0; lx < ChunkSize; lx++)
            {
                int index2d = lz * ChunkSize + lx;
                int surface = surfaceY[index2d];
                int waterId = isOcean[index2d] ? saltWaterId : freshWaterId;

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
                            if (y < _seaLevel)
                            {
                                blocks.SetFluid(index3d, freshWaterId);
                            }
                            continue;
                        }

                        blocks[index3d] = defaultRockId;
                    }
                    else if (y < _seaLevel)
                    {
                        // Freshwater lakes can freeze over; open ocean uses the salt water block.
                        int fluid = (y == _seaLevel - 1 && freezes[index2d] && waterId != saltWaterId)
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
                rainHeightMap[index2d] = (ushort)Math.Clamp(Math.Max(surface, _seaLevel - 1), 1, _mapSizeY - 2);
            }
        }

        ushort yMax = 0;
        for (int i = 0; i < rainHeightMap.Length; i++) yMax = Math.Max(yMax, rainHeightMap[i]);
        mapChunk.YMax = yMax;
    }

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
