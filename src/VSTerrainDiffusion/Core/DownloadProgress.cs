using System;
using System.Threading;
using Vintagestory.API.Common;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Reports a long download to the player in tenths.
///
/// The first run fetches a couple of gigabytes of model weights before a world can be generated,
/// and on a slow connection that is several minutes during which the loading screen says nothing
/// new. People reasonably conclude it has hung. One line per tenth is enough to show it is moving
/// without crowding the screen - eleven short lines against the roughly ten thousand characters
/// <c>updateLogText</c> allows before it gives up.
///
/// Counted across every file rather than per file, because the player is waiting on the total, not
/// on whichever one happens to be in flight.
/// </summary>
internal sealed class DownloadProgress
{
    /// <summary>Tenths, reported at 0% through 90%; the end is announced as "Done!" instead.</summary>
    private const int Steps = 10;

    private readonly ILogger _logger;
    private readonly string _what;

    private long _total;
    private long _done;
    private int _reported = -1;
    private bool _announced;

    /// <param name="what">Names the thing being fetched, e.g. "model files".</param>
    /// <param name="totalBytes">What is expected over the wire; may grow, see <see cref="AddPending"/>.</param>
    public DownloadProgress(ILogger logger, string what, long totalBytes)
    {
        _logger = logger;
        _what = what;
        _total = totalBytes;
    }

    /// <summary>True once there is anything to report, i.e. anything actually has to be fetched.</summary>
    public bool Active => _total > 0;

    /// <summary>
    /// Adds to the expected total. A file that looked complete and then failed its hash is not in
    /// the opening estimate - it is only discovered once hashing reaches it - so its bytes are
    /// added here rather than letting the percentage run past a hundred.
    /// </summary>
    public void AddPending(long bytes)
    {
        if (bytes <= 0) return;
        _total += bytes;
        Announce();
    }

    /// <summary>Posts the opening line and a zero, once, if there is anything to fetch.</summary>
    public void Announce()
    {
        if (_announced || !Active) return;
        _announced = true;

        LoadingNotice.Post(_logger, "Downloading {0} ({1}). This happens once.",
            _what, Pipeline.ModelAssetManager.HumanBytes(_total));
        Report(0);
    }

    /// <summary>Records bytes written. Called per buffer, so it stays cheap and allocation free.</summary>
    public void Advance(long bytes)
    {
        if (!_announced || bytes <= 0) return;

        long done = Interlocked.Add(ref _done, bytes);

        // Held one short of the end: the last tenth is the "Done!" line, which only goes out once
        // the bytes are on disk and verified rather than merely received.
        int step = (int)Math.Min(Steps - 1, done * Steps / Math.Max(1L, _total));
        if (step > _reported) Report(step);
    }

    /// <summary>Closes the report out. Silent if nothing was ever downloaded.</summary>
    public void Complete()
    {
        if (!_announced) return;
        LoadingNotice.Post(_logger, "Downloading {0}: Done!", _what);
    }

    private void Report(int step)
    {
        _reported = step;
        LoadingNotice.Post(_logger, "Downloading {0}: {1}%", _what, step * (100 / Steps));
    }
}
