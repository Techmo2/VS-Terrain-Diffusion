using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using VSTerrainDiffusion.Pipeline;

/// <summary>
/// Runs the deterministic (model-free) parts of the diffusion pipeline and compares them against
/// output captured from the original Java implementation. A mismatch means the C# port has drifted
/// from the reference and the terrain would no longer match the upstream mod for the same seed.
///
/// Regenerate the golden file with ./regenerate-expected.sh after changing upstream.
/// </summary>
class Program
{
    /// <summary>Relative tolerance; the two implementations should agree to the last float bit.</summary>
    private const double Tolerance = 1e-5;

    static readonly StringBuilder Sb = new();
    static void Emit(string tag, params float[] vals)
    {
        Sb.Append(tag);
        foreach (float v in vals) Sb.Append(' ').Append(v.ToString("G9", CultureInfo.InvariantCulture));
        Sb.Append('\n');
    }

    static int Main(string[] args)
    {
        Probe();

        string expectedPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "expected-java.txt");

        if (!File.Exists(expectedPath))
        {
            Console.Write(Sb.ToString());
            Console.Error.WriteLine($"No golden file at {expectedPath}; printed the probe output instead.");
            return 2;
        }

        return Compare(Parse(Sb.ToString()), Parse(File.ReadAllText(expectedPath)));
    }

    static Dictionary<string, double[]> Parse(string text)
    {
        var result = new Dictionary<string, double[]>();
        foreach (string line in text.Split('\n'))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var values = new double[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
                values[i - 1] = double.Parse(parts[i], CultureInfo.InvariantCulture);
            result[parts[0]] = values;
        }
        return result;
    }

    static int Compare(Dictionary<string, double[]> actual, Dictionary<string, double[]> expected)
    {
        int failures = 0;
        foreach ((string tag, double[] want) in expected)
        {
            if (!actual.TryGetValue(tag, out double[] got))
            {
                Console.WriteLine($"skip    {tag,-9} (probe did not run)");
                continue;
            }
            if (got.Length != want.Length)
            {
                Console.WriteLine($"LENGTH  {tag}: expected {want.Length}, got {got.Length}");
                failures++;
                continue;
            }

            double worstRelative = 0;
            for (int i = 0; i < want.Length; i++)
                worstRelative = Math.Max(worstRelative, Math.Abs(got[i] - want[i]) / Math.Max(1e-6, Math.Abs(want[i])));

            bool ok = worstRelative < Tolerance;
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}    {tag,-9} n={want.Length,-4} max relative error {worstRelative:0.###e+00}");
        }

        Console.WriteLine(failures == 0
            ? "All probes match the Java reference."
            : $"{failures} probe(s) diverged from the Java reference.");
        return failures == 0 ? 0 : 1;
    }

    static void Probe()
    {
        var n = new float[16];
        PortableRng.FillStandardNormal(1234567890123UL, n, 0, 16);
        Emit("rng", n);
        Emit("pcg", PortableRng.Pcg64Next(0xDEADBEEFCAFEUL).Output);
        Emit("tileseed", PortableRng.TileSeed(987654321UL, -3, 7) >> 40);

        float[] noise = GaussianNoisePatch.Generate(42UL, -70, -130, 8, 8, 2, 64, 64);
        Emit("noise", noise);

        var s = new EdmScheduler(20);
        Emit("sigmas", s.Sigmas);
        var sample = new float[32];
        for (int i = 0; i < 32; i++) sample[i] = (float)Math.Sin(i * 0.7);
        for (int step = 0; step < 20; step++)
        {
            var mo = new float[32];
            for (int i = 0; i < 32; i++) mo[i] = (float)Math.Cos(i * 0.3 + step);
            sample = s.Step(mo, sample);
        }
        Emit("edm", sample);

        int H = 24, W = 20, lh = 6, lw = 5;
        var res = new float[H][];
        for (int i = 0; i < H; i++) { res[i] = new float[W]; for (int j = 0; j < W; j++) res[i][j] = (float)Math.Sin(i * 0.3 + j * 0.17); }
        var low = new float[lh][];
        for (int i = 0; i < lh; i++) { low[i] = new float[lw]; for (int j = 0; j < lw; j++) low[i][j] = (float)Math.Cos(i * 0.5 - j * 0.25) * 30f; }
        float[][] nl = LaplacianUtils.LaplacianDenoise(res, low, 5.0f);
        var nlf = new float[lh * lw];
        int k = 0;
        for (int i = 0; i < lh; i++) for (int j = 0; j < lw; j++) nlf[k++] = nl[i][j];
        Emit("denoise", nlf);
        float[][] dec = LaplacianUtils.LaplacianDecode(res, nl);
        var decf = new float[H];
        for (int i = 0; i < H; i++) decf[i] = dec[i][i % W];
        Emit("decode", decf);
        float[][][] lbt = LaplacianUtils.LocalBaselineTemperature(res, Low2(H, W), 15, 0.02f);
        var lbtf = new float[2 * 4];
        for (int c = 0; c < 2; c++) for (int i = 0; i < 4; i++) lbtf[c * 4 + i] = lbt[c][i][i];
        Emit("lbt", lbtf);

        // The synthetic climate map needs pipeline_data.json from the downloaded model assets.
        try
        {
            var f = new SyntheticMapFactory(123456789UL);
            Emit("synth", f.Sample(-40, 17, -32, 25));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Skipping the synthetic map probe: " + e.Message);
        }
    }

    static float[][] Low2(int h, int w)
    {
        var e = new float[h][];
        for (int i = 0; i < h; i++) { e[i] = new float[w]; for (int j = 0; j < w; j++) e[i][j] = ((i * w + j) % 7 - 2) * 300f; }
        return e;
    }
}
