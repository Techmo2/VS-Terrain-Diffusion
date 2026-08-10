using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>How much of the world's climate the diffusion model is responsible for.</summary>
public enum DiffusionClimateMode
{
    /// <summary>Vanilla climate, untouched.</summary>
    Off,

    /// <summary>
    /// Latitude sets the baseline temperature; the model supplies a regional anomaly on top of it
    /// plus its own lapse rate and rainfall.
    /// </summary>
    Regional,

    /// <summary>Temperature comes entirely from the model, with no latitude gradient at all.</summary>
    Full
}

/// <summary>
/// Feeds Vintage Story's climate map, so block layers, trees, snow and the survival temperature
/// system follow the same world the terrain came from.
///
/// This wraps the vanilla climate layer rather than replacing it. The model's climate is a
/// *regional* sample — Perlin fields quantile-matched to WorldClim — and carries no global latitude
/// structure, so on its own it produces a world with no poles, no equator, and no relationship to
/// the starting-climate setting. Latitude therefore stays in charge of the broad temperature bands,
/// and the model contributes what it is actually good at: regional character (continentality,
/// maritime moderation, rain shadows) and terrain-driven lapse rate.
///
/// The packed climate integer is (unscaledTemperature &lt;&lt; 16) | (rainfall &lt;&lt; 8) | geologicActivity,
/// holding *sea-level* values. Vintage Story re-applies its own altitude corrections when it reads
/// them, so what is written here is pre-compensated: the values are chosen such that the temperature
/// and rainfall the game computes *at the terrain surface* are the ones intended for that spot.
/// </summary>
public sealed class DiffusionClimateMapLayer : MapLayerBase
{
    /// <summary>
    /// Mean of the model's temperature distribution, in degrees Celsius. Subtracting it turns the
    /// model's absolute temperature into an anomaly that can be added to the latitude baseline.
    /// This is the same constant the pipeline uses to normalise the coarse temperature channel.
    /// </summary>
    private const float ModelMeanTemperatureC = 15.87f;

    /// <summary>
    /// How much of the model's temperature anomaly to apply in
    /// <see cref="DiffusionClimateMode.Regional"/>; <c>worldGen.regionalClimateStrength</c>.
    /// </summary>
    private readonly float _regionalAnomalyStrength;

    private readonly PrecipitationScale _rainfall;
    private readonly MapLayerBase _baseline;
    private readonly TerrainDiffusionProvider _provider;
    private readonly DiffusionClimateMode _mode;
    private readonly int _blocksPerPixel;
    private readonly int _seaLevel;
    private readonly float _temperatureMultiplier;
    private readonly float _rainfallMultiplier;

    /// <summary>The vanilla layer this one decorates, so re-initialisation does not wrap twice.</summary>
    public MapLayerBase Baseline => _baseline;

    public DiffusionClimateMapLayer(long seed, MapLayerBase baseline, TerrainDiffusionProvider provider,
                                    DiffusionClimateMode mode, int seaLevel,
                                    float temperatureMultiplier, float rainfallMultiplier)
        : base(seed)
    {
        _baseline = baseline;
        _provider = provider;
        _mode = mode;
        _blocksPerPixel = TerraGenConfig.climateMapScale;
        _seaLevel = seaLevel;
        _temperatureMultiplier = temperatureMultiplier;
        _rainfallMultiplier = rainfallMultiplier;
        _regionalAnomalyStrength = DiffusionConfig.Instance.WorldGen.RegionalClimateStrength;
        _rainfall = PrecipitationScale.FromConfig(DiffusionConfig.Instance.WorldGen);
    }

    public override int[] GenLayer(int xCoord, int zCoord, int sizeX, int sizeZ)
    {
        // The vanilla layer still provides the latitude gradient and the geologic activity byte,
        // which honours polarEquatorDistance, startingClimate, globalTemperature and geologicActivity.
        int[] baseline = _baseline.GenLayer(xCoord, zCoord, sizeX, sizeZ);
        if (_mode == DiffusionClimateMode.Off) return baseline;

        var result = new int[sizeX * sizeZ];
        TerrainTile tile = null;

        for (int z = 0; z < sizeZ; z++)
        {
            int blockZ = (zCoord + z) * _blocksPerPixel;
            for (int x = 0; x < sizeX; x++)
            {
                int blockX = (xCoord + x) * _blocksPerPixel;
                int pixel = z * sizeX + x;

                int baseClimate = baseline[pixel];
                int baseTemperature = (baseClimate >> 16) & 0xFF;
                int geologicActivity = baseClimate & 0xFF;

                _provider.GetTileAt(blockX, blockZ, ref tile);
                int index = tile.Index(
                    Mod(blockX - tile.BlockX, tile.Size),
                    Mod(blockZ - tile.BlockZ, tile.Size));

                // Climate is read at whatever surface is exposed to the sky, which over water is the
                // sea surface rather than the sea bed. Compensating against a sea bed hundreds of
                // blocks down would make every ocean read tens of degrees too cold.
                int surfaceY = Math.Max(tile.SurfaceY[index], _seaLevel - 1);
                int distanceToSeaLevel = surfaceY - _seaLevel;

                float modelSeaLevel = tile.SeaLevelTemperatureC[index];

                // The model's own lapse contribution, already folded into its surface temperature.
                float elevationTerm = tile.TemperatureC[index] - modelSeaLevel;

                float seaLevelTemperature;
                if (_mode == DiffusionClimateMode.Full)
                {
                    seaLevelTemperature = modelSeaLevel * _temperatureMultiplier;
                }
                else
                {
                    float latitudeTemperature = baseTemperature / Climate.TemperatureScaleConversion - 20f;
                    seaLevelTemperature = latitudeTemperature
                                        + _regionalAnomalyStrength * (modelSeaLevel - ModelMeanTemperatureC);
                }

                float surfaceTemperature = seaLevelTemperature + elevationTerm;

                // Undo the game's own altitude corrections so the surface lands on the intended value.
                int unscaledTemperature = GameMath.Clamp(
                    (int)Math.Round((surfaceTemperature + 20f) * Climate.TemperatureScaleConversion
                                    + distanceToSeaLevel / 1.5f), 0, 255);

                // Vanilla rainfall is pure noise with no latitude structure, so the model's
                // physically-derived precipitation simply replaces it.
                int modelRainfall = _rainfall.ToRainfall(tile.PrecipitationMm[index] * _rainfallMultiplier);
                int rainfall = GameMath.Clamp(
                    modelRainfall
                    - distanceToSeaLevel / 2
                    - 5 * GameMath.Clamp(8 + _seaLevel - surfaceY, 0, 8), 0, 255);

                result[pixel] = (unscaledTemperature << 16) | (rainfall << 8) | geologicActivity;
            }
        }

        return result;
    }

    /// <summary>Maps annual precipitation in millimetres onto the 0-255 rainfall scale.</summary>
    public static int PrecipitationToRainfall(float precipitationMm) =>
        PrecipitationScale.FromConfig(DiffusionConfig.Instance.WorldGen).ToRainfall(precipitationMm);

    private static int Mod(int a, int b)
    {
        int m = a % b;
        return m < 0 ? m + b : m;
    }
}

/// <summary>
/// Feeds Vintage Story's ocean map from the model's elevation, so systems that avoid the sea
/// (dungeons, some structures) agree with the terrain that actually got generated.
/// </summary>
public sealed class DiffusionOceanMapLayer : MapLayerBase
{
    private readonly TerrainDiffusionProvider _provider;
    private readonly int _blocksPerPixel;

    /// <summary>Depth in metres at which a pixel counts as fully oceanic.</summary>
    private const float FullOceanDepthMeters = 200f;

    public DiffusionOceanMapLayer(long seed, TerrainDiffusionProvider provider) : base(seed)
    {
        _provider = provider;
        _blocksPerPixel = TerraGenConfig.oceanMapScale;
    }

    public override int[] GenLayer(int xCoord, int zCoord, int sizeX, int sizeZ)
    {
        var result = new int[sizeX * sizeZ];
        TerrainTile tile = null;

        for (int z = 0; z < sizeZ; z++)
        {
            int blockZ = (zCoord + z) * _blocksPerPixel;
            for (int x = 0; x < sizeX; x++)
            {
                int blockX = (xCoord + x) * _blocksPerPixel;

                _provider.GetTileAt(blockX, blockZ, ref tile);
                int index = tile.Index(
                    Mod(blockX - tile.BlockX, tile.Size),
                    Mod(blockZ - tile.BlockZ, tile.Size));

                float elevation = tile.ElevationMeters[index];
                int oceanicity = elevation >= 0f
                    ? 0
                    : GameMath.Clamp((int)(255f * Math.Min(1f, -elevation / FullOceanDepthMeters)), 0, 255);

                result[z * sizeX + x] = oceanicity;
            }
        }

        return result;
    }

    private static int Mod(int a, int b)
    {
        int m = a % b;
        return m < 0 ? m + b : m;
    }
}
