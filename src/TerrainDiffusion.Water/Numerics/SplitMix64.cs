namespace TerrainDiffusion.Water.Numerics;

/// <summary>
/// A small, fast, seedable random number generator (SplitMix64, Steele et al. 2014). Used only to lay out the
/// meander field from the world seed, so the same seed gives the same field on every platform.
/// </summary>
internal struct SplitMix64
{
    /// <summary>The golden-ratio increment of the state.</summary>
    private const ulong Increment = 0x9E37_79B9_7F4A_7C15UL;
    /// <summary>Current state.</summary>
    private ulong state;

    /// <summary>Starts the sequence at <paramref name="seed"/>.</summary>
    /// <param name="seed">Any 64-bit value.</param>
    public SplitMix64(ulong seed)
    {
        state = seed;
    }

    /// <summary>The next 64 random bits.</summary>
    /// <returns>A uniformly distributed 64-bit value.</returns>
    public ulong NextUInt64()
    {
        unchecked
        {
            ulong z = state += Increment;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>A uniform double in [0, 1) with 53 random bits.</summary>
    /// <returns>The value.</returns>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>A uniform double in [<paramref name="low"/>, <paramref name="high"/>).</summary>
    /// <param name="low">Lower bound.</param>
    /// <param name="high">Upper bound.</param>
    /// <returns>The value.</returns>
    public double NextDouble(double low, double high) => low + (high - low) * NextDouble();
}
