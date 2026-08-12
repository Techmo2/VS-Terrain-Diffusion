using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>How much of the world's climate the diffusion model is responsible for.</summary>
public enum DiffusionClimateMode
{
    /// <summary>Vanilla climate, untouched.</summary>
    Off,

    /// <summary>
    /// Temperature, rainfall and vegetation cover all come from the model, with no latitude
    /// gradient at all.
    /// </summary>
    Full
}

/// <summary>Shared plumbing for map layers that read the model tile under a map pixel.</summary>
public abstract class DiffusionMapLayer : MapLayerBase
{
    protected readonly TerrainDiffusionProvider Provider;
    protected readonly int BlocksPerPixel;

    protected DiffusionMapLayer(long seed, TerrainDiffusionProvider provider, int blocksPerPixel)
        : base(seed)
    {
        Provider = provider;
        BlocksPerPixel = blocksPerPixel;
    }

    public override int[] GenLayer(int xCoord, int zCoord, int sizeX, int sizeZ)
    {
        var result = new int[sizeX * sizeZ];
        TerrainTile tile = null;

        for (int z = 0; z < sizeZ; z++)
        {
            int blockZ = (zCoord + z) * BlocksPerPixel;
            for (int x = 0; x < sizeX; x++)
            {
                int blockX = (xCoord + x) * BlocksPerPixel;

                Provider.GetTileAt(blockX, blockZ, ref tile);
                int index = tile.Index(
                    Mod(blockX - tile.BlockX, tile.Size),
                    Mod(blockZ - tile.BlockZ, tile.Size));

                result[z * sizeX + x] = ValueAt(tile, index, blockX, blockZ);
            }
        }

        return result;
    }

    /// <summary>The packed map value for one column.</summary>
    protected abstract int ValueAt(TerrainTile tile, int index, int blockX, int blockZ);

    protected static int Mod(int a, int b)
    {
        int m = a % b;
        return m < 0 ? m + b : m;
    }
}

/// <summary>
/// Feeds Vintage Story's climate map, so block layers, trees, snow and the survival temperature
/// system all follow the world the terrain came from.
///
/// The packed climate integer is (unscaledTemperature &lt;&lt; 16) | (rainfall &lt;&lt; 8) |
/// geologicActivity. Vintage Story re-applies its own altitude corrections whenever it reads those,
/// so what is written here is pre-compensated: the values are chosen such that the temperature and
/// rainfall the game computes <em>at the terrain surface</em> are the ones the model predicted for
/// that spot. Without that, the game would subtract a lapse rate the model has already applied and
/// every mountain would come out twice as cold as it should be.
///
/// Latitude plays no part. The model's temperature field is a real climatology with continents,
/// oceans, rain shadows and altitude in it; layering a synthetic pole-to-equator gradient on top
/// would only fight it.
/// </summary>
public sealed class DiffusionClimateMapLayer : DiffusionMapLayer
{
    private readonly MapLayerBase _baseline;
    private readonly RainfallScale _rainfall;
    private readonly int _seaLevel;
    private readonly float _temperatureMultiplier;
    private readonly float _temperatureOffset;
    private readonly float _rainfallMultiplier;

    /// <summary>The vanilla layer this one decorates, so re-initialisation does not wrap twice.</summary>
    public MapLayerBase Baseline => _baseline;

    public DiffusionClimateMapLayer(long seed, MapLayerBase baseline, TerrainDiffusionProvider provider,
                                    int seaLevel, float temperatureMultiplier, float rainfallMultiplier)
        : base(seed, provider, TerraGenConfig.climateMapScale)
    {
        _baseline = baseline;
        _seaLevel = seaLevel;
        _temperatureMultiplier = temperatureMultiplier;
        _rainfallMultiplier = rainfallMultiplier;
        _temperatureOffset = DiffusionConfig.Instance.WorldGen.TemperatureOffsetC;
        _rainfall = RainfallScale.FromConfig(DiffusionConfig.Instance.WorldGen);
    }

    public override int[] GenLayer(int xCoord, int zCoord, int sizeX, int sizeZ)
    {
        // The vanilla layer still supplies the geologic activity byte, which honours the world's
        // geologicActivity setting and has nothing to do with climate.
        int[] baseline = _baseline.GenLayer(xCoord, zCoord, sizeX, sizeZ);
        int[] model = base.GenLayer(xCoord, zCoord, sizeX, sizeZ);

        for (int i = 0; i < model.Length; i++) model[i] |= baseline[i] & 0xFF;
        return model;
    }

    protected override int ValueAt(TerrainTile tile, int index, int blockX, int blockZ)
    {
        // Climate is read at whatever surface is exposed to the sky, which over water is the sea
        // surface rather than the sea bed. Compensating against a sea bed hundreds of blocks down
        // would make every ocean read tens of degrees too cold.
        int surfaceY = Math.Max(tile.SurfaceY[index], _seaLevel - 1);
        int distanceToSeaLevel = surfaceY - _seaLevel;

        Bioclim climate = tile.ClimateAt(index);
        float surfaceTemperature = climate.MeanTemperatureC * _temperatureMultiplier + _temperatureOffset;

        // Undo the game's own altitude correction so the surface lands on the intended value.
        int unscaledTemperature = GameMath.Clamp(
            (int)Math.Round((surfaceTemperature + 20f) * Climate.TemperatureScaleConversion
                            + distanceToSeaLevel / 1.5f), 0, 255);

        // Likewise for rainfall: the game adds height and a coastal bonus on read, and the model
        // has already accounted for both.
        int modelRainfall = (int)Math.Round(_rainfall.ToRainfall(climate) * _rainfallMultiplier);
        int rainfall = GameMath.Clamp(
            modelRainfall
            - distanceToSeaLevel / 2
            - 5 * GameMath.Clamp(8 + _seaLevel - surfaceY, 0, 8), 0, 255);

        return (unscaledTemperature << 16) | (rainfall << 8);
    }
}

/// <summary>
/// Replaces Vintage Story's forest density map.
///
/// Vanilla's is <c>MapLayerWobbledForest</c>, which computes <c>128 - rain * temp / 65025</c>; that
/// product never exceeds 1, so forest cover in an unmodified world is pure noise with no
/// relationship to climate whatsoever. Here it comes from the model's moisture and growing season,
/// which is what puts woodland in the foothills, scrub on the dry plateau and nothing above the
/// treeline.
/// </summary>
public sealed class DiffusionForestMapLayer : DiffusionMapLayer
{
    private readonly FastNoiseLite _variation;
    private readonly float _multiplier;
    private readonly bool _shrubs;

    /// <summary>
    /// How much local noise breaks up an otherwise uniform stand of trees. Climate sets the mean
    /// cover; this is the difference between a forest and a lawn of evenly spaced trunks.
    /// </summary>
    private const float VariationAmplitude = 0.18f;

    public DiffusionForestMapLayer(long seed, TerrainDiffusionProvider provider, int blocksPerPixel,
                                   bool shrubs, float multiplier)
        : base(seed, provider, blocksPerPixel)
    {
        _shrubs = shrubs;
        _multiplier = multiplier;

        _variation = new FastNoiseLite((int)seed);
        _variation.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
        _variation.SetFrequency(1f / 480f);
        _variation.SetFractalType(FastNoiseLite.FractalType.FBm);
        _variation.SetFractalOctaves(3);
        _variation.SetFractalLacunarity(2f);
        _variation.SetFractalGain(0.5f);
    }

    protected override int ValueAt(TerrainTile tile, int index, int blockX, int blockZ)
    {
        if (tile.ElevationMeters[index] <= 0f) return 0;

        Bioclim climate = tile.ClimateAt(index);
        float density = _shrubs ? climate.ShrubDensity : climate.ForestDensity;

        // Nothing takes root on ground too steep to hold soil, whatever the climate says.
        if (tile.Slope[index] >= climate.BareSlopeThreshold) return 0;

        density *= _multiplier;
        density += VariationAmplitude * _variation.GetNoise(blockX, blockZ) * density;

        return GameMath.Clamp((int)Math.Round(density * 255f), 0, 255);
    }
}

/// <summary>
/// Feeds Vintage Story's ocean map from the model's elevation, so systems that avoid the sea
/// (dungeons, some structures) agree with the terrain that actually got generated.
/// </summary>
public sealed class DiffusionOceanMapLayer : DiffusionMapLayer
{
    /// <summary>Depth in metres at which a pixel counts as fully oceanic.</summary>
    private const float FullOceanDepthMeters = 200f;

    public DiffusionOceanMapLayer(long seed, TerrainDiffusionProvider provider)
        : base(seed, provider, TerraGenConfig.oceanMapScale)
    {
    }

    protected override int ValueAt(TerrainTile tile, int index, int blockX, int blockZ)
    {
        float elevation = tile.ElevationMeters[index];
        return elevation >= 0f
            ? 0
            : GameMath.Clamp((int)(255f * Math.Min(1f, -elevation / FullOceanDepthMeters)), 0, 255);
    }
}
