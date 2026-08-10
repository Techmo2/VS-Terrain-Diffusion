using System;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Portable RNG matching terrain_diffusion/inference/portable_rng.py and world_pipeline._tile_seed:
/// PCG64 (64-bit LCG + XSH-RR 64/32) with standard normals via the Marsaglia polar method.
/// Produces bit-identical streams to the Python and Java implementations.
/// </summary>
public static class PortableRng
{
    private const ulong Pcg64Mult = 6364136223846793005UL;
    private const ulong Pcg64Inc = 1442695040888963407UL;
    private const double Inv2P32 = 1.0 / 4294967296.0;

    /// <summary>Portable 64-bit seed from (baseSeed, ty, tx); matches world_pipeline._tile_seed.</summary>
    public static ulong TileSeed(ulong baseSeed, int ty, int tx)
    {
        ulong h = baseSeed * 0x9E3779B9UL;
        h += (uint)ty;
        h = h * 0x9E3779B9UL + (uint)tx;
        return h;
    }

    /// <summary>One PCG64 step; returns the new state and the 32-bit XSH-RR output.</summary>
    public static (ulong State, uint Output) Pcg64Next(ulong state)
    {
        state = state * Pcg64Mult + Pcg64Inc;
        uint x = (uint)(((state >> 18) ^ state) >> 27);
        int rot = (int)(state >> 59);
        uint out32 = (x >> rot) | (x << ((32 - rot) & 31));
        return (state, out32);
    }

    /// <summary>
    /// Fills out[offset .. offset+length) with standard normals using the Marsaglia polar method,
    /// matching portable_rng._fill_standard_normal_impl.
    /// </summary>
    public static void FillStandardNormal(ulong seed, float[] output, int offset, int length)
    {
        ulong state = seed;
        int i = 0;
        while (i < length)
        {
            var r1 = Pcg64Next(state);
            state = r1.State;
            var r2 = Pcg64Next(state);
            state = r2.State;

            double v1 = 2.0 * (r1.Output + 1.0) * Inv2P32 - 1.0;
            double v2 = 2.0 * (r2.Output + 1.0) * Inv2P32 - 1.0;
            double s = v1 * v1 + v2 * v2;
            if (s <= 0.0 || s >= 1.0) continue;

            double f = Math.Sqrt(-2.0 * Math.Log(s) / s);
            output[offset + i] = (float)(v1 * f);
            i++;
            if (i < length)
            {
                output[offset + i] = (float)(v2 * f);
                i++;
            }
        }
    }
}
