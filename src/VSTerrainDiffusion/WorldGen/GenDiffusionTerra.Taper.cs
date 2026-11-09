using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods.NoObf;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Vanilla's border taper: blending a chunk towards the heights its neighbours already committed
/// to.
/// </summary>
public sealed partial class GenDiffusionTerra
{
    /// <summary>Per-column blend targets used when a neighbouring chunk already fixed its heights.</summary>
    private struct WeightedTaper
    {
        /// <summary>The neighbours' committed height, weighted by closeness.</summary>
        public float TerrainYPos;

        /// <summary>How far to blend towards <see cref="TerrainYPos"/>, 0 to 1.</summary>
        public float Weight;
    }

    /// <summary>Whether a mod asked that this chunk's border not be tapered.</summary>
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
}
