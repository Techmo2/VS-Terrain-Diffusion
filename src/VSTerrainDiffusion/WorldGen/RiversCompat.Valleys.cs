using System;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Cutting Rivers' valleys and channels into the model's terrain: Rivers' own curves, applied to
/// heights instead of landform weights.
/// </summary>
public static partial class RiversCompat
{
    /// <summary>
    /// How far from a channel Rivers strips the soil to make a shore, from
    /// <c>BlockLayersPatches.GetRiverPower</c>: none at the water's edge, full soil again ten
    /// blocks out. The ground under that strip has to be at the water, or the bare rock it leaves
    /// is a pavement along the top of a cliff rather than a beach.
    /// </summary>
    private const double ShoreBlocks = 10.0;

    /// <summary>
    /// The model's ground height at a column brought down into the river valley, if it is in one.
    ///
    /// A river sits just above sea level wherever it runs, so the ground has to come down to meet
    /// it. Outside the valley the weight is 1 and the model's own landscape is untouched.
    ///
    /// Only ever downwards. Ground already below the valley floor is sea bed, and pulling it
    /// *towards* the floor raises it: that walled every river mouth off from the ocean with a bar of
    /// sand at exactly sea level, a valley's width wide, and left the river ending in a lagoon.
    /// </summary>
    public static int ValleyHeight(int y, in Sample sample, int worldX, int worldZ, int seaLevel)
    {
        int valleyFloorY = ValleyFloorY(seaLevel);
        if (!sample.InValley(MaxValleyWidth) || y <= valleyFloorY) return y;

        float keep = ModelWeight(sample, worldX, worldZ);
        return (int)Math.Round(valleyFloorY + (y - valleyFloorY) * keep);
    }

    /// <summary>
    /// How much of the model's own height survives at a column, from 0 in the channel to 1 outside
    /// the valley.
    ///
    /// This is <c>RiversApi.ModifyLandformWeights</c> rearranged. That bends vanilla's landform
    /// weights towards a river landform whose key positions sit at sea level; there are no
    /// landforms here, so the same curve is applied to the height directly and the result is the
    /// same shape of valley.
    /// </summary>
    public static float ModelWeight(in Sample sample, int worldX, int worldZ)
    {
        if (!_available || sample.Distance >= _maxValleyWidth) return 1f;

        double noise = _valleyNoise(worldX, worldZ);
        noise = Math.Clamp(noise * _noiseExpansion, -1.0, 1.0);
        noise = (noise + 1.0) / 2.0;
        noise = Map(noise, 0.0, 1.0, 1f - _valleyStrengthMin, 1f - _valleyStrengthMax);
        if (noise < 0.02) noise = 0.02;

        double weight = Lerp(noise, 1.0, InverseLerp(sample.Distance, 0.0, _maxValleyWidth));
        weight *= weight;

        // Rivers' own curve leaves up to a third of the original height standing right at the
        // channel, which is harmless when the land beside it was already near sea level and is a
        // cliff when it was a hillside. Over the strip Rivers bares anyway, bring the ground all
        // the way down so that strip is the shore.
        return (float)(weight * InverseLerp(sample.Distance, 0.0, ShoreBlocks));
    }

    /// <summary>
    /// The level a valley floor is pulled down to: sea level, which is where Rivers' own river
    /// landform pins the ground beside a channel (<c>TerrainYKeyPositions[0]</c> is 22/51 of the
    /// world, the same fraction the game puts the sea at).
    ///
    /// Not <c>seaLevel + heightBoost</c>, which is where the *carve* is centred - that is the
    /// middle of the channel, not the top of its bank. Aiming the banks there left them nine
    /// blocks above the water, and since Rivers strips the soil within ten blocks of a channel to
    /// make a shore, the bare strip came out as a stone pavement along a clifftop instead of a
    /// beach at the water's edge.
    /// </summary>
    public static int ValleyFloorY(int seaLevel) => seaLevel;

    /// <summary>
    /// The highest block still solid beneath a channel - the river bed. Below this the column is
    /// ordinary ground, which is what lets the chunk fill still be done in bulk.
    /// </summary>
    public static int ChannelFloorY(in Sample sample, int seaLevel, int mapSizeY)
    {
        int bankHalf = (int)(sample.BankFactor * (mapSizeY - seaLevel));
        return seaLevel + _heightBoost - bankHalf;
    }

    /// <summary>
    /// Whether a block is inside the channel and should be left out. Mirrors
    /// <c>RiversApi.ShouldBeCarved</c> rather than calling it, because this is asked once per block
    /// rather than once per column.
    /// </summary>
    public static bool Carved(in Sample sample, int y, int seaLevel, int mapSizeY)
    {
        if (sample.Distance > 0.0) return false;

        int bankHalf = (int)(sample.BankFactor * (mapSizeY - seaLevel));
        int riverY = seaLevel + _heightBoost;
        return y > riverY - bankHalf && y < riverY + bankHalf * _topFactor;
    }

    /// <summary>Linearly maps a value from one range to another, unclamped.</summary>
    /// <param name="value">The value.</param>
    /// <param name="fromMin">Source range start.</param>
    /// <param name="fromMax">Source range end.</param>
    /// <param name="toMin">Target range start.</param>
    /// <param name="toMax">Target range end.</param>
    /// <returns>The mapped value.</returns>
    private static double Map(double value, double fromMin, double fromMax, double toMin, double toMax)
        => toMin + (value - fromMin) / (fromMax - fromMin) * (toMax - toMin);

    /// <summary>Linear interpolation.</summary>
    /// <param name="a">At t = 0.</param>
    /// <param name="b">At t = 1.</param>
    /// <param name="t">The fraction.</param>
    /// <returns>The interpolated value.</returns>
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Where a value sits between two others, clamped to [0, 1].</summary>
    /// <param name="value">The value.</param>
    /// <param name="min">Maps to 0.</param>
    /// <param name="max">Maps to 1.</param>
    /// <returns>The fraction.</returns>
    private static double InverseLerp(double value, double min, double max)
        => Math.Clamp((value - min) / (max - min), 0.0, 1.0);
}
