using System;
using System.IO;
using Newtonsoft.Json;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Generates the synthetic climate conditioning maps that steer the coarse model: Perlin noise
/// quantile-matched to real WorldClim/ETOPO distributions, matching
/// world_pipeline.py make_synthetic_map_factory.
/// </summary>
public sealed partial class SyntheticMapFactory
{
    /// <summary>
    /// Conditioning channels: elevation, temperature, its seasonality, precipitation, its seasonality.
    /// </summary>
    private const int Channels = 5;

    /// <summary>Conditioning channels the world's two global climate settings act on.</summary>
    private const int TemperatureChannel = 1;

    /// <summary>See <see cref="TemperatureChannel"/>.</summary>
    private const int PrecipitationChannel = 3;

    /// <summary>
    /// How much of the map's own climate noise survives inside a latitude band, as a fraction of
    /// the global spread.
    ///
    /// The synthetic map is drawn from the whole world's distribution at every pixel, so left
    /// undamped one row of it swings across forty degrees of mean annual temperature - the entire
    /// equator-to-pole range - and the band underneath would be invisible. Real land at one
    /// latitude spreads about 6 C either side of its zonal mean, against 12 for the distribution as
    /// a whole, which is where the temperature figure comes from; rainfall is damped less because
    /// its spread within a belt really is most of its spread overall - the Atacama and the Amazon
    /// are the same latitude.
    /// </summary>
    private const float TemperatureAnomaly = 0.5f;

    /// <summary>The same for precipitation, as a power on its ratio to the median.</summary>
    private const float PrecipitationAnomaly = 0.7f;

    /// <summary>
    /// Noise frequency, per coarse pixel, before each channel's multiplier from the pipeline config.
    /// </summary>
    private const float BaseFrequency = 0.05f;

    /// <summary>Fractal octaves per channel.</summary>
    private static readonly int[] Octaves = { 4, 2, 4, 4, 4 };

    /// <summary>Frequency step between octaves.</summary>
    private const float Lacunarity = 2.0f;

    /// <summary>Amplitude step between octaves.</summary>
    private const float Gain = 0.5f;

    /// <summary><c>pipeline_data.json</c>: the real-world distributions the noise is matched to.</summary>
    private sealed class PipelineData
    {
        /// <summary>Entries in each quantile table.</summary>
        [JsonProperty("n_quantiles")] public int NQuantiles;

        /// <summary>Real-world quantiles per channel, ascending.</summary>
        [JsonProperty("data_quantile_tables")] public float[][] DataQuantileTables;

        /// <summary>Slope of the temperature-seasonality baseline in temperature.</summary>
        [JsonProperty("a_temp_std")] public float ATempStd;

        /// <summary>Intercept of the temperature-seasonality baseline.</summary>
        [JsonProperty("b_temp_std")] public float BTempStd;

        /// <summary>First percentile of temperature seasonality.</summary>
        [JsonProperty("temp_std_p1")] public float TempStdP1;

        /// <summary>99th percentile of temperature seasonality.</summary>
        [JsonProperty("temp_std_p99")] public float TempStdP99;
    }

    /// <summary><c>pipeline_data.json</c>, once read; shared by every world.</summary>
    private static PipelineData _data;

    /// <summary>Serialises the first read of <see cref="_data"/>.</summary>
    private static readonly object DataGate = new();

    /// <summary>Each channel's noise distribution, as a quantile table.</summary>
    private readonly float[][] _noiseQuantiles = new float[Channels][];

    /// <summary><see cref="PipelineData.DataQuantileTables"/>.</summary>
    private readonly float[][] _dataQuantiles;

    /// <summary>One fractal Perlin noise per channel.</summary>
    private readonly FastNoiseLite[] _noises = new FastNoiseLite[Channels];

    /// <summary>
    /// Slope of the temperature-seasonality baseline in temperature; see <see cref="PipelineData.ATempStd"/>.
    /// </summary>
    private readonly float _aTempStd;

    /// <summary>Intercept of that baseline.</summary>
    private readonly float _bTempStd;

    /// <summary>First percentile of temperature seasonality.</summary>
    private readonly float _tempStdP1;

    /// <summary>99th percentile of temperature seasonality.</summary>
    private readonly float _tempStdP99;

    /// <summary>Where the sea goes, or null when the noise decides.</summary>
    private readonly ILandmaskSource _landmask;

    /// <summary>Where rivers lower the land, or null.</summary>
    private readonly IRiverBasinSource _riverBasins;

    /// <summary>
    /// How far a fully-river cell's land rank is pulled down towards sea level.
    ///
    /// Measured, not guessed (tools/RiverBasinResponse, 48x48 coarse cells, a 3-cell corridor, at
    /// the shipped landmaskNoiseLevel of 0.1): 0.5 takes a corridor from 466 m to 251 m against
    /// ~500 m either side - a 45% drop, with every cell still land. 1.0 takes it to 28 m, a 94%
    /// drop, but a fifth of the corridor ends up under water, which turns rivers into inlets.
    /// Binding the conditioning tighter than 0.1 is worth about five more points and costs more
    /// drowned cells, so the coastline setting is left alone.
    /// </summary>
    private readonly float _riverBasinDepth;

    /// <summary>How completely the landmask overrides the noise, 0 to 1.</summary>
    private readonly float _landmaskStrength;

    /// <summary>
    /// Extra basin depth per pixel from structure sites, already multiplied out (0 to 1), or null.
    /// The deeper of this and the river basin applies.
    /// </summary>
    private readonly Func<int, int, int, int, float[]> _siteBasins;

    /// <summary>Puts each row of the map in its latitude band, or null for an unbanded world.</summary>
    private readonly ILatitudeSource _latitude;

    /// <summary>
    /// Where the elevation quantile table crosses sea level, as a fractional table index. Splitting
    /// the table there gives two sub-distributions - sea floors below, land above - and the
    /// landmask picks between them per pixel.
    /// </summary>
    private readonly float _seaPosition;

    /// <summary>Rank warp carrying the world's temperature setting; 1 leaves the channel alone.</summary>
    private readonly float _temperatureExponent;

    /// <summary>Scale carrying the world's precipitation setting; 1 leaves the channel alone.</summary>
    private readonly float _precipitationScale;

    /// <summary>Builds the noise for each channel and settles how the world's settings bend it.</summary>
    /// <param name="worldSeed">64-bit world seed; per-channel seeds use the low 31 bits.</param>
    /// <param name="landmask">Decides where the sea goes, or null to let the noise decide.</param>
    /// <param name="landmaskStrength">How completely the landmask overrides the noise, 0 to 1.</param>
    /// <param name="climate">The world's global temperature and precipitation settings.</param>
    /// <param name="latitude">Bands the climate channels north to south, or null to leave them flat.</param>
    /// <param name="riverBasins">Lowers the land where a river network runs, or null for none.</param>
    /// <param name="riverBasinDepth">How far a fully-river cell is pulled towards sea level, 0 to 1.</param>
    /// <param name="siteBasins">Per-pixel basin depth from structure sites over (x1, y1, x2, y2), or null.</param>
    public SyntheticMapFactory(ulong worldSeed, ILandmaskSource landmask = null, float landmaskStrength = 1f,
                               ClimateShift climate = default, ILatitudeSource latitude = null,
                               IRiverBasinSource riverBasins = null, float riverBasinDepth = 0f,
                               Func<int, int, int, int, float[]> siteBasins = null)
    {
        _siteBasins = siteBasins;
        PipelineData data = LoadData();
        _dataQuantiles = data.DataQuantileTables;
        _aTempStd = data.ATempStd;
        _bTempStd = data.BTempStd;
        _tempStdP1 = data.TempStdP1;
        _tempStdP99 = data.TempStdP99;

        _landmask = landmaskStrength > 0f ? landmask : null;
        _landmaskStrength = Math.Clamp(landmaskStrength, 0f, 1f);
        _riverBasinDepth = Math.Clamp(riverBasinDepth, 0f, 1f);
        _riverBasins = _riverBasinDepth > 0f ? riverBasins : null;
        _seaPosition = FindSeaPosition(_dataQuantiles[0]);
        _latitude = latitude != null && !latitude.IsNeutral ? latitude : null;

        // A banded world carries its global climate settings in the bands themselves, one absolute
        // climate per row, so the whole-world warp that stands in for them otherwise is not wanted.
        ClimatePlan plan = _latitude != null ? ClimatePlan.Unconditioned(ClimateShift.None) : PlanClimate(climate);
        _temperatureExponent = plan.TemperatureExponent;
        _precipitationScale = plan.PrecipitationScale;

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

    /// <summary>Reads <c>pipeline_data.json</c> once.</summary>
    /// <exception cref="InvalidOperationException">The file is missing or malformed.</exception>
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

        // Null only when this world has no landmask at all, in which case the elevation channel
        // falls through to the same plain quantile lookup as the climate channels. A configured
        // landmask that comes back empty is a fault, not a lighter kind of conditioning.
        float[] sea = _landmask?.SeaFraction(x1, y1, x2, y2);
        if (_landmask != null && plane > 0 && (sea == null || sea.Length != plane))
        {
            throw DiffusionFailure.Fatal(
                $"The landmask returned {(sea == null ? "nothing" : sea.Length + " values")} for " +
                $"the {w}x{h} coarse window at ({x1}, {y1}), where {plane} were needed.");
        }

        float[] basins = _riverBasins?.BasinStrength(x1, y1, x2, y2);
        if (basins != null && basins.Length != plane)
        {
            throw DiffusionFailure.Fatal(
                $"The river basin map returned {basins.Length} values for the {w}x{h} coarse window " +
                $"at ({x1}, {y1}), where {plane} were needed.");
        }

        float[] depths = BasinDepths(basins, x1, y1, x2, y2, plane);

        // Rows run along Z, which is the axis latitude is measured on, so a band is one climate per
        // row. Resolved up front because the two banded channels want the same rows.
        float[] bandTemperature = null, bandPrecipitation = null;
        if (_latitude != null)
        {
            bandTemperature = new float[h];
            bandPrecipitation = new float[h];
            for (int r = 0; r < h; r++)
            {
                _latitude.BandAt(y1 + r, out bandTemperature[r], out bandPrecipitation[r]);
            }
        }

        for (int ch = 0; ch < Channels; ch++)
        {
            FastNoiseLite fnl = _noises[ch];
            float[] nq = _noiseQuantiles[ch];
            float[] dq = _dataQuantiles[ch];
            bool masked = ch == 0 && sea != null;
            float channelExponent = ch == TemperatureChannel ? _temperatureExponent : 1f;
            float channelScale = ch == PrecipitationChannel ? _precipitationScale : 1f;
            bool bandedTemperature = bandTemperature != null && ch == TemperatureChannel;
            bool bandedPrecipitation = bandPrecipitation != null && ch == PrecipitationChannel;
            float median = bandedTemperature || bandedPrecipitation ? SampleTable(dq, (dq.Length - 1) * 0.5f) : 0f;
            var channel = new float[plane];
            int k = 0;
            for (int r = 0; r < h; r++)
            {
                // A banded row is anchored, not shifted: the band says what the climate at this
                // latitude is and the noise becomes the region's departure from it, damped so that
                // the departure stays a regional anomaly instead of swamping the band it sits in.
                float band = bandedTemperature ? bandTemperature[r]
                    : bandedPrecipitation ? bandPrecipitation[r] : 0f;

                for (int c = 0; c < w; c++, k++)
                {
                    float noise = fnl.GetNoise(x1 + c, y1 + r);
                    if (masked)
                    {
                        float rank = ApplyLandmask(TablePosition(noise, nq), sea[k], dq.Length);
                        if (depths != null) rank = ApplyBasin(rank, depths[k], dq.Length);
                        channel[k] = SampleTable(dq, rank);
                    }
                    else if (ch == 0 && depths != null)
                    {
                        // No landmask, so the model picks its own coastline - but a river still
                        // wants low ground under it.
                        channel[k] = SampleTable(dq,
                            ApplyBasin(TablePosition(noise, nq), depths[k], dq.Length));
                    }
                    else if (bandedTemperature)
                        channel[k] = Math.Clamp(
                            band + (Interp(noise, nq, dq) - median) * TemperatureAnomaly,
                            dq[0], dq[dq.Length - 1]);
                    else if (bandedPrecipitation)
                        channel[k] = Math.Clamp(
                            band * (float)Math.Pow(Math.Max(1e-3f, Interp(noise, nq, dq)) / median,
                                                   PrecipitationAnomaly),
                            dq[0], dq[dq.Length - 1]);
                    else if (channelExponent != 1f)
                        channel[k] = SampleTable(dq, WarpRank(TablePosition(noise, nq), channelExponent, dq.Length));
                    else if (channelScale != 1f)
                        channel[k] = Math.Clamp(Interp(noise, nq, dq) * channelScale, dq[0], dq[dq.Length - 1]);
                    else
                        channel[k] = Interp(noise, nq, dq);
                }
            }
            raw[ch] = channel;
        }

        return Combine(raw, plane);
    }

    /// <summary>
    /// How far each pixel's land rank is pulled towards sea level: the deeper of the river basin
    /// and any structure site.
    /// </summary>
    /// <param name="basins">River basin strength per pixel, 0 to 1, or null.</param>
    /// <param name="x1">The window's first column.</param>
    /// <param name="y1">The window's first row.</param>
    /// <param name="x2">The column past the window.</param>
    /// <param name="y2">The row past the window.</param>
    /// <param name="plane">Pixels in the window.</param>
    /// <returns>Depths 0 to 1 per pixel, or null when nothing lowers this window.</returns>
    private float[] BasinDepths(float[] basins, int x1, int y1, int x2, int y2, int plane)
    {
        float[] depths = null;
        if (basins != null)
        {
            depths = new float[plane];
            for (int k = 0; k < plane; k++) depths[k] = Math.Clamp(basins[k], 0f, 1f) * _riverBasinDepth;
        }
        float[] sites = _siteBasins?.Invoke(x1, y1, x2, y2);
        if (sites != null)
        {
            depths ??= new float[plane];
            for (int k = 0; k < plane; k++) depths[k] = Math.Max(depths[k], Math.Clamp(sites[k], 0f, 1f));
        }
        return depths;
    }

    /// <summary>
    /// Turns the five quantile-matched channels into the conditioning the model reads, as the
    /// reference pipeline does: temperature lapsed to the synthetic elevation, temperature
    /// seasonality rescaled against the baseline that temperature implies, precipitation
    /// seasonality damped in wet places, and elevation square-root encoded.
    /// </summary>
    /// <param name="raw">The channels, elevation (m), temperature, its seasonality, precipitation and its seasonality.</param>
    /// <param name="plane">Pixels per channel.</param>
    /// <returns>Flat (5, H, W) array with channels [elevSqrt, temp, tempStd, precip, precipStd].</returns>
    private float[] Combine(float[][] raw, int plane)
    {
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

    /// <summary>
    /// Bends a rank in the elevation distribution towards the half of it the landmask asks for.
    ///
    /// A pixel the map calls sea is drawn from the sea-floor half of the distribution and one it
    /// calls land from the land half, each keeping its own shape - so shelves, abyssal plains,
    /// plains and mountains all still occur in their real-world proportions, only now on the side
    /// of the coastline the world was configured to put them.
    ///
    /// Part-sea pixels interpolate between the two, which lands them near sea level. That is the
    /// right answer for this channel even though it reads as a bias: what the model is being told
    /// is the pixel's *mean* elevation, and the mean of a 7.68 km cell with a coastline through it
    /// really is well below zero, because the sea floor on one side is far deeper than the land on
    /// the other is high. Splitting the warp at the pixel's own sea fraction instead - so that the
    /// share coming out under water is exactly the share asked for - was tried and is worse: it
    /// hands coastal cells the full range of land heights and abyssal depths, and the model turns
    /// them into whole mountains or whole trenches rather than a coast (86.0% of columns on the
    /// right side of the water against 88.2% for this).
    /// </summary>
    private float ApplyLandmask(float position, float seaFraction, int tableLength)
    {
        float span = tableLength - 1f;
        float u = position / span;
        float uSea = _seaPosition / span;
        float sea = Math.Clamp(seaFraction, 0f, 1f);

        float wanted = sea * (u * uSea) + (1f - sea) * (uSea + u * (1f - uSea));
        return (u + _landmaskStrength * (wanted - u)) * span;
    }

    /// <summary>
    /// Lowers a land rank towards sea level where a river system runs.
    ///
    /// Only ever pulls downwards, and only within the land half: a valley is low ground, not sea,
    /// and a river that came out under water would be no river at all. Ordering inside the land
    /// half is preserved, so a mountainous region with a river through it still reads as higher
    /// than a plain with one - it is the whole cell that settles, not the landscape that flattens.
    ///
    /// This is the only thing the model can be told about a river. One coarse pixel is 512 blocks
    /// at the default scale, so what arrives is "somewhere in this half kilometre there is a river
    /// system", and what comes back is ground low enough to have carried one.
    /// </summary>
    private float ApplyBasin(float position, float basin, int tableLength)
    {
        if (basin <= 0f) return position;

        float span = tableLength - 1f;
        float u = position / span;
        float uSea = _seaPosition / span;
        if (u <= uSea) return position;

        return (uSea + (u - uSea) * (1f - basin)) * span;
    }

    /// <summary>
    /// Bends a channel's whole distribution towards its hot, cold, wet or dry end.
    ///
    /// The warp is <c>u^k</c> on the rank, which is strictly increasing for any positive k: every
    /// pixel keeps its place in the order and only the values attached to those places move. That
    /// is what a shifted climate should be. Scaling the values instead would be simpler and wrong
    /// — four times a 25 C mean is 100 C, a reading with no counterpart on Earth and so none in
    /// the model's training data either, and the conditioning would clamp to its maximum
    /// everywhere and hand the model a world with no climate variation left in it at all.
    /// </summary>
    private static float WarpRank(float position, float exponent, int tableLength)
    {
        float span = tableLength - 1f;
        return (float)Math.Pow(position / span, exponent) * span;
    }

    /// <summary>Fractional index at which the elevation table passes through sea level.</summary>
    private static float FindSeaPosition(float[] elevationQuantiles)
    {
        for (int i = 1; i < elevationQuantiles.Length; i++)
        {
            float below = elevationQuantiles[i - 1], above = elevationQuantiles[i];
            if (below <= 0f && above > 0f) return i - 1 + -below / (above - below);
        }

        // A table that never crosses sea level cannot be split into a sea half and a land half.
        return (elevationQuantiles.Length - 1) * 0.5f;
    }
}
