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
public sealed class SyntheticMapFactory
{
    private const int Channels = 5;

    /// <summary>Conditioning channels the world's two global climate settings act on.</summary>
    private const int TemperatureChannel = 1;

    private const int PrecipitationChannel = 3;

    /// <summary>
    /// Bounds on the rank warp the climate settings are allowed to apply. Five squeezes the world
    /// into roughly the driest or coldest twentieth of the real distribution and a fifth into the
    /// wettest or hottest sixth, which is as far as a warp can go before it stops being a warp:
    /// past that it flattens the channel to one value and the model loses the variation that makes
    /// a climate a climate.
    /// </summary>
    private const float MinRankExponent = 0.2f;

    private const float MaxRankExponent = 5f;

    /// <summary>
    /// How hard the model leans on a climate it is conditioned towards, as an exponent: shift the
    /// conditioning by a factor r and the world comes out shifted by about r to this power. The
    /// model amplifies rather than follows — a fifth of the rain in the conditioning is a seventh
    /// of the rain on the ground — so a setting handed straight to it arrives overdone, and these
    /// are what the request is divided by on the way in.
    ///
    /// Measured over 48x48 coarse cells on two seeds, comparing the median of the model's own land
    /// against the median it was conditioned towards, over the full range of both settings. With
    /// these values every setting the game offers lands within 2 C of what it asked for, and
    /// within 10% for rainfall in a region of ordinary wetness. Two places do worse and both do so
    /// by running out of world rather than by arithmetic: "Hot" comes out about 5 C over because
    /// the model has no hotter climate left to draw, and an already very wet or very dry region
    /// moves perhaps half as far as asked, being near the end of what the model will draw there.
    /// </summary>
    private const float TemperatureResponse = 1.25f;

    private const float PrecipitationResponse = 1.15f;

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

    private const float PrecipitationAnomaly = 0.7f;

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

    private readonly ILandmaskSource _landmask;
    private readonly float _landmaskStrength;

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

    /// <param name="worldSeed">64-bit world seed; per-channel seeds use the low 31 bits.</param>
    /// <param name="landmask">Decides where the sea goes, or null to let the noise decide.</param>
    /// <param name="landmaskStrength">How completely the landmask overrides the noise, 0 to 1.</param>
    /// <param name="climate">The world's global temperature and precipitation settings.</param>
    /// <param name="latitude">Bands the climate channels north to south, or null to leave them flat.</param>
    public SyntheticMapFactory(ulong worldSeed, ILandmaskSource landmask = null, float landmaskStrength = 1f,
                               ClimateShift climate = default, ILatitudeSource latitude = null)
    {
        PipelineData data = LoadData();
        _dataQuantiles = data.DataQuantileTables;
        _aTempStd = data.ATempStd;
        _bTempStd = data.BTempStd;
        _tempStdP1 = data.TempStdP1;
        _tempStdP99 = data.TempStdP99;

        _landmask = landmaskStrength > 0f ? landmask : null;
        _landmaskStrength = Math.Clamp(landmaskStrength, 0f, 1f);
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

        // Null when there is no landmask, or none to be had for this window; the elevation channel
        // then falls through to the same plain quantile lookup as the climate channels.
        float[] sea = _landmask?.SeaFraction(x1, y1, x2, y2);

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
                        channel[k] = SampleTable(dq, ApplyLandmask(TablePosition(noise, nq), sea[k], dq.Length));
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

    /// <summary>
    /// Works out how much of a world's climate settings the model can be asked for and how much
    /// has to be applied to what it produces.
    ///
    /// The model is asked for a conditioning shift of <c>m^(1/response)</c>, which is the shift
    /// that comes back out as <c>m</c>; then the anchor is clamped to what the real climate
    /// distribution can express, and the correction carries whatever that clamp cost. Most
    /// settings need no correction at all. The ones that do are the hot end of the temperature
    /// scale, where there is nothing left to ask for: the world's warmest mean annual temperature
    /// is about 30 C, so "very hot" and "scorching hot" both draw the hottest climate on Earth and
    /// then have the difference between that and what was asked for scaled onto the result — which
    /// is what vanilla does too, its own climate byte having saturated at 40 C long before.
    /// </summary>
    /// <returns>
    /// The division of labour, or <see cref="ClimatePlan.Unconditioned"/> if the model's data is
    /// not on disk yet — on a fresh install the world is read once before anything is downloaded,
    /// and a world that cannot consult the model still has to honour its settings.
    /// </returns>
    public static ClimatePlan PlanClimate(ClimateShift climate)
    {
        float strength = DiffusionConfig.Instance.WorldGen.GlobalClimateStrength;
        if (strength <= 0f || climate.IsNeutral) return ClimatePlan.Unconditioned(climate);

        PipelineData data;
        try
        {
            data = LoadData();
        }
        catch (InvalidOperationException)
        {
            return ClimatePlan.Unconditioned(climate);
        }

        (float exponent, float temperatureDelivered) = PlanTemperature(
            data.DataQuantileTables[TemperatureChannel], climate.Temperature, strength);

        // Rainfall needs no clamping and so no correction: the conditioning is scaled outright and
        // the model returns the scale it was given, raised to its response.
        float scale = Asked(climate.Precipitation, strength, PrecipitationResponse);
        float precipitationDelivered = (float)Math.Pow(scale, PrecipitationResponse);

        return new ClimatePlan(
            exponent, scale,
            climate.Temperature / temperatureDelivered,
            climate.Precipitation / precipitationDelivered);
    }

    /// <summary>
    /// The conditioning shift to ask for, being the one that comes back out as the shift the world
    /// wants — or a fraction of it, where the conditioning has been turned down.
    /// </summary>
    private static float Asked(float multiplier, float strength, float response)
        => (float)Math.Pow(Math.Max(1e-4f, multiplier), strength / response);

    /// <summary>
    /// What the model actually puts on the ground when it is conditioned on a given climate,
    /// measured rather than derived.
    ///
    /// Two things separate the two numbers. The land the model draws stands above sea level, and
    /// its own lapse rate cools it; and the model amplifies, leaning about 16% further from its
    /// neutral climate than the conditioning asked. Both are steady enough to invert: the slope
    /// held to within 3% across three seeds, and it is the slope that decides whether a latitude
    /// gradient survives the model at all.
    ///
    /// The offset is not steady, and deliberately not corrected for. It ran from -5 C to -9 C
    /// between the three seeds, because it is mostly the relief of whichever continents that seed
    /// drew, and a world of high plateaus really is colder than a world of coastal plains - the
    /// same reason Siberia is colder than Ireland. These are the mean over the three, so an
    /// ordinary world lands on its bands and a mountainous one comes out a few degrees under.
    ///
    /// Measured over a 64x64 coarse box (about 250 km square at the default resolution) on seeds
    /// 1234, 777 and 424242, taking the median of every land pixel, with the whole map conditioned
    /// on one climate at a time.
    /// </summary>
    private static readonly float[] ResponseConditioningC = { -7f, -2f, 2f, 6f, 10f, 14f, 18f, 22f, 26f, 30f };

    private static readonly float[] ResponseLandC =
        { -14.33f, -11.40f, -7.63f, -3.03f, 1.87f, 6.77f, 11.57f, 16.23f, 20.70f, 24.87f };

    /// <summary>
    /// The same for rainfall, which the model damps rather than amplifies: land comes out at about
    /// two thirds of the rain the conditioning carries, near enough proportionally that the curve is
    /// straight in the log of both.
    /// </summary>
    private static readonly float[] ResponseConditioningMm = { 250f, 400f, 600f, 900f, 1400f, 2200f };

    private static readonly float[] ResponseLandMm = { 166f, 286f, 439f, 679f, 1073f, 1681f };

    /// <summary>
    /// How much wetter a banded world comes out than the curve above predicts.
    ///
    /// The curve was measured with one climate over the whole map. A banded world is not that: its
    /// conditioning runs from a rainforest to an ice cap down one meridian, and the model returns
    /// about a third more rain across it than the flat case does - most of it at the cold end,
    /// where it declines to dry out a polar belt as far as the zonal means say it should. Measured
    /// as the median of world-against-band over three full equator-to-pole transects (1.21, 1.35,
    /// 1.47), and applied to the curve in both directions so that the ask comes down and the answer
    /// lands on the band.
    /// </summary>
    private const float BandedRainfallBias = 1.34f;

    /// <summary>The rainfall curve in the log of both axes, with the banded bias folded in.</summary>
    private static readonly float[] LogResponseConditioningMm = Log(ResponseConditioningMm, 1f);

    private static readonly float[] LogResponseLandMm = Log(ResponseLandMm, BandedRainfallBias);

    private static float[] Log(float[] values, float bias)
    {
        var result = new float[values.Length];
        for (int i = 0; i < values.Length; i++) result[i] = (float)Math.Log(values[i] * bias);
        return result;
    }

    /// <summary>
    /// Works out how one latitude's climate is divided between the model and its output.
    ///
    /// Most of it the model can simply be asked for, once the ask is put through the inverse of its
    /// measured response. What it cannot be asked for is a climate it has never seen: mean annual
    /// temperature in the model's world tops out at 35.6 C and bottoms at -7.5 C, which after the
    /// response is land between about -14 C and 31 C. From roughly 80 degrees poleward the band
    /// wants an ice cap colder than that, so the conditioning is pinned to the coldest climate there
    /// is and the last few degrees are an offset on the output. That is the same bargain the hot end
    /// of <c>globalTemperature</c> strikes, and the same one vanilla strikes when its climate byte
    /// saturates.
    ///
    /// Called once per entry of <see cref="LatitudeBands"/>' table rather than per pixel, which is
    /// why it can afford to search the quantile tables.
    /// </summary>
    /// <param name="wantedC">Mean annual temperature the band wants on the ground, in Celsius.</param>
    /// <param name="wantedMm">Annual rainfall the band wants on the ground, in millimetres.</param>
    /// <exception cref="InvalidOperationException">The model's data is not on disk.</exception>
    public static LatitudeBandPlan PlanLatitude(float wantedC, float wantedMm)
    {
        PipelineData data = LoadData();
        float[] temperature = data.DataQuantileTables[TemperatureChannel];
        float[] rainfall = data.DataQuantileTables[PrecipitationChannel];

        float conditioningC = Math.Clamp(
            Interp(wantedC, ResponseLandC, ResponseConditioningC, true),
            temperature[0], temperature[temperature.Length - 1]);
        float conditioningMm = Math.Clamp(
            (float)Math.Exp(Interp((float)Math.Log(Math.Max(1f, wantedMm)), LogResponseLandMm,
                                   LogResponseConditioningMm, true)),
            rainfall[0], rainfall[rainfall.Length - 1]);

        float deliveredC = Interp(conditioningC, ResponseConditioningC, ResponseLandC, true);
        float deliveredMm = (float)Math.Exp(
            Interp((float)Math.Log(Math.Max(1f, conditioningMm)), LogResponseConditioningMm,
                   LogResponseLandMm, true));

        return new LatitudeBandPlan(
            conditioningC, conditioningMm,
            wantedC - deliveredC,
            deliveredMm > 0f ? wantedMm / deliveredMm : 1f);
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

    /// <summary>The climate the model draws when nothing has told it otherwise.</summary>
    public static (float TemperatureC, float PrecipitationMm) NeutralClimate()
    {
        PipelineData data = LoadData();
        float[] temperature = data.DataQuantileTables[TemperatureChannel];
        float[] rainfall = data.DataQuantileTables[PrecipitationChannel];
        return (SampleTable(temperature, (temperature.Length - 1) * 0.5f),
                SampleTable(rainfall, (rainfall.Length - 1) * 0.5f));
    }

    /// <summary>
    /// The temperature warp, and the shift the world will actually come out with because of it.
    ///
    /// Temperature is warped by rank where rainfall is scaled outright, because the two
    /// distributions are shaped nothing alike. Annual rainfall runs from nothing to six metres and
    /// scaling it lands inside that range whatever the setting asks for. Mean annual temperature
    /// occupies about forty degrees, from -7 C to 36 C, with no room above: scaling it by anything
    /// over 1.1 walks straight off the end of the world's climate and takes every pixel with it,
    /// where a rank warp merely moves the world towards its hot end and stops when it gets there.
    /// </summary>
    private static (float Exponent, float Delivered) PlanTemperature(
        float[] table, float multiplier, float strength)
    {
        // Temperature scales from -20 C, the bottom of the scale Vintage Story multiplies on.
        const float origin = ClimateShift.ScaleFloorC;

        float span = table.Length - 1f;
        float median = SampleTable(table, span * 0.5f);
        float asked = Asked(multiplier, strength, TemperatureResponse);
        float wanted = origin + (median - origin) * asked;
        if (Math.Abs(wanted - median) < 1e-3f) return (1f, 1f);

        // Clamped off both ends of the table: a rank of exactly 0 has no logarithm, and one of
        // exactly 1 has no warp that reaches it. The exponent bounds then decide how far the warp
        // is allowed to go before it stops being a warp and starts being a flat field.
        float rank = Math.Clamp(TablePosition(wanted, table) / span, 1e-3f, 1f - 1e-3f);
        float exponent = Math.Clamp(
            (float)(Math.Log(rank) / Math.Log(0.5)), MinRankExponent, MaxRankExponent);

        float reached = SampleTable(table, span * (float)Math.Pow(0.5, exponent));
        float delivered = (float)Math.Pow((reached - origin) / (median - origin), TemperatureResponse);
        return (exponent, delivered);
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
