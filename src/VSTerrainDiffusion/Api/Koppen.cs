using System;

namespace VSTerrainDiffusion.Api;

/// <summary>
/// Köppen–Geiger classes (Beck et al. 2018 criteria) from monthly temperature and precipitation: a
/// port of the reference's <c>koppen.koppen_classes</c>, with the same class order and colours.
///
/// <see cref="Classify(double, double, MonthlyClimate)"/> is the hard class. <see cref="Memberships(double, double, MonthlyClimate, Softness, Span{float})"/> softens every threshold, so
/// a place near a boundary belongs partly to the classes on both sides; the weights sum to one and
/// become the hard class as the widths go to zero.
/// </summary>
public static class Koppen
{
    /// <summary>One Köppen class: its code, English name and map colour.</summary>
    public readonly struct ClassInfo
    {
        /// <summary>The class code, e.g. "Cfb".</summary>
        public readonly string Code;

        /// <summary>The class's English name, e.g. "Oceanic".</summary>
        public readonly string Name;

        /// <summary>The usual Köppen map colour, 0xRRGGBB.</summary>
        public readonly int Rgb;

        /// <summary>Creates a class entry.</summary>
        /// <param name="code">The class code.</param>
        /// <param name="name">The English name.</param>
        /// <param name="rgb">The map colour, 0xRRGGBB.</param>
        public ClassInfo(string code, string name, int rgb)
        {
            Code = code;
            Name = name;
            Rgb = rgb;
        }
    }

    /// <summary>
    /// Every land class, in the reference's order; an index into this array is a class index.
    /// </summary>
    public static readonly ClassInfo[] Classes =
    {
        new("Af", "Tropical rainforest", 0x0000FF), new("Am", "Tropical monsoon", 0x0078FF),
        new("Aw", "Tropical savanna", 0x46AAFA),
        new("BWh", "Hot desert", 0xFF0000), new("BWk", "Cold desert", 0xFF9696),
        new("BSh", "Hot semi-arid", 0xF5A500), new("BSk", "Cold semi-arid", 0xFFDC64),
        new("Csa", "Hot-summer Mediterranean", 0xFFFF00), new("Csb", "Warm-summer Mediterranean", 0xC8C800),
        new("Csc", "Cold-summer Mediterranean", 0x969600),
        new("Cwa", "Monsoon humid subtropical", 0x96FF96), new("Cwb", "Subtropical highland", 0x64C864),
        new("Cwc", "Cold subtropical highland", 0x329632),
        new("Cfa", "Humid subtropical", 0xC8FF50), new("Cfb", "Oceanic", 0x64FF50),
        new("Cfc", "Subpolar oceanic", 0x32C800),
        new("Dsa", "Hot-summer continental, dry summer", 0xFF00FF),
        new("Dsb", "Warm-summer continental, dry summer", 0xC800C8),
        new("Dsc", "Subarctic, dry summer", 0x963296), new("Dsd", "Extremely cold subarctic, dry summer", 0x966496),
        new("Dwa", "Hot-summer continental, dry winter", 0xAAAFFF),
        new("Dwb", "Warm-summer continental, dry winter", 0x5A78DC),
        new("Dwc", "Subarctic, dry winter", 0x4B50B4), new("Dwd", "Extremely cold subarctic, dry winter", 0x320087),
        new("Dfa", "Hot-summer humid continental", 0x00FFFF), new("Dfb", "Warm-summer humid continental", 0x37C8FF),
        new("Dfc", "Subarctic", 0x007D7D), new("Dfd", "Extremely cold subarctic", 0x00465F),
        new("ET", "Tundra", 0xB2B2B2), new("EF", "Ice cap", 0x666666),
    };

    /// <summary>Number of land classes; also the index callers use for water.</summary>
    public const int Count = 30;

    /// <summary>The class index callers use for water; one past the last land class.</summary>
    public const int Water = Count;

    /// <summary>Index of a class code such as "Cfb", or -1.</summary>
    public static int IndexOf(string code) => ByCode.TryGetValue(code, out int index) ? index : -1;

    /// <summary>Class index by code, built once from <see cref="Classes"/>.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, int> ByCode = BuildIndex();

    /// <summary>Builds <see cref="ByCode"/>.</summary>
    /// <returns>The code-to-index map.</returns>
    private static System.Collections.Generic.Dictionary<string, int> BuildIndex()
    {
        var index = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Classes.Length; i++) index[Classes[i].Code] = i;
        return index;
    }

    /// <summary>The figures every criterion is made of, from one place's twelve months.</summary>
    private readonly struct Facts
    {
        /// <summary>Annual mean temperature, °C.</summary>
        public readonly double MeanC;

        /// <summary>Annual precipitation, mm.</summary>
        public readonly double AnnualMm;

        /// <summary>Mean temperature of the hottest month, °C.</summary>
        public readonly double HottestC;

        /// <summary>Mean temperature of the coldest month, °C.</summary>
        public readonly double ColdestC;

        /// <summary>Precipitation of the driest month, mm.</summary>
        public readonly double DriestMm;

        /// <summary>Precipitation of the driest month of the summer half, mm.</summary>
        public readonly double SummerMinMm;

        /// <summary>Precipitation of the wettest month of the summer half, mm.</summary>
        public readonly double SummerMaxMm;

        /// <summary>Precipitation of the driest month of the winter half, mm.</summary>
        public readonly double WinterMinMm;

        /// <summary>Precipitation of the wettest month of the winter half, mm.</summary>
        public readonly double WinterMaxMm;

        /// <summary>The summer half's share of the annual precipitation, 0 to 1.</summary>
        public readonly double SummerFraction;

        /// <summary>How many months average above 10 °C.</summary>
        public readonly int MonthsAbove10;

        /// <summary>The twelve monthly mean temperatures, °C, in calendar order.</summary>
        public readonly double[] TemperatureC;

        /// <summary>Works out the figures from one place's months.</summary>
        /// <param name="meanC">Annual mean temperature, °C.</param>
        /// <param name="annualMm">Annual precipitation, mm; negative counts as zero.</param>
        /// <param name="months">The twelve monthly means.</param>
        public Facts(double meanC, double annualMm, MonthlyClimate months)
        {
            MeanC = meanC;
            AnnualMm = Math.Max(annualMm, 0.0);
            TemperatureC = months.TemperatureC;
            double[] t = months.TemperatureC, p = months.PrecipitationMm;

            // "Summer" is the six warmest months, so s and w need no hemisphere. A stable sort, as
            // numpy's, so ties keep month order.
            var order = new int[12];
            for (int m = 0; m < 12; m++) order[m] = m;
            for (int a = 1; a < 12; a++)
            {
                int key = order[a], b = a - 1;
                while (b >= 0 && t[order[b]] < t[key]) { order[b + 1] = order[b]; b--; }
                order[b + 1] = key;
            }

            HottestC = t[order[0]];
            ColdestC = t[order[11]];
            DriestMm = double.MaxValue;
            SummerMinMm = WinterMinMm = double.MaxValue;
            SummerMaxMm = WinterMaxMm = double.MinValue;
            double summerSum = 0.0;
            MonthsAbove10 = 0;
            for (int k = 0; k < 12; k++)
            {
                double pm = p[order[k]];
                DriestMm = Math.Min(DriestMm, pm);
                if (k < 6)
                {
                    SummerMinMm = Math.Min(SummerMinMm, pm);
                    SummerMaxMm = Math.Max(SummerMaxMm, pm);
                    summerSum += pm;
                }
                else
                {
                    WinterMinMm = Math.Min(WinterMinMm, pm);
                    WinterMaxMm = Math.Max(WinterMaxMm, pm);
                }
                if (t[k] > 10.0) MonthsAbove10++;
            }
            SummerFraction = AnnualMm > 0.0 ? summerSum / Math.Max(AnnualMm, 1e-12) : 0.5;
        }

        /// <summary>The aridity threshold, mm: more allowed where the rain falls in summer, when it evaporates.</summary>
        public double DryThreshold =>
            2.0 * MeanC + (SummerFraction >= 0.7 ? 28.0 : 1.0 - SummerFraction >= 0.7 ? 0.0 : 14.0);
    }

    /// <summary>The class index into <see cref="Classes"/> for one place.</summary>
    /// <param name="meanC">Annual mean temperature, °C.</param>
    /// <param name="seasonality">SD of monthly means × 100 (BIO4).</param>
    /// <param name="annualMm">Annual precipitation, mm.</param>
    /// <param name="cycle">The six cycle channels: t2c, t2s, p1c, p1s, p2c, p2s.</param>
    public static int Classify(double meanC, double seasonality, double annualMm, ReadOnlySpan<double> cycle) =>
        Classify(meanC, annualMm, MonthlyClimate.Reconstruct(meanC, seasonality, annualMm, cycle));

    /// <summary>
    /// The class index into <see cref="Classes"/> for one place, from its reconstructed months.
    /// </summary>
    /// <param name="meanC">Annual mean temperature, °C.</param>
    /// <param name="annualMm">Annual precipitation, mm.</param>
    /// <param name="months">Monthly temperature and precipitation.</param>
    /// <returns>The class index.</returns>
    public static int Classify(double meanC, double annualMm, MonthlyClimate months)
    {
        var f = new Facts(meanC, annualMm, months);
        if (f.HottestC < 10.0) return IndexOf(f.HottestC > 0.0 ? "ET" : "EF");

        double pTh = f.DryThreshold;
        if (f.AnnualMm < 10.0 * pTh)
            return IndexOf((f.AnnualMm < 5.0 * pTh ? "BW" : "BS") + (meanC >= 18.0 ? "h" : "k"));

        if (f.ColdestC >= 18.0)
            return IndexOf(f.DriestMm >= 60.0 ? "Af" : f.DriestMm >= 100.0 - f.AnnualMm / 25.0 ? "Am" : "Aw");

        char group = f.ColdestC > 0.0 ? 'C' : 'D';
        bool s = f.SummerMinMm < 40.0 && f.SummerMinMm < f.WinterMaxMm / 3.0;
        bool w = !s && f.WinterMinMm < f.SummerMaxMm / 10.0;
        char second = s ? 's' : w ? 'w' : 'f';
        char third = f.HottestC >= 22.0 ? 'a'
            : f.MonthsAbove10 >= 4 ? 'b'
            : group == 'D' && f.ColdestC < -38.0 ? 'd'
            : 'c';
        return IndexOf(new string(new[] { group, second, third }));
    }

    /// <summary>How sharply <see cref="Memberships(double, double, MonthlyClimate, Softness, Span{float})"/> moves from one side of a threshold to the other.</summary>
    public readonly struct Softness
    {
        /// <summary>Width of a temperature threshold, °C: about 2.2 widths take a weight from 10% to 90%.</summary>
        public readonly double TemperatureC;

        /// <summary>Width of a monthly precipitation threshold, mm.</summary>
        public readonly double MonthlyMm;

        /// <summary>Width of the aridity thresholds, as a natural log of the ratio (0.15 ≈ 15%).</summary>
        public readonly double AridityLog;

        /// <summary>Width of the summer-rain fraction thresholds (0.7 and 0.3).</summary>
        public readonly double SummerFraction;

        /// <summary>Creates a set of threshold widths.</summary>
        /// <param name="temperatureC">Width of temperature thresholds, °C.</param>
        /// <param name="monthlyMm">Width of monthly precipitation thresholds, mm.</param>
        /// <param name="aridityLog">Width of the aridity thresholds, natural log.</param>
        /// <param name="summerFraction">Width of the summer-rain fraction thresholds.</param>
        public Softness(double temperatureC, double monthlyMm, double aridityLog, double summerFraction)
        {
            TemperatureC = temperatureC;
            MonthlyMm = monthlyMm;
            AridityLog = aridityLog;
            SummerFraction = summerFraction;
        }

        /// <summary>
        /// Widths that blend neighbouring classes over a narrow band without blurring the map.
        /// </summary>
        public static readonly Softness Default = new(0.5, 5.0, 0.08, 0.03);
    }

    /// <summary>
    /// A weight per class (length <see cref="Count"/>) that sums to one. Each criterion of
    /// <see cref="Classify(double, double, MonthlyClimate)"/> becomes a logistic step of the given width, and a class's weight is
    /// the product of the steps along its branch of the decision tree.
    /// </summary>
    public static float[] Memberships(double meanC, double seasonality, double annualMm, ReadOnlySpan<double> cycle,
                                      Softness softness)
    {
        var weights = new float[Count];
        MonthlyClimate months = MonthlyClimate.Reconstruct(meanC, seasonality, annualMm, cycle);
        Memberships(meanC, annualMm, months, softness, weights);
        return weights;
    }

    /// <summary>
    /// Writes a weight per class into <paramref name="weights"/>, from already reconstructed months, without
    /// allocating.
    /// </summary>
    /// <param name="meanC">Annual mean temperature, °C.</param>
    /// <param name="annualMm">Annual precipitation, mm.</param>
    /// <param name="months">Monthly temperature and precipitation.</param>
    /// <param name="softness">Threshold widths.</param>
    /// <param name="weights">Receives <see cref="Count"/> weights summing to one; cleared first.</param>
    public static void Memberships(double meanC, double annualMm, MonthlyClimate months, Softness softness,
                                   Span<float> weights)
    {
        var f = new Facts(meanC, annualMm, months);
        double wt = Math.Max(softness.TemperatureC, 1e-6), wp = Math.Max(softness.MonthlyMm, 1e-6);
        double wa = Math.Max(softness.AridityLog, 1e-6), wf = Math.Max(softness.SummerFraction, 1e-6);
        weights.Clear();

        // Above(x, threshold, width): weight that x is above the threshold.
        static double Above(double x, double threshold, double width) => 1.0 / (1.0 + Math.Exp(-(x - threshold) / width));

        double pE = Above(10.0, f.HottestC, wt);
        double pEF = Above(0.0, f.HottestC, wt);
        Add(weights, "ET", pE * (1.0 - pEF));
        Add(weights, "EF", pE * pEF);

        // Aridity, on a log scale since the thresholds are proportional to the dry threshold.
        double summerRain = Above(f.SummerFraction, 0.7, wf), winterRain = Above(0.3, f.SummerFraction, wf);
        double pTh = 2.0 * meanC + 14.0 + 14.0 * summerRain - 14.0 * winterRain;
        double annual = Math.Max(f.AnnualMm, 1e-3);
        double pB = pTh <= 0.0 ? 0.0 : Above(Math.Log(10.0 * pTh / annual), 0.0, wa);
        double pBW = pTh <= 0.0 ? 0.0 : Above(Math.Log(5.0 * pTh / annual), 0.0, wa);
        double hot = Above(meanC, 18.0, wt);
        double b = (1.0 - pE) * pB;
        Add(weights, "BWh", b * pBW * hot);
        Add(weights, "BWk", b * pBW * (1.0 - hot));
        Add(weights, "BSh", b * (1.0 - pBW) * hot);
        Add(weights, "BSk", b * (1.0 - pBW) * (1.0 - hot));

        double rest = (1.0 - pE) * (1.0 - pB);
        double pA = Above(f.ColdestC, 18.0, wt);
        double af = Above(f.DriestMm, 60.0, wp), am = Above(f.DriestMm, 100.0 - f.AnnualMm / 25.0, wp);
        Add(weights, "Af", rest * pA * af);
        Add(weights, "Am", rest * pA * (1.0 - af) * am);
        Add(weights, "Aw", rest * pA * (1.0 - af) * (1.0 - am));

        double cd = rest * (1.0 - pA);
        double pC = Above(f.ColdestC, 0.0, wt);
        double s = Above(40.0, f.SummerMinMm, wp) * Above(f.WinterMaxMm / 3.0, f.SummerMinMm, wp);
        double w = (1.0 - s) * Above(f.SummerMaxMm / 10.0, f.WinterMinMm, wp);
        double fully = 1.0 - s - w;

        // A soft count of months above 10 °C, so b and c blend too.
        double warmMonths = 0.0;
        foreach (double t in f.TemperatureC) warmMonths += Above(t, 10.0, wt);
        double a = Above(f.HottestC, 22.0, wt);
        double bThird = (1.0 - a) * Above(warmMonths, 3.5, 0.5);
        double dThird = Above(-38.0, f.ColdestC, wt);
        foreach ((char second, double ps) in new[] { ('s', s), ('w', w), ('f', fully) })
        {
            Add(weights, $"C{second}a", cd * pC * ps * a);
            Add(weights, $"C{second}b", cd * pC * ps * bThird);
            Add(weights, $"C{second}c", cd * pC * ps * (1.0 - a - bThird));
            Add(weights, $"D{second}a", cd * (1.0 - pC) * ps * a);
            Add(weights, $"D{second}b", cd * (1.0 - pC) * ps * bThird);
            Add(weights, $"D{second}c", cd * (1.0 - pC) * ps * (1.0 - a - bThird) * (1.0 - dThird));
            Add(weights, $"D{second}d", cd * (1.0 - pC) * ps * (1.0 - a - bThird) * dThird);
        }
    }

    /// <summary>Adds <paramref name="weight"/> to the class named <paramref name="code"/>.</summary>
    /// <param name="weights">The weights being built.</param>
    /// <param name="code">A class code.</param>
    /// <param name="weight">The weight to add.</param>
    private static void Add(Span<float> weights, string code, double weight) => weights[IndexOf(code)] += (float)weight;
}
