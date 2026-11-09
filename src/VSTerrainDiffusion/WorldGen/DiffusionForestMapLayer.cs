using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;
using static VSTerrainDiffusion.Core.IntMath;

namespace VSTerrainDiffusion.WorldGen;

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
    /// <summary>Local noise that breaks up uniform cover.</summary>
    private readonly FastNoiseLite _variation;

    /// <summary>The world's forest or shrub density setting.</summary>
    private readonly float _multiplier;

    /// <summary>Whether this is the shrub map rather than the forest map.</summary>
    private readonly bool _shrubs;

    /// <summary>Backing field of <see cref="Vanilla"/>.</summary>
    private readonly MapLayerBase _vanilla;

    /// <summary>How much of the cover vanilla's open ground takes away, 0 to 1.</summary>
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

    /// <summary>
    /// The vanilla layer this one reads clearings from, or null. Kept even with clearings off, so
    /// that re-initialisation unwraps to vanilla's layer instead of losing it, and the debug map
    /// can show it.
    /// </summary>
    public MapLayerBase Vanilla => _vanilla;

    /// <summary>A forest or shrub layer at one map scale.</summary>
    /// <param name="seed">The layer's seed, also the variation noise's.</param>
    /// <param name="provider">The world's terrain tiles.</param>
    /// <param name="blocksPerPixel">Blocks per map pixel edge.</param>
    /// <param name="shrubs">True for the shrub map, false for the forest map.</param>
    /// <param name="multiplier">The world's forest or shrub density setting.</param>
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
        _vanilla = clearings;
        _clearingStrength = clearingStrength;

        _variation = new FastNoiseLite((int)seed);
        _variation.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
        _variation.SetFrequency(1f / 480f);
        _variation.SetFractalType(FastNoiseLite.FractalType.FBm);
        _variation.SetFractalOctaves(3);
        _variation.SetFractalLacunarity(2f);
        _variation.SetFractalGain(0.5f);
    }

    /// <summary>The model's cover over a block of map pixels, thinned where vanilla's map is open.</summary>
    public override int[] GenLayer(int xCoord, int zCoord, int sizeX, int sizeZ) =>
        Clear(base.GenLayer(xCoord, zCoord, sizeX, sizeZ), xCoord, zCoord, sizeX, sizeZ);

    /// <summary>Map pixels, and so blocks, per pixel edge.</summary>
    public int BlocksPerMapPixel => BlocksPerPixel;

    /// <summary>
    /// This map's pixels over one terrain tile, read from that tile alone, at each stage: the
    /// model's cover, vanilla's map (null without one), and the two combined - the same bytes the
    /// game gets for that ground. Row-major, <c>tile.Size / BlocksPerMapPixel</c> a side. For the
    /// debug map, which records tiles as they finish.
    /// </summary>
    public (int[] Model, int[] Vanilla, int[] Applied) CoverWithin(TerrainTile tile)
    {
        int size = Math.Max(1, tile.Size / BlocksPerPixel);
        int xCoord = FloorDiv(tile.BlockX, BlocksPerPixel), zCoord = FloorDiv(tile.BlockZ, BlocksPerPixel);
        int[] model = Sample(xCoord, zCoord, size, size, tile);
        int[] vanilla = _vanilla == null ? null : VanillaCover(xCoord, zCoord, size, size);
        return (model, vanilla, Clear((int[])model.Clone(), vanilla));
    }

    /// <summary>Thins <paramref name="cover"/> where vanilla's map is open.</summary>
    private int[] Clear(int[] cover, int xCoord, int zCoord, int sizeX, int sizeZ)
    {
        if (_vanilla == null || _clearingStrength <= 0f) return cover;
        return Clear(cover, VanillaCover(xCoord, zCoord, sizeX, sizeZ));
    }

    /// <summary>
    /// Thins <paramref name="cover"/> in place by <see cref="_clearingStrength"/> where
    /// <paramref name="vanilla"/> is open; untouched when either is missing.
    /// </summary>
    private int[] Clear(int[] cover, int[] vanilla)
    {
        if (vanilla == null || _clearingStrength <= 0f) return cover;

        for (int i = 0; i < cover.Length; i++)
        {
            float open = 1f - SmoothStep(vanilla[i] / 255f / ClearingEdge);
            cover[i] = (int)Math.Round(cover[i] * (1f - _clearingStrength * open));
        }
        return cover;
    }

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
        lock (_vanilla)
        {
            _vanilla.SetInputMap(input, output);
            return _vanilla.GenLayer(xCoord, zCoord, sizeX, sizeZ);
        }
    }

    /// <summary>3t² - 2t³ on t clamped to [0, 1].</summary>
    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>The cover of one column, 0 to 255: none at sea or on ground too steep for soil.</summary>
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
