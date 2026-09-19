using Vintagestory.API.Common;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Puts a line on the world loading screen.
///
/// The screen the game shows while a single player world starts is a live tail of the server log,
/// but it only shows two of the log types: <c>StoryEvent</c> normally, and <c>Event</c> when
/// developer mode is on. Everything else - notifications included - goes to the log file and is
/// never seen by the player. The first run of this mod downloads a couple of gigabytes of model
/// weights before the world can be generated, which on a slow connection is long enough that a
/// screen with nothing new on it looks like a hang, so the download reports itself through here.
///
/// Each notice is logged twice, once under each type, so it appears whether or not developer mode
/// is on. That also keeps <c>Event</c>'s console output, which is the only way an operator running
/// a dedicated server sees any of this. The cost is a duplicate line in server-main.log, which is
/// a fair trade for a handful of messages that only appear while something slow is happening.
///
/// Rationed, because the screen appends lines and never rewrites them and <c>updateLogText</c>
/// stops accepting new ones past about ten thousand characters. What goes here is the model
/// download reporting itself in tenths (see <see cref="DownloadProgress"/>), a line when the
/// inference runtime is fetched, and one when both are done - around six hundred characters all
/// told. Anyone who wants byte counts per file has server-main.log.
/// </summary>
public static class LoadingNotice
{
    /// <summary>
    /// Posts a notice. The format string and arguments are passed through to the logger rather than
    /// formatted here, because the loading screen formats them itself when it picks the entry up.
    /// </summary>
    public static void Post(ILogger logger, string format, params object[] args)
    {
        // The mod's name rather than its id: this is the one place the player reads our log lines,
        // and it sits next to the game's own "It begins...".
        string prefixed = "Terrain Diffusion: " + format;
        logger.StoryEvent(prefixed, args);
        logger.Event(prefixed, args);
    }
}
