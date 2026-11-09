using System;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// The metre-to-block mapping: headroom, the linear range and its compression above, shore detail,
/// and the calibration that can fit the gain to a region's peaks.
/// </summary>
public sealed partial class DiffusionWorldSettings
{
    /// <summary>The tallest elevation the model is expected to produce, in metres.</summary>
    private const float ModelMaxElevationMeters = 10000f;

    /// <summary>Deepest ocean the compression curve is calibrated against, in metres.</summary>
    private const float ModelMaxDepthMeters = 10000f;

    /// <summary>Blocks available between sea level and the world ceiling.</summary>
    public int HeadroomBlocks { get; private set; }

    /// <summary>Elevation, in metres, below which the mapping is perfectly linear.</summary>
    public float LinearRangeMeters { get; private set; }

    /// <summary>True when the vertical gain came from measuring this world's terrain.</summary>
    public bool IsCalibrated { get; private set; }

    /// <summary>The measured peak elevation the gain was fitted to, in metres. Zero if uncalibrated.</summary>
    public float CalibratedPeakMeters { get; private set; }

    /// <summary>
    /// True when calibration wanted more vertical gain than <c>maxAutoExaggeration</c> allows, so
    /// the region's peaks will fall short of the ceiling.
    /// </summary>
    public bool CalibrationClamped { get; private set; }

    /// <summary>Total height multiplier relative to true real-world scale.</summary>
    public float EffectiveExaggeration => _blocksPerMeter * MetersPerBlock;

    /// <summary>
    /// Metres of elevation per block of height away from the shore, the inverse of the vertical
    /// gain. At the shore the rows follow <see cref="ShoreDetail"/> instead.
    /// </summary>
    public float MetersPerBlockVertical => _blocksPerMeter > 0f ? 1f / _blocksPerMeter : 0f;

    /// <summary>
    /// Extra block rows per unit of the model's own height, the signed square root of metres, at
    /// the waterline. Zero is the plain metre mapping. Recorded in each world when it is created
    /// (see <see cref="ApplyShoreDetail"/>), so chunks generated before and after a config change
    /// agree.
    /// </summary>
    public float ShoreDetail { get; private set; }

    /// <summary>
    /// How far, in the model's square-root-of-metres units, <see cref="ShoreDetail"/> takes to fall
    /// by a factor of e. At 3 that is 9 m from the waterline, and it has all but gone by 60 m.
    /// </summary>
    public float ShoreFade { get; private set; } = 3f;

    /// <summary>
    /// Metres per block averaged from the waterline to the ceiling: the one per-block figure that
    /// is exact at both ends, for the parts of the game that can only apply a single lapse rate.
    /// </summary>
    public float MeanMetersPerBlockVertical =>
        HeadroomBlocks > 0 && _blocksPerMeter > 0f
            ? Unlift(HeadroomBlocks / _blocksPerMeter) / HeadroomBlocks
            : MetersPerBlockVertical;

    /// <summary>Vertical gain before <see cref="VerticalExaggeration"/> is applied, in blocks per metre.</summary>
    private float _baseBlocksPerMeter;

    /// <summary>
    /// Height above the waterline, in blocks, where the linear mapping hands over to the compression curve;
    /// the headroom itself when nothing needs compressing.
    /// </summary>
    private float _kneeBlocks;

    /// <summary>Vertical gain in blocks per metre, <see cref="VerticalExaggeration"/> included.</summary>
    private float _blocksPerMeter;

    /// <summary>
    /// Blocks per unit of <see cref="DepthCurve"/>, sized so the deepest ocean uses the config's share of the
    /// room below sea level.
    /// </summary>
    private float _oceanScale;

    /// <summary>True when the world wants its terrain height measured before generating anything.</summary>
    public bool WantsCalibration => _shaping.HeightMode == "auto" && !IsCalibrated;

    /// <summary>
    /// True when a block is exactly as tall as it is wide — the Minecraft mod's geometry, and the
    /// case in which vanilla's altitude-keyed surface rules already line up with the terrain.
    /// </summary>
    public bool IsIsotropic => Math.Abs(EffectiveExaggeration - 1f) < 0.001f && ShoreDetail == 0f;

    /// <summary>Half-width in blocks of the area calibration should survey.</summary>
    public int CalibrationRadiusBlocks => _shaping.CalibrationRadiusBlocks;

    /// <summary>Full-detail probes calibration runs on the tallest surveyed cells.</summary>
    public int CalibrationProbes => _shaping.CalibrationProbes;

    /// <summary>The elevation quantile calibration treats as the region's tall peaks.</summary>
    public float PeakQuantile => _shaping.PeakQuantile;

    /// <summary>
    /// Ratio of true peak height to the coarse survey's, assumed when probing is off or fails.
    /// </summary>
    public float ReliefFactor => _shaping.ReliefFactor;

    /// <summary>
    /// Sets <see cref="ShoreDetail"/> and <see cref="ShoreFade"/> and recomputes the mapping. Must run
    /// before the first tile is built, like <see cref="ApplyCalibration"/>.
    /// </summary>
    public void ApplyShoreDetail(float detail, float fade)
    {
        ShoreDetail = Math.Clamp(float.IsFinite(detail) ? detail : 0f, 0f, 8f);
        ShoreFade = Math.Clamp(float.IsFinite(fade) ? fade : 3f, 0.5f, 20f);
        RecomputeMapping();
    }

    /// <summary>
    /// Fits the vertical gain so that terrain <paramref name="peakMeters"/> tall reaches
    /// <c>targetPeakFillFraction</c> of the way from sea level to the world ceiling. Everything
    /// derived from the mapping is recomputed, so this must run before the first tile is built.
    /// </summary>
    /// <param name="peakMeters">The surveyed peak elevation, m; ignored unless finite and above 1.</param>
    public void ApplyCalibration(float peakMeters)
    {
        if (peakMeters <= 1f || float.IsNaN(peakMeters) || float.IsInfinity(peakMeters)) return;

        CalibratedPeakMeters = peakMeters;
        IsCalibrated = true;
        RecomputeMapping();
    }

    /// <summary>
    /// Derives the headroom, gain, knee and ocean scale from the current settings, calibration and shore
    /// detail.
    /// </summary>
    private void RecomputeMapping()
    {
        // Two blocks of safety below the world ceiling: the topmost layer must stay air.
        HeadroomBlocks = Math.Max(8, MapSizeY - 3 - SeaLevel);

        float trueScaleBlocksPerMeter = 1f / MetersPerBlock;

        if (IsCalibrated)
        {
            float targetBlocks = _shaping.TargetPeakFillFraction * HeadroomBlocks;
            float wanted = targetBlocks / Lift(CalibratedPeakMeters) * MetersPerBlock;
            float allowed = Math.Clamp(wanted, _shaping.MinAutoExaggeration, _shaping.MaxAutoExaggeration);
            CalibrationClamped = allowed < wanted;
            _baseBlocksPerMeter = allowed * trueScaleBlocksPerMeter;
        }
        else if (_shaping.MetersPerBlockVertical > 0f && _shaping.HeightMode == "manual")
        {
            _baseBlocksPerMeter = 1f / _shaping.MetersPerBlockVertical;
            CalibrationClamped = false;
        }
        else
        {
            _baseBlocksPerMeter = trueScaleBlocksPerMeter;
            CalibrationClamped = false;
        }

        _blocksPerMeter = VerticalExaggeration * _baseBlocksPerMeter;

        float naturalPeakBlocks = LandBlocks(ModelMaxElevationMeters);
        if (naturalPeakBlocks <= HeadroomBlocks)
        {
            // Everything the model can produce already fits; no compression needed.
            _kneeBlocks = HeadroomBlocks;
            LinearRangeMeters = ModelMaxElevationMeters;
        }
        else
        {
            _kneeBlocks = HeadroomBlocks * _shaping.LinearKneeFraction;
            LinearRangeMeters = Unlift(_kneeBlocks / _blocksPerMeter);
        }

        // Measured from the curve's own value at the shore, not from zero, because that is where
        // the sea floor now starts: scaling the whole curve would scale its constant term too, and
        // that term is meant to be one block of water whatever the world's height.
        // The shore term adds up to ShoreBlocks to every depth, which comes out of the same budget.
        _oceanScale = Math.Max(1f, _shaping.OceanDepthFraction * Math.Max(8, SeaLevel - 4) - ShoreBlocks)
                      / (DepthCurve(ModelMaxDepthMeters) - DepthCurve(0f));
    }

    /// <summary>
    /// Height of land above the waterline, in blocks and before the knee, for an elevation in metres.
    ///
    /// Linear, as it always was, plus <see cref="ShoreRows"/> at the shore. The model works in the
    /// square root of elevation and squares it on the way out, so a coast it draws as an even ramp
    /// comes out in metres as a curve that is nearly flat for its first few metres and then climbs
    /// steeply: a wide, dead-flat beach with a lip behind it, whatever the metres-per-block. Rows
    /// taken from the model's own units undo that at the shore, and fade out before larger
    /// landforms, where metres are the right unit again.
    /// </summary>
    private float LandBlocks(float meters) => Lift(meters) * _blocksPerMeter;

    /// <summary>The most rows the shore term adds, which it approaches on high ground and deep sea.</summary>
    private float ShoreBlocks => ShoreDetail * ShoreFade;

    /// <summary>
    /// Extra rows at an elevation <paramref name="meters"/> from the waterline, either side of it:
    /// <c>k s (1 - e^(-r / s))</c> with <c>r</c> the square root of the distance. Near the shore that
    /// is <c>k r</c>, so the rows are evenly spaced in the model's own units; it levels off at
    /// <see cref="ShoreBlocks"/>, so high ground and deep sea are only moved, not reshaped.
    /// </summary>
    private float ShoreRows(float meters)
    {
        if (ShoreDetail <= 0f || meters == 0f) return 0f;
        float root = (float)Math.Sqrt(Math.Abs(meters));
        return ShoreBlocks * (1f - (float)Math.Exp(-root / ShoreFade));
    }

    /// <summary>Elevation plus the shore rows, in metres of the normal scale. Strictly increasing.</summary>
    private float Lift(float meters) =>
        _blocksPerMeter > 0f && meters > 0f ? meters + ShoreRows(meters) / _blocksPerMeter : meters;

    /// <summary>
    /// The inverse of <see cref="Lift"/>, which is never below its argument and never more than the
    /// shore rows' worth above it, so the answer lies between those two.
    /// </summary>
    private float Unlift(float lifted)
    {
        if (ShoreDetail <= 0f || _blocksPerMeter <= 0f || lifted <= 0f) return lifted;

        float lo = Math.Max(0f, lifted - ShoreBlocks / _blocksPerMeter), hi = lifted;
        for (int i = 0; i < 50; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (Lift(mid) < lifted) lo = mid; else hi = mid;
        }
        return 0.5f * (lo + hi);
    }

    /// <summary>
    /// Where a block layer written for a uniform, true-to-scale world now sits, in blocks above the
    /// waterline. <paramref name="blocks"/> is read as that many horizontal blocks of real altitude.
    /// </summary>
    public float RescaleLayerHeight(float blocks) => blocks <= 0f ? blocks : LandBlocks(blocks * MetersPerBlock);

    /// <summary>Shape of the sea-floor curve: steep near the coast, heavily compressed in the abyss.</summary>
    private static float DepthCurve(float meters) => (float)(Math.Sqrt(meters + 10.0) - Math.Sqrt(10.0) + 1.0);

    /// <summary>
    /// Converts a model elevation in metres to a block Y coordinate: the topmost solid block of
    /// that column.
    ///
    /// Zero metres is the waterline, which is the <em>top</em> of block
    /// <see cref="WaterSurfaceY"/> — the sea fills every block below <see cref="SeaLevel"/>, so
    /// the last one it fills is the one under it. Land measured from <c>SeaLevel</c> instead would
    /// stand a block proud of the water everywhere along every coast, since a whole block of
    /// height is 15 m of elevation at the default resolution and the entire shore falls inside the
    /// first one.
    ///
    /// Land is linear up to the knee and then bends over towards the ceiling. The bend is
    /// <c>u / (1 + u)</c>, which has slope 1 at the join so there is no crease, and — unlike a
    /// saturating exponential — never quite flattens, so even in a region whose mountains overrun
    /// the world by several kilometres the summits stay rounded instead of shearing off into a
    /// mesa.
    ///
    /// Ocean floors use a square-root curve so that abyssal plains stay within the much shallower
    /// block budget below sea level. It is measured from the same waterline the land is, and has
    /// its own value at the shore taken off it, so that a column a handful of centimetres under
    /// water is a handful of centimetres under water rather than a cliff. What stops it being a
    /// puddle instead is the one-block floor below, which is a block because a block is the
    /// smallest depth the world can hold — not because of anything to do with the depth curve.
    /// That distinction is the whole bug this replaced: the floor used to be the curve's constant
    /// term, which <see cref="_oceanScale"/> then multiplied, so the shallows came out three
    /// blocks deep in a tall world and bone dry in a short one.
    /// </summary>
    public int ElevationToBlockY(float meters)
    {
        if (meters >= 0f)
        {
            float linear = LandBlocks(meters);
            float y;
            if (linear <= _kneeBlocks)
            {
                y = linear;
            }
            else
            {
                float span = Math.Max(1f, HeadroomBlocks - _kneeBlocks);
                float u = (linear - _kneeBlocks) / span;
                y = _kneeBlocks + span * (u / (1f + u));
            }
            return WaterSurfaceY + (int)y;
        }

        // At least one block, because anything below the waterline has to hold water, and a block
        // is the least the world can express. The shore rows mirror the land's, so the shallows
        // deepen evenly in the model's units instead of every column down to ~9 m sharing one row.
        int depth = Math.Max(1, (int)((DepthCurve(-meters) - DepthCurve(0f)) * _oceanScale + ShoreRows(meters)));
        return Math.Max(2, WaterSurfaceY - depth);
    }

    /// <summary>
    /// The topmost block the sea fills, whose upper face is the waterline. Ground at this height
    /// is level with the water rather than a step above it.
    /// </summary>
    public int WaterSurfaceY => SeaLevel - 1;

    /// <summary>Highest block Y the mapping can ever return, used for sanity logging.</summary>
    public int MaxBlockY => ElevationToBlockY(ModelMaxElevationMeters);

    /// <summary>
    /// How the terrain's height is being mapped, as a phrase. Shared by the one-line log summary
    /// and the field-per-line command readout so the two cannot drift apart.
    /// </summary>
    public string DescribeHeight() => (IsCalibrated
        ? $"calibrated to a {CalibratedPeakMeters:0} m peak ({EffectiveExaggeration:0.##}x, " +
          $"{MetersPerBlockVertical:0.##} m/block vertical)"
        : IsIsotropic
            ? "true to scale (1 block = 1 block in every direction)"
            : $"{EffectiveExaggeration:0.##}x ({MetersPerBlockVertical:0.##} m/block vertical)")
        + (ShoreDetail > 0f ? $", shore detail {ShoreDetail:0.##} rows per sqrt-metre fading over {ShoreFade:0.##}" : "");

    /// <summary>
    /// True when the world is so short that most mountains would be squashed. Only meaningful
    /// without calibration: a calibrated world fits its own terrain by construction, and when it
    /// cannot, <see cref="CalibrationClamped"/> says so instead.
    /// </summary>
    public bool IsWorldTooShort => !IsCalibrated && LinearRangeMeters < 3200f;

    /// <summary>
    /// World height that would keep terrain up to 5000 m perfectly to scale. A vanilla sea level
    /// rises with the world, so the room it needs is scaled up by the share above the sea; a fixed
    /// sea level stays put, so the room is simply added on top of it.
    /// </summary>
    public int RecommendedMapSizeY
    {
        get
        {
            float neededHeadroom = LandBlocks(5000f) / _shaping.LinearKneeFraction + 3;
            int needed = MapSizeY <= 0 || Heights.IsVanilla
                ? (int)Math.Ceiling(neededHeadroom / (1 - HeightFrame.VanillaSeaFraction))
                : SeaLevel + (int)Math.Ceiling(neededHeadroom);
            return Math.Min(4096, ((needed + 127) / 128) * 128);
        }
    }
}
