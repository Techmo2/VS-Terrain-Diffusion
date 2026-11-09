using System;
using System.Collections.Generic;
using TerrainDiffusion.Water.Numerics;

namespace TerrainDiffusion.Water.Meander;

/// <summary>One plane wave <c>sin(RowWavenumber · row + ColumnWavenumber · column + Phase)</c> of the swing
/// field, in radians per cell.</summary>
/// <param name="RowWavenumber">Radians per cell along rows (Z).</param>
/// <param name="ColumnWavenumber">Radians per cell along columns (X).</param>
/// <param name="Phase">Phase in radians.</param>
public readonly record struct PlaneWave(double RowWavenumber, double ColumnWavenumber, double Phase);

/// <summary>
/// The seed-dependent parts of a <see cref="MeanderField"/>: the plane waves of its swing field, and the table
/// that turns a target sinuosity into a swing amplitude.
/// </summary>
/// <remarks>
/// A path whose heading swings as <c>θ = ω · n</c>, with n the swing field, has expected sinuosity
/// <c>1 / E[cos(ω n)]</c>. The table samples that relation at <see cref="SwingSteps"/> amplitudes ω from 0 to π/2
/// (the heading may swing at most 90°); the expectation is over the field's own distribution, sampled at random
/// world positions.
/// </remarks>
public sealed class MeanderTables
{
    /// <summary>Number of swing amplitudes in the table (0 to π/2 in steps of π/360).</summary>
    public const int SwingSteps = 181;

    /// <summary>Largest swing amplitude, radians.</summary>
    public const double MaxSwing = 0.5 * Math.PI;

    /// <summary>The field is clipped to ± this many standard deviations.</summary>
    public const double FieldClip = 1.5;

    /// <summary>Random positions the field is sampled at to build the sinuosity table.</summary>
    private const int FieldSamples = 200_000;
    /// <summary>Bins of the field-value histogram used for the expectation.</summary>
    private const int HistogramBins = 4096;
    /// <summary>Sample positions lie within ± this many cells of the origin.</summary>
    private const double SampleSpan = 1e5;
    /// <summary>Mixed into the seed so the field does not share a stream with other seeded uses.</summary>
    private const ulong Salt = 7177;

    /// <summary>The plane waves (backing <see cref="Waves"/>).</summary>
    private readonly PlaneWave[] waves;
    /// <summary>Swing amplitudes (backing <see cref="Swing"/>).</summary>
    private readonly double[] swing;
    /// <summary>Expected sinuosities (backing <see cref="Sinuosity"/>).</summary>
    private readonly double[] sinuosity;

    /// <summary>Tables from explicit values (for tests and for matching another implementation).</summary>
    /// <param name="waves">The plane waves of the field.</param>
    /// <param name="swing">Increasing swing amplitudes (radians).</param>
    /// <param name="sinuosity">The expected sinuosity at each amplitude, increasing.</param>
    /// <exception cref="ArgumentException">The arrays are empty, differ in length, or are not increasing.</exception>
    public MeanderTables(IReadOnlyList<PlaneWave> waves, ReadOnlySpan<double> swing, ReadOnlySpan<double> sinuosity)
    {
        if (waves.Count == 0)
        {
            throw new ArgumentException("At least one wave is needed.", nameof(waves));
        }
        if (swing.Length < 2 || swing.Length != sinuosity.Length)
        {
            throw new ArgumentException("The swing and sinuosity tables must have the same length, at least 2.");
        }
        for (int i = 1; i < swing.Length; i++)
        {
            if (!(swing[i] > swing[i - 1]) || !(sinuosity[i] >= sinuosity[i - 1]))
            {
                throw new ArgumentException("The swing and sinuosity tables must be increasing.");
            }
        }
        this.waves = new PlaneWave[waves.Count];
        for (int i = 0; i < waves.Count; i++)
        {
            this.waves[i] = waves[i];
        }
        this.swing = swing.ToArray();
        this.sinuosity = sinuosity.ToArray();
        Normalisation = Math.Sqrt(2.0 / this.waves.Length);
    }

    /// <summary>The plane waves of the swing field.</summary>
    public ReadOnlySpan<PlaneWave> Waves => waves;

    /// <summary>Swing amplitudes (radians) of the table.</summary>
    public ReadOnlySpan<double> Swing => swing;

    /// <summary>Expected sinuosity at each amplitude of <see cref="Swing"/>.</summary>
    public ReadOnlySpan<double> Sinuosity => sinuosity;

    /// <summary>Scale that gives the summed waves unit variance: <c>sqrt(2 / waves)</c>.</summary>
    public double Normalisation { get; }

    /// <summary>The swing field at a world position: the waves summed, normalised, and clipped to
    /// ±<see cref="FieldClip"/>.</summary>
    /// <param name="row">World row (cells).</param>
    /// <param name="column">World column (cells).</param>
    /// <returns>The field value, about unit spread.</returns>
    public double FieldAt(double row, double column)
    {
        double sum = 0.0;
        foreach (PlaneWave wave in waves)
        {
            sum += Math.Sin(wave.RowWavenumber * row + wave.ColumnWavenumber * column + wave.Phase);
        }
        return Math.Clamp(sum * Normalisation, -FieldClip, FieldClip);
    }

    /// <summary>The swing amplitude whose expected sinuosity is <paramref name="targetSinuosity"/>
    /// (interpolated, and clamped to the table).</summary>
    /// <param name="targetSinuosity">Target sinuosity (≥ 1).</param>
    /// <returns>Amplitude in radians.</returns>
    public double SwingFor(double targetSinuosity) => PythonMath.Interpolate(targetSinuosity, sinuosity, swing);

    /// <summary>
    /// Lays out the field for a seed: <paramref name="settings"/>.WaveCount waves of about the configured
    /// wavelength (±25 %), at evenly spread directions with random offsets, with random phases; then the
    /// sinuosity table, from the field sampled at 200,000 random positions.
    /// </summary>
    /// <param name="settings">Meander settings.</param>
    /// <param name="cellSizeMetres">Cell size in metres.</param>
    /// <param name="seed">World seed.</param>
    /// <returns>The tables.</returns>
    public static MeanderTables Generate(MeanderSettings settings, double cellSizeMetres, uint seed)
    {
        var random = new SplitMix64(((ulong)seed << 32) ^ Salt);
        int count = settings.WaveCount;
        double wavenumber = 2.0 * Math.PI * cellSizeMetres / settings.WavelengthMetres;   // radians per cell
        var waves = new PlaneWave[count];
        for (int i = 0; i < count; i++)
        {
            double direction = (i + random.NextDouble()) * Math.PI / count;
            double k = wavenumber * random.NextDouble(0.8, 1.25);
            waves[i] = new PlaneWave(k * Math.Sin(direction), k * Math.Cos(direction), random.NextDouble(0.0, 2.0 * Math.PI));
        }

        // The field's distribution: a histogram of samples, each bin represented by the mean of its samples
        var field = new MeanderTables(waves, stackalloc double[] { 0.0, 1.0 }, stackalloc double[] { 1.0, 1.0 });
        var binSum = new double[HistogramBins];
        var binCount = new int[HistogramBins];
        for (int i = 0; i < FieldSamples; i++)
        {
            double row = random.NextDouble(-SampleSpan, SampleSpan);
            double column = random.NextDouble(-SampleSpan, SampleSpan);
            double n = field.FieldAt(row, column);
            int bin = Math.Clamp((int)((n + FieldClip) / (2.0 * FieldClip) * HistogramBins), 0, HistogramBins - 1);
            binSum[bin] += n;
            binCount[bin]++;
        }

        var swing = new double[SwingSteps];
        var sinuosity = new double[SwingSteps];
        double step = MaxSwing / (SwingSteps - 1);
        for (int s = 0; s < SwingSteps; s++)
        {
            double omega = s == SwingSteps - 1 ? MaxSwing : s * step;
            double meanCos = 0.0;
            for (int bin = 0; bin < HistogramBins; bin++)
            {
                if (binCount[bin] > 0)
                {
                    meanCos += binCount[bin] * Math.Cos(omega * (binSum[bin] / binCount[bin]));
                }
            }
            swing[s] = omega;
            sinuosity[s] = FieldSamples / meanCos;
        }
        return new MeanderTables(waves, swing, sinuosity);
    }
}
