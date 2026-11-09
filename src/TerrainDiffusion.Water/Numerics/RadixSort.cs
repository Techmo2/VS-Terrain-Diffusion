using System;

namespace TerrainDiffusion.Water.Numerics;

/// <summary>
/// Least-significant-digit radix sort of 64-bit keys, and the key encoding that sorts cells by height.
/// </summary>
/// <remarks>
/// The depression fill visits every cell of a window in height order, ties broken by cell index (what a stable
/// sort of the heights gives). Packing (height, index) into one unsigned key makes every key distinct, so any
/// correct sort gives that order; a radix sort of the packed keys takes a few linear passes instead of
/// O(n log n) comparisons.
/// </remarks>
internal static class RadixSort
{
    /// <summary>Bits per radix digit: 13 bits sort the 52-bit keys of a 768² window in four passes.</summary>
    private const int DigitBits = 13;
    /// <summary>Buckets per digit.</summary>
    private const int BucketCount = 1 << DigitBits;

    /// <summary>
    /// The unsigned key of <paramref name="height"/> whose integer order is the order of the heights
    /// (−0 and +0 are equal heights and get the same key).
    /// </summary>
    /// <param name="height">A finite height.</param>
    /// <returns>A 32-bit key.</returns>
    public static uint OrderedKey(float height)
    {
        if (height == 0.0f)
        {
            height = 0.0f;                        // fold −0 into +0
        }
        uint bits = BitConverter.SingleToUInt32Bits(height);
        return (bits & 0x8000_0000u) != 0 ? ~bits : bits | 0x8000_0000u;
    }

    /// <summary>Number of bits needed to hold every index below <paramref name="count"/>.</summary>
    /// <param name="count">Number of distinct indices.</param>
    /// <returns>Bit width, at least 1.</returns>
    public static int IndexBits(int count)
    {
        int bits = 1;
        while ((1L << bits) < count)
        {
            bits++;
        }
        return bits;
    }

    /// <summary>Sorts <paramref name="keys"/> ascending, looking at their lowest
    /// <paramref name="significantBits"/> bits only.</summary>
    /// <remarks>Every digit's histogram is counted in one pass over the keys first; a digit that is the same for
    /// every key needs no pass at all.</remarks>
    /// <param name="keys">Keys to sort in place.</param>
    /// <param name="buffer">Scratch of at least the same length.</param>
    /// <param name="significantBits">Bits that can be non-zero (1-64).</param>
    public static void Sort(Span<ulong> keys, Span<ulong> buffer, int significantBits)
    {
        if (buffer.Length < keys.Length)
        {
            throw new ArgumentException("The buffer must be at least as long as the keys.", nameof(buffer));
        }
        if (significantBits < 1 || significantBits > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(significantBits), significantBits, "1-64 bits.");
        }
        if (keys.Length < 2)
        {
            return;
        }
        int digits = (significantBits + DigitBits - 1) / DigitBits;
        int[] counts = new int[digits * BucketCount];
        foreach (ulong key in keys)
        {
            for (int d = 0; d < digits; d++)
            {
                counts[d * BucketCount + (int)((key >> (d * DigitBits)) & (BucketCount - 1))]++;
            }
        }

        Span<ulong> source = keys;
        Span<ulong> target = buffer[..keys.Length];
        for (int d = 0; d < digits; d++)
        {
            Span<int> offsets = counts.AsSpan(d * BucketCount, BucketCount);
            int shift = d * DigitBits;
            if (offsets[(int)((source[0] >> shift) & (BucketCount - 1))] == source.Length)
            {
                continue;                         // every key has this digit: already in order for it
            }
            int offset = 0;
            for (int bucket = 0; bucket < BucketCount; bucket++)
            {
                int count = offsets[bucket];
                offsets[bucket] = offset;
                offset += count;
            }
            for (int i = 0; i < source.Length; i++)
            {
                ulong key = source[i];
                target[offsets[(int)((key >> shift) & (BucketCount - 1))]++] = key;
            }
            Span<ulong> swap = source;
            source = target;
            target = swap;
        }
        if (source != keys)
        {
            source.CopyTo(keys);                  // the result ended in the buffer
        }
    }
}
