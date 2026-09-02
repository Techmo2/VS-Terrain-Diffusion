using System;
using System.Diagnostics;
using System.Threading;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Holds model inference to a share of wall-clock time, by idling after every graph execution for
/// as long as that execution took to run.
///
/// The problem this exists for is frame stuttering in single player. The integrated server
/// generates chunks on a background thread, but the model runs on the same GPU the game renders
/// with, and a decoder window can occupy it for the better part of a tenth of a second. The
/// renderer's own work queues behind it, so a burst of world generation reads as a freeze even
/// though nothing on the CPU is blocked.
///
/// There is no way to preempt a kernel that is already running - once a graph is submitted the GPU
/// runs it to completion - so the only lever is how often one is submitted. At 50% the pattern
/// becomes run, wait, run, wait rather than one unbroken block of compute, which leaves the
/// renderer regular windows to get a frame out. It does not shorten any individual run, so it
/// cannot remove stutter entirely; what it does is stop world generation from monopolising the
/// device for seconds at a time. World generation gets correspondingly slower: at 50% a tile takes
/// about twice as long, at 25% about four times.
/// </summary>
public static class InferenceThrottle
{
    /// <summary>Not worth a context switch, and Thread.Sleep cannot resolve it anyway.</summary>
    private static readonly long MinimumSleepTicks = Stopwatch.Frequency / 1000;

    private static volatile int _percent = 100;

    /// <summary>
    /// Idle time owed but not yet slept, in Stopwatch ticks. Runs shorter than a millisecond of
    /// debt would otherwise round to nothing and the limit would never bite.
    /// </summary>
    private static long _debtTicks;

    private static long _sleptTicks;

    /// <summary>
    /// Share of the time, as a percentage, that inference may keep the device busy. 100 disables
    /// the limiter entirely and is the default.
    /// </summary>
    public static int UtilizationPercent
    {
        get => _percent;
        set
        {
            int clamped = value < 5 ? 5 : value > 100 ? 100 : value;
            _percent = clamped;
            if (clamped >= 100) Interlocked.Exchange(ref _debtTicks, 0);
        }
    }

    public static bool IsLimiting => _percent < 100;

    /// <summary>Wall-clock milliseconds given up to the limiter since startup.</summary>
    public static long TotalThrottleMillis => Interlocked.Read(ref _sleptTicks) * 1000 / Stopwatch.Frequency;

    /// <summary>
    /// Called by <see cref="OnnxModel"/> once a graph execution has finished and its inputs have
    /// been released. <paramref name="busyTicks"/> is how long that execution took.
    /// </summary>
    public static void AfterRun(long busyTicks)
    {
        int percent = _percent;
        if (percent >= 100 || busyTicks <= 0) return;

        // busy / (busy + idle) = percent / 100.
        long owed = Interlocked.Add(ref _debtTicks, busyTicks * (100 - percent) / percent);
        if (owed < MinimumSleepTicks) return;

        Interlocked.Add(ref _debtTicks, -owed);
        Interlocked.Add(ref _sleptTicks, owed);
        Thread.Sleep((int)Math.Min(int.MaxValue, owed * 1000 / Stopwatch.Frequency));
    }

    /// <summary>Describes the limiter for the status command.</summary>
    public static string Describe() =>
        _percent >= 100
            ? "unlimited"
            : $"{_percent}% of the time ({TotalThrottleMillis} ms idled so far)";
}
