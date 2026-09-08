using System;
using System.Threading;
using Vintagestory.API.Common;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Stops the game when the model cannot generate this world's terrain.
///
/// There is deliberately no fallback anywhere in this mod. Half a world drawn by the model and
/// half by something else is not a degraded world, it is a broken one: the seam runs through
/// terrain that is already written to disk, the heightmaps on either side of it disagree, and
/// nothing short of deleting the save puts it right. A player is far better served by a game that
/// stops with the reason in the log than by one that quietly keeps generating.
///
/// Throwing is not enough to get that. Vintage Story catches whatever comes out of a chunk
/// generation handler - <c>ServerSystemSupplyChunks.runGenerators</c> logs it to the worldgen log,
/// which almost nobody reads, and then carries straight on to the next generator in the pass - so
/// an exception raised while filling a column leaves that column part filled by us and the rest by
/// whoever runs next, saved and indistinguishable from good terrain. The only way out of a
/// swallowed handler is to take the process with us.
///
/// The log write happens first and Vintage Story flushes every line as it writes it, so the reason
/// is on disk before the process goes.
/// </summary>
public static class DiffusionFailure
{
    private static int _failing;
    private static ILogger _ambient;

    /// <summary>
    /// Remembers the server's logger, so the parts of the pipeline that are not handed one can
    /// still say why they stopped the game. Called once, as the server starts.
    /// </summary>
    public static void UseLogger(ILogger logger) => _ambient = logger;

    /// <inheritdoc cref="Fatal(ILogger,string,Exception)"/>
    public static Exception Fatal(string problem, Exception cause = null) => Fatal(_ambient, problem, cause);

    /// <summary>
    /// Logs <paramref name="problem"/> and terminates the process. Never returns; the return type
    /// exists only so call sites can write <c>throw DiffusionFailure.Fatal(...)</c> and keep the
    /// compiler's flow analysis happy.
    /// </summary>
    /// <param name="logger">Where to write the reason. May be null.</param>
    /// <param name="problem">
    /// What went wrong, as a sentence, in terms the player can act on. This is the only thing they
    /// will see, so name the setting, the mod or the file involved.
    /// </param>
    /// <param name="cause">The underlying exception, when there is one.</param>
    public static Exception Fatal(ILogger logger, string problem, Exception cause = null)
    {
        // Chunks generate on several threads, so a fault usually arrives more than once. Only the
        // first one gets to explain itself; the rest wait here for it to bring the process down
        // rather than interleaving their own reports through the log.
        if (Interlocked.Exchange(ref _failing, 1) != 0)
        {
            Thread.Sleep(Timeout.Infinite);
        }

        string message =
            $"[{DiffusionPaths.ModId}] {problem}\n" +
            "Terrain Diffusion has stopped the game rather than let another generator finish this " +
            "world. It does not fall back: a world that is part modelled and part vanilla has a " +
            "permanent seam through it and cannot be repaired afterwards. Fix the fault reported " +
            "above, or turn the mod off for this world (world config: diffusionTerrain), and the " +
            "chunks generated so far will still be good.";

        logger ??= _ambient;

        try
        {
            logger?.Fatal(message);
            if (cause != null) logger?.Fatal(cause);

            // Also in the error log, which is the one people actually send with a bug report, and
            // in the worldgen log, which is where a chunk generation fault would be looked for.
            logger?.Error(message);
            if (cause != null) logger?.Error(cause);
            logger?.Log(EnumLogType.Worldgen, message);

            if (cause == null) logger?.Error("Raised at:\n{0}", Environment.StackTrace);
        }
        catch (Exception)
        {
            // A logger that is itself broken must not stop the process from going down.
        }

        Environment.FailFast(message, cause);
        return null; // unreachable
    }
}
