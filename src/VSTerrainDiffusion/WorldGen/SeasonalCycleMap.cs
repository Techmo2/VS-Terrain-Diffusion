using System;
using System.Runtime.CompilerServices;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.ServerMods;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// A per-region map of the seasonal model's cycle channels and diurnal range, beside
/// <see cref="SeasonalityMap"/>: the shape of the temperature year, when the rain falls, and the
/// day-night swing. Only a world made with the seasonal model has one; an annual-model world keeps
/// its seasons as they were.
///
/// Map region mod data is saved with the region and sent to clients, so a client with the mod
/// swings its weather the same way the server does. Floats rather than packed bytes, since the cycle
/// channels are small signed numbers, at the climate map's resolution (17 × 17 points a region).
/// </summary>
public static class SeasonalCycleMap
{
    /// <summary>The map region mod data key the grid is saved under.</summary>
    public const string ModDataKey = "vsterraindiffusion:cycle";

    /// <summary>
    /// t2c, t2s, p1c, p1s, p2c, p2s, diurnal range, and the rain harmonics' scale (1 + 5 mm / monthly
    /// mean, which the reference adds so deserts don't get huge relative swings).
    /// </summary>
    public const int Channels = 8;

    /// <summary>One region's points, as saved.</summary>
    [ProtoContract]
    public sealed class Grid
    {
        /// <summary>Points per edge.</summary>
        [ProtoMember(1)] public int Size;

        /// <summary><see cref="Channels"/> floats per point, row-major.</summary>
        [ProtoMember(2)] public float[] Values;
    }

    /// <summary>The seasonal cycle at one position.</summary>
    public readonly struct Sample
    {
        /// <summary>Second harmonic of the temperature cycle, cosine term, °C.</summary>
        public readonly float TemperatureShapeCos;

        /// <summary>Second harmonic of the temperature cycle, sine term, °C.</summary>
        public readonly float TemperatureShapeSin;

        /// <summary>
        /// First rain harmonic relative to the monthly mean, cosine term: when the wet season falls.
        /// </summary>
        public readonly float WetSeasonCos;

        /// <summary>First rain harmonic, sine term.</summary>
        public readonly float WetSeasonSin;

        /// <summary>Second rain harmonic, cosine term: a second rainy season.</summary>
        public readonly float SecondRainyCos;

        /// <summary>Second rain harmonic, sine term.</summary>
        public readonly float SecondRainySin;

        /// <summary>Mean diurnal temperature range, °C.</summary>
        public readonly float DiurnalRangeC;

        /// <summary>1 + 5 mm / monthly mean rain: how strongly the rain harmonics act here.</summary>
        public readonly float RainHarmonicScale;

        /// <summary>Reads one point's <see cref="Channels"/> values, in the map's order.</summary>
        public Sample(ReadOnlySpan<float> v)
        {
            TemperatureShapeCos = v[0];
            TemperatureShapeSin = v[1];
            WetSeasonCos = v[2];
            WetSeasonSin = v[3];
            SecondRainyCos = v[4];
            SecondRainySin = v[5];
            DiurnalRangeC = v[6];
            RainHarmonicScale = v[7];
        }

        /// <summary>
        /// Departure from the annual mean temperature at angle θ through the year (0 = midsummer),
        /// for a place whose monthly temperatures spread by <paramref name="sigmaC"/>: the reference's
        /// two-harmonic curve, whose first harmonic carries what the second leaves of the variance.
        /// </summary>
        public double TemperatureDeviation(double sigmaC, double theta)
        {
            double t2c = TemperatureShapeCos, t2s = TemperatureShapeSin;
            double a1 = Math.Sqrt(Math.Max(0.0, 2.0 * sigmaC * sigmaC - t2c * t2c - t2s * t2s));
            return a1 * Math.Cos(theta) + t2c * Math.Cos(2.0 * theta) + t2s * Math.Sin(2.0 * theta);
        }

        /// <summary>
        /// Rain at angle θ relative to the annual average (mean 1 over the twelve month centres),
        /// rebuilt as the reference rebuilds its monthly rain: clipped at zero and renormalised.
        /// </summary>
        public double RelativeRain(double theta)
        {
            double sum = 0.0;
            for (int m = 0; m < 12; m++) sum += Math.Max(0.0, 1.0 + RainHarmonicScale * RainHarmonics(Api.MonthlyClimate.MonthTheta(m)));
            double here = Math.Max(0.0, 1.0 + RainHarmonicScale * RainHarmonics(theta));
            return sum > 1e-9 ? here * 12.0 / sum : 1.0;
        }

        /// <summary>Both rain harmonics summed at angle <paramref name="th"/>, before scaling.</summary>
        private double RainHarmonics(double th) =>
            WetSeasonCos * Math.Cos(th) + WetSeasonSin * Math.Sin(th)
            + SecondRainyCos * Math.Cos(2.0 * th) + SecondRainySin * Math.Sin(2.0 * th);
    }

    /// <summary>Each region's grid once deserialised, dropped with the region.</summary>
    private static readonly ConditionalWeakTable<IMapRegion, Grid> Decoded = new();

    /// <summary>Stands in for a region with no grid, so the lookup is cached too.</summary>
    private static readonly Grid Missing = new() { Size = 0, Values = Array.Empty<float>() };

    /// <summary>Builds the map for one region from the coarse model's channels. Nothing for an annual-model world.</summary>
    public static void Generate(IMapRegion region, int regionX, int regionZ, int regionSize, TerrainDiffusionProvider provider)
    {
        if (!provider.CoarseSpec.HasSeasonalCycle) return;

        int step = TerraGenConfig.climateMapScale;
        int size = Math.Max(1, regionSize / step) + 1;
        var values = new float[size * size * Channels];
        Span<float> channels = stackalloc float[CoarseChannel.SeasonalCount];
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                double blockX = regionX * regionSize + x * step, blockZ = regionZ * regionSize + z * step;
                if (!provider.CoarseChannelsAt(blockX, blockZ, channels))
                    throw DiffusionFailure.Fatal("The seasonal cycle could not be read from the coarse map.");

                int o = (z * size + x) * Channels;
                channels.Slice(CoarseChannel.TemperatureShapeCos, 7).CopyTo(values.AsSpan(o, 7));
                double monthlyMm = Math.Max(0f, channels[CoarseChannel.Precipitation]) / 12.0;
                values[o + 7] = (float)(1.0 + Api.MonthlyClimate.PrecipitationEpsilon / Math.Max(monthlyMm, 1e-3));
            }
        }

        region.SetModdata(ModDataKey, SerializerUtil.Serialize(new Grid { Size = size, Values = values }));
    }

    /// <summary>The cycle at a world position, or null where none was generated (an annual-model world, or a vanilla region).</summary>
    public static Sample? At(IBlockAccessor blockAccessor, double x, double z)
    {
        int regionSize = blockAccessor.RegionSize;
        int rx = (int)Math.Floor(x / regionSize), rz = (int)Math.Floor(z / regionSize);
        IMapRegion region = blockAccessor.GetMapRegion(rx, rz);
        if (region == null) return null;

        Grid grid = GridFor(region);
        if (grid.Size < 2) return null;

        // Points sit every climateMapScale blocks from the region's corner; the last row and column
        // are the next region's first, so a region interpolates up to its edge without a seam.
        int inner = grid.Size - 1;
        double gx = (x - rx * (double)regionSize) / regionSize * inner;
        double gz = (z - rz * (double)regionSize) / regionSize * inner;
        int x0 = Math.Clamp((int)gx, 0, inner - 1), z0 = Math.Clamp((int)gz, 0, inner - 1);
        float fx = (float)Math.Clamp(gx - x0, 0.0, 1.0), fz = (float)Math.Clamp(gz - z0, 0.0, 1.0);

        Span<float> v = stackalloc float[Channels];
        float[] d = grid.Values;
        for (int c = 0; c < Channels; c++)
        {
            float a = d[(z0 * grid.Size + x0) * Channels + c], b = d[(z0 * grid.Size + x0 + 1) * Channels + c];
            float e = d[((z0 + 1) * grid.Size + x0) * Channels + c], f = d[((z0 + 1) * grid.Size + x0 + 1) * Channels + c];
            v[c] = (1 - fz) * ((1 - fx) * a + fx * b) + fz * ((1 - fx) * e + fx * f);
        }
        return new Sample(v);
    }

    /// <summary>
    /// The cycle at a block's centre; see <see cref="At(IBlockAccessor, double, double)"/>.
    /// </summary>
    public static Sample? At(IBlockAccessor blockAccessor, BlockPos pos) => At(blockAccessor, pos.X + 0.5, pos.Z + 0.5);

    /// <summary>
    /// A region's grid, decoded once; <see cref="Missing"/> when it has none or it is malformed.
    /// </summary>
    private static Grid GridFor(IMapRegion region)
    {
        if (Decoded.TryGetValue(region, out Grid cached)) return cached;

        Grid grid = Missing;
        try
        {
            byte[] raw = region.GetModdata(ModDataKey);
            if (raw != null) grid = SerializerUtil.Deserialize<Grid>(raw);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal("A region's stored seasonal cycle could not be read.", e);
        }

        if (grid.Values == null || grid.Values.Length != grid.Size * grid.Size * Channels) grid = Missing;
        Decoded.AddOrUpdate(region, grid);
        return grid;
    }
}
