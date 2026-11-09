namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Interpolation over quantile tables: a value's rank in one table, and the value at a rank in
/// another.
/// </summary>
public sealed partial class SyntheticMapFactory
{
    /// <summary>
    /// Linear interpolation matching numpy's np.interp, clamping at the table boundaries.
    ///
    /// Deliberately not written as <c>SampleTable(fp, TablePosition(x, xp))</c>, which is the same
    /// function: routing a rank back through a float would move a value by a part in a million at
    /// the top of an interval, and this path generates the terrain of every world that is not using
    /// a landmask, including ones that already exist.
    /// </summary>
    internal static float Interp(float x, float[] xp, float[] fp)
    {
        int n = xp.Length;
        if (x <= xp[0]) return fp[0];
        if (x >= xp[n - 1]) return fp[n - 1];

        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (xp[mid] <= x) lo = mid; else hi = mid;
        }
        float t = (x - xp[lo]) / (xp[hi] - xp[lo]);
        return fp[lo] + t * (fp[hi] - fp[lo]);
    }

    /// <summary>
    /// Where <paramref name="x"/> falls in <paramref name="xp"/>, as a fractional index. This is
    /// the noise value's rank in its own distribution, which is what makes the two quantile tables
    /// interchangeable: the same position read out of the data table is the matched value.
    /// </summary>
    internal static float TablePosition(float x, float[] xp)
    {
        int n = xp.Length;
        if (x <= xp[0]) return 0f;
        if (x >= xp[n - 1]) return n - 1f;

        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (xp[mid] <= x) lo = mid; else hi = mid;
        }
        return lo + (x - xp[lo]) / (xp[hi] - xp[lo]);
    }

    /// <summary>Reads a quantile table at a fractional index, clamping at both ends.</summary>
    internal static float SampleTable(float[] fp, float position)
    {
        int n = fp.Length;
        if (position <= 0f) return fp[0];
        if (position >= n - 1) return fp[n - 1];

        int lo = (int)position;
        float t = position - lo;
        return fp[lo] + t * (fp[lo + 1] - fp[lo]);
    }

    /// <summary>
    /// <see cref="Interp(float, float[], float[])"/> with the option of continuing the end segments
    /// past the table instead of flattening at them, which is what an inverted response needs: a
    /// band a little hotter than anything measured should ask for a little more conditioning, not
    /// for exactly the most that was measured.
    /// </summary>
    private static float Interp(float x, float[] xp, float[] fp, bool extrapolate)
    {
        int n = xp.Length;
        if (!extrapolate) return Interp(x, xp, fp);
        if (x < xp[0]) return fp[0] + (x - xp[0]) * (fp[1] - fp[0]) / (xp[1] - xp[0]);
        if (x > xp[n - 1])
            return fp[n - 1] + (x - xp[n - 1]) * (fp[n - 1] - fp[n - 2]) / (xp[n - 1] - xp[n - 2]);
        return Interp(x, xp, fp);
    }
}
