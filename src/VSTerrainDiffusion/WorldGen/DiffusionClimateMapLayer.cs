using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Feeds Vintage Story's climate map, so block layers, trees, snow and the survival temperature
/// system all follow the world the terrain came from.
///
/// The packed climate integer is (unscaledTemperature &lt;&lt; 16) | (rainfall &lt;&lt; 8) |
/// geologicActivity, and both values mean exactly what the game says they mean: the temperature this
/// column would have at sea level, and its annual precipitation. What makes that work is
/// <see cref="ClimateScale"/>, which replaces the game's fixed altitude correction with the real
/// lapse rate, so the surface reads back as the model predicted without the byte having to carry
/// the correction itself. Writing a pre-compensated value instead - the obvious alternative - costs
/// the byte its entire range on high ground and is what used to put a floor under the vertical
/// resolution.
///
/// Latitude plays no part. The model's temperature field is a real climatology with continents,
/// oceans, rain shadows and altitude in it; layering a synthetic pole-to-equator gradient on top
/// would only fight it.
/// </summary>
public sealed class DiffusionClimateMapLayer : DiffusionMapLayer
{
    /// <summary>Backing field of <see cref="Baseline"/>.</summary>
    private readonly MapLayerBase _baseline;

    /// <summary>Maps the model's moisture onto the rainfall byte.</summary>
    private readonly RainfallScale _rainfall;

    /// <summary>The world's sea level.</summary>
    private readonly int _seaLevel;

    /// <summary>The vanilla layer this one decorates, so re-initialisation does not wrap twice.</summary>
    public MapLayerBase Baseline => _baseline;

    /// <summary>Wraps vanilla's climate layer.</summary>
    /// <param name="seed">The layer's seed.</param>
    /// <param name="baseline">Vanilla's climate layer, which still supplies geologic activity.</param>
    /// <param name="provider">The world's terrain tiles.</param>
    /// <param name="settings">The world's settings, for its sea level.</param>
    public DiffusionClimateMapLayer(long seed, MapLayerBase baseline, TerrainDiffusionProvider provider,
                                    DiffusionWorldSettings settings)
        : base(seed, provider, TerraGenConfig.climateMapScale)
    {
        _baseline = baseline;
        _seaLevel = settings.SeaLevel;
        _rainfall = RainfallScale.FromConfig(DiffusionConfig.Instance.WorldGen);
    }

    /// <summary>
    /// The model's temperature and rainfall, with vanilla's geologic activity in the low byte.
    /// </summary>
    public override int[] GenLayer(int xCoord, int zCoord, int sizeX, int sizeZ)
    {
        // The vanilla layer still supplies the geologic activity byte, which honours the world's
        // geologicActivity setting and has nothing to do with climate.
        int[] baseline = _baseline.GenLayer(xCoord, zCoord, sizeX, sizeZ);
        int[] model = base.GenLayer(xCoord, zCoord, sizeX, sizeZ);

        for (int i = 0; i < model.Length; i++) model[i] |= baseline[i] & 0xFF;
        return model;
    }

    /// <summary>
    /// The packed climate of one column: sea-level temperature and rainfall, geologic activity left 0.
    /// </summary>
    protected override int ValueAt(TerrainTile tile, int index, int blockX, int blockZ)
    {
        // Climate is read at whatever surface is exposed to the sky, which over water is the sea
        // surface rather than the sea bed. Measuring from a sea bed hundreds of blocks down would
        // make every ocean read tens of degrees too warm at sea level.
        //
        // And at the ground as built, not as the model drew it. Rivers pulls its valleys down to
        // sea level wherever they run, through mountains too; taking the model's surface back to
        // sea level from the height the valley was carved out of stored the whole mountain's lapse
        // as warmth, and the river bed - which is at sea level - read it all back: 29 C in a canyon
        // whose model temperature is 11 C. From the built height, the bed gets the model's own
        // temperature and the valley walls warm only by as much as they were lowered.
        int surfaceY = Math.Max(Provider.BuiltSurfaceY(tile.SurfaceY[index], blockX, blockZ), _seaLevel - 1);
        int distanceToSeaLevel = surfaceY - _seaLevel;

        // The world's global temperature setting and the config's offset are already in the tile.
        Bioclim climate = tile.ClimateAt(index);

        // Take the model's surface temperature back down to sea level at the reference lapse rate.
        // The game undoes exactly this on read, through the same helper, so the surface returns the
        // model's own number and sea level returns something that means what it says.
        int unscaledTemperature = GameMath.Clamp(
            (int)Math.Round((climate.MeanTemperatureC + 20f) * Climate.TemperatureScaleConversion
                            + ClimateScale.ScaleDistance(distanceToSeaLevel) / 1.5f), 0, 255);

        // Straight through. The game's altitude and shoreline additions to rainfall are switched
        // off in ClimateScale, so there is nothing left to pre-compensate for.
        int rainfall = GameMath.Clamp(_rainfall.ToRainfall(climate), 0, 255);

        return (unscaledTemperature << 16) | (rainfall << 8);
    }
}
