using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
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
    private readonly MapLayerBase _baseline;
    private readonly RainfallScale _rainfall;
    private readonly int _seaLevel;

    /// <summary>The vanilla layer this one decorates, so re-initialisation does not wrap twice.</summary>
    public MapLayerBase Baseline => _baseline;

    public DiffusionClimateMapLayer(long seed, MapLayerBase baseline, TerrainDiffusionProvider provider,
                                    DiffusionWorldSettings settings)
        : base(seed, provider, TerraGenConfig.climateMapScale)
    {
        _baseline = baseline;
        _seaLevel = settings.SeaLevel;
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

/// <summary>
/// Replaces Vintage Story's forest density map.
///
/// Vanilla's is <c>MapLayerWobbledForest</c>, which computes <c>128 - rain * temp / 65025</c>; that
/// product never exceeds 1, so forest cover in an unmodified world is pure noise with no
/// relationship to climate whatsoever. Here it comes from the model's moisture and growing season,
/// which is what puts woodland in the foothills, scrub on the dry plateau and nothing above the
/// treeline.
///
/// Climate alone makes a wet region one unbroken wood, though. Vanilla's map is still good for what
/// it is - patchy noise - so it is kept and read as where the clearings go: its open ground thins
/// the model's cover into fields and glens (<see cref="WorldGenConfig.ForestClearings"/>). Shrubs
/// get the same from vanilla's shrub map, which is the same noise on its own seed and a finer grid
/// (<see cref="WorldGenConfig.ShrubClearings"/>).
/// </summary>
public sealed class DiffusionForestMapLayer : DiffusionMapLayer
{
    private readonly FastNoiseLite _variation;
    private readonly float _multiplier;
    private readonly bool _shrubs;
    private readonly MapLayerBase _clearings;
    private readonly float _clearingStrength;

    /// <summary>
    /// Vanilla's value, as a fraction, below which its ground counts as open. About a third of
    /// vanilla's map is 0 and the rest spreads evenly to 1, so this is a soft edge round the open
    /// patches rather than a second threshold on top of them.
    /// </summary>
    private const float ClearingEdge = 0.35f;

    /// <summary>
    /// How much local noise breaks up an otherwise uniform stand of trees. Climate sets the mean
    /// cover; this is the difference between a forest and a lawn of evenly spaced trunks.
    /// </summary>
    private const float VariationAmplitude = 0.18f;

    /// <summary>The vanilla layer read for clearings, or null; kept so re-initialisation does not wrap twice.</summary>
    public MapLayerBase Clearings => _clearings;

    /// <param name="clearings">
    /// Vanilla's forest or shrub layer, whose open ground becomes clearings, or null for none.
    /// </param>
    /// <param name="clearingStrength">How much of the model's cover the open ground takes away, 0 to 1.</param>
    public DiffusionForestMapLayer(long seed, TerrainDiffusionProvider provider, int blocksPerPixel,
                                   bool shrubs, float multiplier,
                                   MapLayerBase clearings = null, float clearingStrength = 0f)
        : base(seed, provider, blocksPerPixel)
    {
        _shrubs = shrubs;
        _multiplier = multiplier;
        _clearings = clearingStrength > 0f ? clearings : null;
        _clearingStrength = clearingStrength;

        _variation = new FastNoiseLite((int)seed);
        _variation.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
        _variation.SetFrequency(1f / 480f);
        _variation.SetFractalType(FastNoiseLite.FractalType.FBm);
        _variation.SetFractalOctaves(3);
        _variation.SetFractalLacunarity(2f);
        _variation.SetFractalGain(0.5f);
    }

    public override int[] GenLayer(int xCoord, int zCoord, int sizeX, int sizeZ) =>
        Clear(base.GenLayer(xCoord, zCoord, sizeX, sizeZ), xCoord, zCoord, sizeX, sizeZ);

    /// <summary>Map pixels, and so blocks, per pixel edge.</summary>
    public int BlocksPerMapPixel => BlocksPerPixel;

    /// <summary>
    /// This map's pixels over one terrain tile, clearings and all, read from that tile alone: the
    /// same bytes the game gets for that ground. Row-major, <c>tile.Size / BlocksPerMapPixel</c> a
    /// side. For the debug map, which records tiles as they finish.
    /// </summary>
    public int[] CoverWithin(TerrainTile tile)
    {
        int size = Math.Max(1, tile.Size / BlocksPerPixel);
        int xCoord = FloorDiv(tile.BlockX, BlocksPerPixel), zCoord = FloorDiv(tile.BlockZ, BlocksPerPixel);
        return Clear(Sample(xCoord, zCoord, size, size, tile), xCoord, zCoord, size, size);
    }

    /// <summary>Thins <paramref name="cover"/> where vanilla's map is open.</summary>
    private int[] Clear(int[] cover, int xCoord, int zCoord, int sizeX, int sizeZ)
    {
        if (_clearings == null) return cover;

        int[] vanilla = VanillaCover(xCoord, zCoord, sizeX, sizeZ);
        for (int i = 0; i < cover.Length; i++)
        {
            float open = 1f - SmoothStep(vanilla[i] / 255f / ClearingEdge);
            cover[i] = (int)Math.Round(cover[i] * (1f - _clearingStrength * open));
        }
        return cover;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

    /// <summary>
    /// Vanilla's forest map over the same pixels. It subtracts <c>128 - rain * temp / 65025</c>
    /// read from the climate map it is handed, a term that is 127 to 128 whatever the climate, so a
    /// zeroed map in its place gives its own pattern to within a unit - and keeps this layer free of
    /// whatever maps the caller did or did not set, which the terrain sampler does not.
    /// </summary>
    private int[] VanillaCover(int xCoord, int zCoord, int sizeX, int sizeZ)
    {
        var input = new IntDataMap2D { Data = new int[sizeX * sizeZ], Size = sizeX };
        var output = new IntDataMap2D { Data = new int[sizeX * sizeZ], Size = sizeX };

        // The input maps are fields on the shared vanilla instance.
        lock (_clearings)
        {
            _clearings.SetInputMap(input, output);
            return _clearings.GenLayer(xCoord, zCoord, sizeX, sizeZ);
        }
    }

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
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
///
/// Only for <c>worldGen.oceanMap: "output"</c>, where the model invents the continents. The
/// default runs the other way round - see <see cref="OceanMapLandmask"/> - and there the ocean map
/// is the input the terrain was built from, so there is nothing here to correct.
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
