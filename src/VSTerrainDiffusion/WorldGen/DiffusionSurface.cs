using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Corrects the two places where Vintage Story's surface layers disagree with the landscape the
/// model produced, after <c>GenBlockLayers</c> has had its say.
///
/// Vanilla decides the top block from temperature, rainfall and altitude, and has no notion of how
/// steep the ground is: a vertical cliff face gets the same eight blocks of soil as the meadow
/// above it, which is why mountains in an unmodified world look upholstered. It also has no notion
/// of permanent ice, only of snow that falls and melts. Both are things the model can answer -
/// slope from its heightmap, and whether the warmest month ever climbs above freezing from its
/// temperature seasonality - so those two cases are fixed up here and everything else is left to
/// vanilla.
/// </summary>
public sealed class DiffusionSurface
{
    private readonly ICoreServerAPI _api;
    private readonly TerrainDiffusionProvider _provider;
    private readonly WorldGenConfig _config;
    private readonly int _glacierIceId;

    /// <summary>Blocks of glacier ice capping ground that never thaws.</summary>
    private const int GlacierDepth = 3;

    /// <summary>Deepest a soil layer can be, so scouring a cliff never walks the whole column.</summary>
    private const int MaxSurfaceDepth = 12;

    /// <summary>
    /// Blocks below the model's surface the ground has to have been carved before its built slope
    /// counts. Only Rivers cuts the ground away from the model's, and a block or two is rounding.
    /// </summary>
    private const int CarvedBlocks = 2;

    public DiffusionSurface(ICoreServerAPI api, TerrainDiffusionProvider provider)
    {
        _api = api;
        _provider = provider;
        _config = DiffusionConfig.Instance.WorldGen;
        _glacierIceId = api.World.GetBlock(new AssetLocation("glacierice"))?.BlockId ?? 0;
    }

    public bool Enabled => _config.BareSlopeRock || (_config.GlacierIce && _glacierIceId != 0);

    public void OnChunkColumnGeneration(IChunkColumnGenerateRequest request)
    {
        IServerChunk[] chunks = request.Chunks;
        IMapChunk mapChunk = chunks[0].MapChunk;
        if (mapChunk == null) return;

        int baseX = request.ChunkX * 32;
        int baseZ = request.ChunkZ * 32;
        TerrainTile tile = null;

        // The model's slope knows nothing of the valleys Rivers carves out of it, so a canyon wall
        // hundreds of blocks deep reads as whatever hillside the model drew there. Inside a cut the
        // ground as built is measured too, from this chunk's heightmap and its four neighbours',
        // which the terrain pass has always finished before this one runs.
        Heights built = RiversCompat.Installed ? new Heights(_api, request.ChunkX, request.ChunkZ, mapChunk) : null;
        DiffusionWorldSettings settings = _provider.Settings;
        float blockSlopeToReal = settings.MetersPerBlockVertical / settings.MetersPerBlock;

        for (int lz = 0; lz < 32; lz++)
        {
            for (int lx = 0; lx < 32; lx++)
            {
                int flat = lz * 32 + lx;
                int surfaceY = mapChunk.RainHeightMap[flat];
                if (surfaceY <= 0 || surfaceY >= _api.WorldManager.MapSizeY - 1) continue;

                int blockX = baseX + lx, blockZ = baseZ + lz;
                _provider.GetTileAt(blockX, blockZ, ref tile);
                int index = tile.Index(
                    Mod(blockX - tile.BlockX, tile.Size),
                    Mod(blockZ - tile.BlockZ, tile.Size));

                if (tile.ElevationMeters[index] <= 0f) continue;

                Bioclim climate = tile.ClimateAt(index);

                if (_config.GlacierIce && _glacierIceId != 0 && climate.IsPermanentIce)
                {
                    Fill(chunks, lx, lz, surfaceY, GlacierDepth, _glacierIceId);
                    ClearPlantAbove(chunks, lx, lz, surfaceY);
                    continue;
                }

                float slope = tile.Slope[index];
                if (built != null && tile.SurfaceY[index] - built.At(lx, lz) >= CarvedBlocks)
                    slope = Math.Max(slope, built.SlopeAt(lx, lz) * blockSlopeToReal);

                if (_config.BareSlopeRock && slope >= climate.BareSlopeThreshold)
                {
                    int rockId = mapChunk.TopRockIdMap?[flat] ?? 0;
                    if (rockId != 0 && ScourToRock(chunks, lx, lz, surfaceY, rockId))
                        ClearPlantAbove(chunks, lx, lz, surfaceY);
                }
            }
        }
    }

    /// <summary>
    /// Strips the loose surface layers off a column and leaves the bedrock showing. Only soil,
    /// gravel and sand are removed - anything else there is something a later pass placed
    /// deliberately, or the rock itself. True if the top block was stripped.
    /// </summary>
    private bool ScourToRock(IServerChunk[] chunks, int lx, int lz, int topY, int rockId)
    {
        for (int depth = 0; depth < MaxSurfaceDepth; depth++)
        {
            int y = topY - depth;
            if (y < 1) return depth > 0;

            int flat = (32 * (y % 32) + lz) * 32 + lx;
            IChunkBlocks data = chunks[y / 32].Data;
            Block block = _api.World.Blocks[data.GetBlockIdUnsafe(flat)];

            switch (block?.BlockMaterial)
            {
                case EnumBlockMaterial.Soil:
                case EnumBlockMaterial.Gravel:
                case EnumBlockMaterial.Sand:
                    data.SetBlockUnsafe(flat, rockId);
                    continue;
                default:
                    return depth > 0;
            }
        }
        return true;
    }

    /// <summary>
    /// Removes the tall grass vanilla's block layers planted on the soil just replaced. It goes one
    /// block above the rain heightmap, which is left at the soil, so stripping from the heightmap
    /// down leaves it standing on bare rock or ice. Plants are all that pass places up there.
    /// </summary>
    private void ClearPlantAbove(IServerChunk[] chunks, int lx, int lz, int topY)
    {
        int y = topY + 1;
        if (y >= _api.WorldManager.MapSizeY) return;

        int flat = (32 * (y % 32) + lz) * 32 + lx;
        IChunkBlocks data = chunks[y / 32].Data;
        if (_api.World.Blocks[data.GetBlockIdUnsafe(flat)]?.BlockMaterial == EnumBlockMaterial.Plant)
            data.SetBlockUnsafe(flat, 0);
    }

    private static void Fill(IServerChunk[] chunks, int lx, int lz, int topY, int depth, int blockId)
    {
        for (int i = 0; i < depth; i++)
        {
            int y = topY - i;
            if (y < 1) return;

            int flat = (32 * (y % 32) + lz) * 32 + lx;
            IChunkBlocks data = chunks[y / 32].Data;
            if (data.GetBlockIdUnsafe(flat) == 0) continue;

            data.SetBlockUnsafe(flat, blockId);
            data.SetFluid(flat, 0);
        }
    }

    /// <summary>
    /// Ground heights as built around one chunk: its own heightmap and, across each edge, the
    /// neighbour's. A neighbour that is missing or has no terrain yet leaves that side to a one-sided
    /// difference.
    /// </summary>
    private sealed class Heights
    {
        private readonly ushort[] _self, _west, _east, _north, _south;

        public Heights(ICoreServerAPI api, int chunkX, int chunkZ, IMapChunk self)
        {
            _self = self.WorldGenTerrainHeightMap;
            _west = Map(api, chunkX - 1, chunkZ);
            _east = Map(api, chunkX + 1, chunkZ);
            _north = Map(api, chunkX, chunkZ - 1);
            _south = Map(api, chunkX, chunkZ + 1);
        }

        private static ushort[] Map(ICoreServerAPI api, int chunkX, int chunkZ) =>
            api.WorldManager.GetMapChunk(chunkX, chunkZ)?.WorldGenTerrainHeightMap;

        public int At(int lx, int lz) => _self[lz * 32 + lx];

        /// <summary>Height at a column up to a chunk outside this one along one axis, or -1 where unknown.</summary>
        private int Around(int lx, int lz)
        {
            ushort[] map = lx < 0 ? _west : lx > 31 ? _east : lz < 0 ? _north : lz > 31 ? _south : _self;
            if (map == null) return -1;
            int h = map[((lz + 32) % 32) * 32 + (lx + 32) % 32];
            return h > 0 ? h : -1;
        }

        /// <summary>
        /// Blocks either side the built slope is measured across. Ground is whole blocks, so gentle
        /// ground is flat runs joined by one-block steps, and measured from its next-door columns a
        /// step reads 0.5, or 0.71 where it runs diagonally - steeper than an arid threshold - which
        /// laid a line of bare stone along every step of a shallow desert river bank. Across three a
        /// lone step reads 0.17, or 0.24 diagonally, while a wall of many blocks still reads as steep
        /// as it is.
        /// </summary>
        private const int Baseline = 3;

        /// <summary>Rise over run, in blocks, across <see cref="Baseline"/> either side where both are known.</summary>
        public float SlopeAt(int lx, int lz)
        {
            float centre = At(lx, lz);
            return MathF.Sqrt(Square(Gradient(Around(lx - Baseline, lz), centre, Around(lx + Baseline, lz))) +
                              Square(Gradient(Around(lx, lz - Baseline), centre, Around(lx, lz + Baseline))));
        }

        private static float Gradient(int before, float centre, int after) =>
            before >= 0 && after >= 0 ? (after - before) / (2f * Baseline)
            : after >= 0 ? (after - centre) / Baseline
            : before >= 0 ? (centre - before) / Baseline
            : 0f;

        private static float Square(float v) => v * v;
    }

    private static int Mod(int a, int b)
    {
        int m = a % b;
        return m < 0 ? m + b : m;
    }
}
