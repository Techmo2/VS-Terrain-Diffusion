using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;
using static VSTerrainDiffusion.Core.IntMath;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>Shared plumbing for map layers that read the model tile under a map pixel.</summary>
public abstract class DiffusionMapLayer : MapLayerBase
{
    /// <summary>The world's terrain tiles.</summary>
    protected readonly TerrainDiffusionProvider Provider;

    /// <summary>Blocks per map pixel edge: the map's scale.</summary>
    protected readonly int BlocksPerPixel;

    /// <summary>A layer at one map scale.</summary>
    /// <param name="seed">The layer's seed.</param>
    /// <param name="provider">The world's terrain tiles.</param>
    /// <param name="blocksPerPixel">Blocks per map pixel edge.</param>
    protected DiffusionMapLayer(long seed, TerrainDiffusionProvider provider, int blocksPerPixel)
        : base(seed)
    {
        Provider = provider;
        BlocksPerPixel = blocksPerPixel;
    }

    /// <summary>The map over a block of pixels, each read from the tile under it.</summary>
    public override int[] GenLayer(int xCoord, int zCoord, int sizeX, int sizeZ) =>
        Sample(xCoord, zCoord, sizeX, sizeZ, null);

    /// <summary>
    /// The map over a block of pixels. With <paramref name="fixedTile"/> every pixel is read from
    /// that tile, and any outside it comes out 0, rather than asking the provider - for a tile that
    /// is still being handed round as it finishes, before the provider will return it.
    /// </summary>
    protected int[] Sample(int xCoord, int zCoord, int sizeX, int sizeZ, TerrainTile fixedTile)
    {
        var result = new int[sizeX * sizeZ];
        TerrainTile tile = fixedTile;

        for (int z = 0; z < sizeZ; z++)
        {
            int blockZ = (zCoord + z) * BlocksPerPixel;
            for (int x = 0; x < sizeX; x++)
            {
                int blockX = (xCoord + x) * BlocksPerPixel;

                // The game asks for a whole 512-block map region at a time. A chunk peek only ever
                // reads the few samples around the column it is inspecting, and then throws the
                // region away, so modelling the rest of it is terrain generated for nobody - and
                // the peeked region covers nine terrain tiles where the peek itself needs one.
                // Outside a peek this is always false and nothing changes.
                if (!ChunkPeekScope.Covers(blockX, blockZ))
                {
                    result[z * sizeX + x] = 0;
                    continue;
                }

                if (fixedTile == null)
                {
                    Provider.GetTileAt(blockX, blockZ, ref tile);
                }
                else if (blockX < tile.BlockX || blockX >= tile.BlockX + tile.Size ||
                         blockZ < tile.BlockZ || blockZ >= tile.BlockZ + tile.Size)
                {
                    result[z * sizeX + x] = 0;
                    continue;
                }

                int index = tile.Index(
                    FloorMod(blockX - tile.BlockX, tile.Size),
                    FloorMod(blockZ - tile.BlockZ, tile.Size));

                result[z * sizeX + x] = ValueAt(tile, index, blockX, blockZ);
            }
        }

        return result;
    }

    /// <summary>The packed map value for one column.</summary>
    protected abstract int ValueAt(TerrainTile tile, int index, int blockX, int blockZ);
}
