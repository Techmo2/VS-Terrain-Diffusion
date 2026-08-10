using System;
using System.IO;
using Newtonsoft.Json;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Generates the synthetic climate conditioning maps that steer the coarse model: Perlin noise
/// quantile-matched to real WorldClim/ETOPO distributions, matching
/// world_pipeline.py make_synthetic_map_factory.
/// </summary>
public sealed class SyntheticMapFactory
{
    private const int Channels = 5;
    private const float BaseFrequency = 0.05f;
    private static readonly int[] Octaves = { 4, 2, 4, 4, 4 };
    private const float Lacunarity = 2.0f;
    private const float Gain = 0.5f;

    private sealed class PipelineData
    {
        [JsonProperty("n_quantiles")] public int NQuantiles;
        [JsonProperty("data_quantile_tables")] public float[][] DataQuantileTables;
        [JsonProperty("a_temp_std")] public float ATempStd;
        [JsonProperty("b_temp_std")] public float BTempStd;
        [JsonProperty("temp_std_p1")] public float TempStdP1;
        [JsonProperty("temp_std_p99")] public float TempStdP99;
    }

    private static PipelineData _data;
    private static readonly object DataGate = new();

    private readonly float[][] _noiseQuantiles = new float[Channels][];
    private readonly float[][] _dataQuantiles;
    private readonly FastNoiseLite[] _noises = new FastNoiseLite[Channels];
    private readonly float _aTempStd, _bTempStd, _tempStdP1, _tempStdP99;

    /// <param name="worldSeed">64-bit world seed; per-channel seeds use the low 31 bits.</param>
    public SyntheticMapFactory(ulong worldSeed)
    {
        PipelineData data = LoadData();
        _dataQuantiles = data.DataQuantileTables;
        _aTempStd = data.ATempStd;
        _bTempStd = data.BTempStd;
        _tempStdP1 = data.TempStdP1;
        _tempStdP99 = data.TempStdP99;

        float[] frequencyMult = WorldPipelineModelConfig.Instance.FrequencyMult;
        for (int ch = 0; ch < Channels; ch++)
        {
            var fnl = new FastNoiseLite((int)((worldSeed + (ulong)ch + 1) & 0x7FFFFFFFUL));
            fnl.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
            fnl.SetFrequency(BaseFrequency * frequencyMult[ch]);
            fnl.SetFractalType(FastNoiseLite.FractalType.FBm);
            fnl.SetFractalOctaves(Octaves[ch]);
            fnl.SetFractalLacunarity(Lacunarity);
            fnl.SetFractalGain(Gain);
            _noises[ch] = fnl;
            _noiseQuantiles[ch] = BuildNoiseQuantiles(fnl, 64, 1e-4f);
        }
    }

    private static PipelineData LoadData()
    {
        if (_data != null) return _data;
        lock (DataGate)
        {
            if (_data != null) return _data;
            string path = ModelAssetManager.ResolveAssetPath("pipeline_data.json");
            try
            {
                var data = JsonConvert.DeserializeObject<PipelineData>(File.ReadAllText(path));
                if (data?.DataQuantileTables == null || data.DataQuantileTables.Length != Channels)
                    throw new InvalidDataException("data_quantile_tables must contain 5 channels");
                _data = data;
                return _data;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Failed to load pipeline_data.json from " + path, e);
            }
        }
    }

    /// <summary>
    /// Quantile table for a noise instance, sampled on a 1024x1024 grid at stride 32 to match
    /// Python's _compute_map_stats.
    /// </summary>
    internal static float[] BuildNoiseQuantiles(FastNoiseLite fnl, int nQuantiles, float eps)
    {
        var values = new float[1024 * 1024];
        int k = 0;
        for (int r = 0; r < 1024; r++)
            for (int c = 0; c < 1024; c++)
                values[k++] = fnl.GetNoise(c * 32, r * 32);
        Array.Sort(values);

        var q = new float[nQuantiles];
        int n = values.Length;
        for (int i = 0; i < nQuantiles; i++)
        {
            float pct = eps + i * (1.0f - 2 * eps) / (nQuantiles - 1);
            float idx = pct * (n - 1);
            int lo = (int)idx;
            int hi = Math.Min(lo + 1, n - 1);
            q[i] = values[lo] + (idx - lo) * (values[hi] - values[lo]);
        }

        // Force a strictly increasing table, matching Python's build_quantiles.
        float minDiff = float.MaxValue;
        for (int i = 1; i < nQuantiles; i++)
            if (q[i] > q[i - 1]) minDiff = Math.Min(minDiff, q[i] - q[i - 1]);
        if (minDiff == float.MaxValue) minDiff = 1e-10f;
        for (int i = 1; i < nQuantiles; i++)
            if (q[i] <= q[i - 1]) q[i] = q[i - 1] + minDiff * 0.1f;

        return q;
    }

    /// <summary>
    /// Samples the synthetic map over the half-open window [x1, x2) x [y1, y2).
    /// </summary>
    /// <returns>
    /// Flat (5, H, W) array with channels [elevSqrt, temp, tempStd, precip, precipStd].
    /// </returns>
    public float[] Sample(int x1, int y1, int x2, int y2)
    {
        int h = y2 - y1;
        int w = x2 - x1;
        int plane = h * w;
        var raw = new float[Channels][];

        for (int ch = 0; ch < Channels; ch++)
        {
            FastNoiseLite fnl = _noises[ch];
            float[] nq = _noiseQuantiles[ch];
            float[] dq = _dataQuantiles[ch];
            var channel = new float[plane];
            int k = 0;
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    channel[k++] = Interp(fnl.GetNoise(x1 + c, y1 + r), nq, dq);
            raw[ch] = channel;
        }

        var result = new float[Channels * plane];
        for (int idx = 0; idx < plane; idx++)
        {
            float elev = raw[0][idx];
            float temp = raw[1][idx];
            float tempStd = raw[2][idx];
            float precip = raw[3][idx];
            float precipStd = raw[4][idx];

            // Lapse-rate correction of temperature for the synthetic elevation.
            float lapseRate = -6.5f + 0.0015f * precip;
            lapseRate = Math.Clamp(lapseRate, -9.8f, -4.0f) / 1000.0f;
            temp = Math.Clamp(temp + lapseRate * Math.Max(0.0f, elev), -10.0f, 40.0f);

            float baseline = _aTempStd * temp + _bTempStd;
            float t01 = (tempStd - _tempStdP1) / (_tempStdP99 - _tempStdP1);
            float baselineClipped = Math.Max(_tempStdP1, -baseline);
            tempStd = t01 * (_tempStdP99 - baselineClipped) + baselineClipped + baseline;
            tempStd = Math.Max(tempStd, 20.0f);

            precipStd *= Math.Max(0.0f, (185.0f - 0.04111f * precip) / 185.0f);

            float elevSqrt = (float)(Math.Sign(elev) * Math.Sqrt(Math.Abs(elev)));

            result[idx] = elevSqrt;
            result[plane + idx] = temp;
            result[2 * plane + idx] = tempStd;
            result[3 * plane + idx] = precip;
            result[4 * plane + idx] = precipStd;
        }
        return result;
    }

    /// <summary>Linear interpolation matching numpy's np.interp, clamping at the table boundaries.</summary>
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
}
