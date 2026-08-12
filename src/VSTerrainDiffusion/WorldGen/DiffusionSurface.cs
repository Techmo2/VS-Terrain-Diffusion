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
                    continue;
                }

                if (_config.BareSlopeRock && tile.Slope[index] >= climate.BareSlopeThreshold)
                {
                    int rockId = mapChunk.TopRockIdMap?[flat] ?? 0;
                    if (rockId != 0) ScourToRock(chunks, lx, lz, surfaceY, rockId);
                }
            }
        }
    }

    /// <summary>
    /// Strips the loose surface layers off a column and leaves the bedrock showing. Only soil,
    /// gravel and sand are removed - anything else there is something a later pass placed
    /// deliberately, or the rock itself.
    /// </summary>
    private void ScourToRock(IServerChunk[] chunks, int lx, int lz, int topY, int rockId)
    {
        for (int depth = 0; depth < MaxSurfaceDepth; depth++)
        {
            int y = topY - depth;
            if (y < 1) return;

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
                    return;
            }
        }
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

    private static int Mod(int a, int b)
    {
        int m = a % b;
        return m < 0 ? m + b : m;
    }
}
