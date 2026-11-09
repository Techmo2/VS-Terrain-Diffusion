using System;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Feeds Vintage Story's ocean map from the model's elevation, so systems that avoid the sea
/// (dungeons, some structures) agree with the terrain that actually got generated.
///
/// Only for <c>worldGen.oceanMap: "output"</c>, where the model invents the continents. The
/// default runs the other way round - see <see cref="OceanMapLandmask"/> - and there the ocean map
/// is the input the terrain was built from, so there is nothing here to correct.
/// </summary>
public sealed class DiffusionOceanMapLayer : DiffusionMapLayer
{
    /// <summary>Depth in metres at which a pixel counts as fully oceanic.</summary>
    private const float FullOceanDepthMeters = 200f;

    /// <summary>An ocean map at the game's ocean map scale.</summary>
    /// <param name="seed">The layer's seed.</param>
    /// <param name="provider">The world's terrain tiles.</param>
    public DiffusionOceanMapLayer(long seed, TerrainDiffusionProvider provider)
        : base(seed, provider, TerraGenConfig.oceanMapScale)
    {
    }

    /// <summary>0 on land, rising to 255 at <see cref="FullOceanDepthMeters"/> below sea level.</summary>
    protected override int ValueAt(TerrainTile tile, int index, int blockX, int blockZ)
    {
        float elevation = tile.ElevationMeters[index];
        return elevation >= 0f
            ? 0
            : GameMath.Clamp((int)(255f * Math.Min(1f, -elevation / FullOceanDepthMeters)), 0, 255);
    }
}
