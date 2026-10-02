using System;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// A world's vertical layout: where sea level is, and how much room there is above and below it.
/// Everything that means "so high above the sea" goes through here, so sea level can sit wherever
/// the world wants it and the climate, soil, trees and weather above it stay where they were.
///
/// Vanilla puts the sea at 22/51 of the world height and writes much of its world generation
/// data as fractions of that height: a soil band "from 0.66", a tree "up to 0.8". With the sea
/// moved, those fractions no longer land where they were written for. The frame answers that with
/// an equivalent vanilla world - the height a vanilla world would need to have this many blocks
/// above its sea - and places a fraction where it would fall in that world: the same blocks above
/// sea level, or below it the same share of the way down.
/// </summary>
public readonly struct HeightFrame
{
    /// <summary>Vanilla's sea level as a fraction of world height, 110/255.</summary>
    public const double VanillaSeaFraction = 0.4313725490196078;

    public int SeaLevel { get; }

    public int MapSizeY { get; }

    public HeightFrame(int seaLevel, int mapSizeY)
    {
        SeaLevel = seaLevel;
        MapSizeY = mapSizeY;
    }

    /// <summary>Vanilla's sea level for a world this tall.</summary>
    public static int VanillaSeaLevel(int mapSizeY) => (int)(VanillaSeaFraction * mapSizeY);

    /// <summary>Blocks from sea level to the world ceiling.</summary>
    public int BlocksAboveSea => MapSizeY - SeaLevel;

    /// <summary>Blocks from bedrock to sea level.</summary>
    public int BlocksBelowSea => SeaLevel;

    /// <summary>Height relative to sea level: 0 at the water surface's top, negative below.</summary>
    public int Offset(int y) => y - SeaLevel;

    /// <summary>The block Y at an offset from sea level.</summary>
    public int AtOffset(int offset) => SeaLevel + offset;

    /// <summary>
    /// The world height a vanilla world would need for this many blocks above its sea. With the
    /// sea where vanilla puts it, that is this world, exactly - vanilla rounds its sea level down
    /// to a whole block, and working it back out would move everything by a fraction of one.
    /// </summary>
    public double EquivalentMapSizeY => IsVanilla ? MapSizeY : BlocksAboveSea / (1.0 - VanillaSeaFraction);

    /// <summary>Sea level in that equivalent vanilla world.</summary>
    public double EquivalentSeaLevel => EquivalentMapSizeY - BlocksAboveSea;

    /// <summary>True when the sea is where vanilla would put it, so vanilla's fractions need no help.</summary>
    public bool IsVanilla => SeaLevel == VanillaSeaLevel(MapSizeY);

    /// <summary>
    /// Where a height vanilla gives as a fraction of world height falls in this world, as a block
    /// Y: the same blocks above sea as in the equivalent vanilla world, or below the sea the same
    /// fraction of its depth. <paramref name="rescaleAboveSea"/> can move the blocks above sea
    /// further, for a world whose vertical scale differs from vanilla's.
    /// </summary>
    public double VanillaFractionToY(double fraction, Func<double, double> rescaleAboveSea = null)
    {
        double y = fraction * EquivalentMapSizeY;
        double seaLevel = EquivalentSeaLevel;
        if (y < seaLevel) return seaLevel > 0 ? y / seaLevel * SeaLevel : 0;

        double above = y - seaLevel;
        return SeaLevel + (rescaleAboveSea?.Invoke(above) ?? above);
    }

    /// <summary><see cref="VanillaFractionToY"/> expressed as a fraction of this world's height, for data vanilla compares with <c>y / MapSizeY</c>.</summary>
    public float VanillaFractionToFraction(float fraction, Func<double, double> rescaleAboveSea = null)
    {
        // The ends mean "everywhere" and have to stay that way.
        if (fraction <= 0f || fraction >= 1f) return fraction;
        return (float)Math.Clamp(VanillaFractionToY(fraction, rescaleAboveSea) / MapSizeY, 0.0, 1.0);
    }

    public override string ToString() =>
        $"sea level {SeaLevel} of {MapSizeY} ({BlocksAboveSea} above, like a vanilla world {EquivalentMapSizeY:0} tall)";
}
