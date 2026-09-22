using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Makes Vintage Story's static translocator search survivable in a world whose terrain costs real
/// time to generate.
///
/// A repaired but unlinked translocator hunts for another one four times a second, forever. Each
/// attempt picks a point 400 to 8000 blocks away and peeks a chunk column there, which generates a
/// 3x3 block of columns through the whole Terrain pass, scans the middle one for a ruin, and throws
/// all nine away. Under vanilla worldgen that is cheap. Here every one of those columns is modelled
/// terrain.
///
/// Three things are done about it:
/// <list type="bullet">
/// <item>The search can stall forever. The game drops a peek outright if it cannot pause its
/// worldgen threads within 3.6 seconds, and the translocator has already cleared the flag that
/// would make it try again, so it sits on "Warping spacetime..." until its block entity is reloaded
/// from disk. A watchdog puts the flag back.</item>
/// <item>Terrain a peek generated is marked so the tile cache evicts it first, through
/// <see cref="ChunkPeekScope"/>. Otherwise a search walking thousands of blocks away evicts the
/// ground the player is standing on.</item>
/// <item>Optionally, the search ring is narrowed - see <c>TranslocatorMaxRangeBlocks</c>. Off by
/// default, because it changes which translocator ends up linked to which.</item>
/// </list>
///
/// None of this touches terrain, so unlike <see cref="SurfaceClimateCompat"/> a patch that fails to
/// attach is not fatal: the world is still correct, only slower.
/// </summary>
public static class TranslocatorSearchCompat
{
    private const string HarmonyId = "vsterraindiffusion.translocatorsearch";

    private static Harmony _harmony;
    private static ICoreServerAPI _api;

    /// <summary>True while the patches are in place.</summary>
    public static bool Installed { get; private set; }

    private static long _peeks;
    private static long _rearms;
    private static long _regionMapsSkipped;

    /// <summary>What the translocator search has cost and what was saved, for <c>/tdiff status</c>.</summary>
    public static string Describe() => Installed
        ? $"{_peeks} chunk peeks, {_regionMapsSkipped} region seasonality maps skipped, {_rearms} stalled searches restarted"
        : "not installed";

    /// <summary>Counted by the map region handler when it declines to build a map for a peek.</summary>
    public static void CountRegionMapSkipped() => Interlocked.Increment(ref _regionMapsSkipped);

    private sealed class SearchState
    {
        public long StartedMs;
        public bool Warned;

        /// <summary>How many times this translocator's search has had to be restarted.</summary>
        public int Restarts;

        /// <summary>
        /// A peek found a ruin and the real chunk load for it is in flight. Written from the peek
        /// thread, read from the tick.
        /// </summary>
        public volatile bool AwaitingExitChunk;
    }

    private static readonly ConditionalWeakTable<BlockEntityStaticTranslocator, SearchState> States = new();

    public static void Install(ICoreServerAPI api)
    {
        Uninstall();

        _api = api;

        MethodInfo tick = AccessTools.Method(typeof(BlockEntityStaticTranslocator), "OnServerGameTick");
        MethodInfo tested = AccessTools.Method(typeof(BlockEntityStaticTranslocator), "TestForExitPoint");

        // Internal to the server assembly, so they are found by name.
        Type supply = AccessTools.TypeByName("Vintagestory.Server.ServerSystemSupplyChunks");
        MethodInfo peekArea = supply == null ? null : AccessTools.Method(supply, "PeekChunkAreaLocking");
        MethodInfo pause = supply == null ? null : AccessTools.Method(supply, "PauseAllWorldgenThreads");

        if (tick == null || tested == null || peekArea == null || pause == null)
        {
            api.Logger.Warning(
                "[{0}] Vintage Story's translocator search is not where this mod speeds it up, so " +
                "linking a translocator will generate terrain at the game's own pace.",
                DiffusionPaths.ModId);
            return;
        }

        try
        {
            _harmony = new Harmony(HarmonyId);

            _harmony.Patch(tick,
                prefix: Method(nameof(BeforeTranslocatorTick)),
                finalizer: Method(nameof(AfterTranslocatorTick)));
            _harmony.Patch(tested, postfix: Method(nameof(AfterTestForExitPoint)));
            _harmony.Patch(pause, prefix: Method(nameof(BeforePauseAllWorldgenThreads)));
            _harmony.Patch(peekArea,
                prefix: Method(nameof(BeforePeekArea)),
                finalizer: Method(nameof(AfterPeekArea)));
        }
        catch (Exception e)
        {
            Uninstall();
            api.Logger.Warning(
                "[{0}] Vintage Story's translocator search could not be patched, so linking a " +
                "translocator will generate terrain at the game's own pace: {1}",
                DiffusionPaths.ModId, e.Message);
            return;
        }

        foreach (MethodInfo target in new[] { tick, tested, peekArea, pause })
        {
            Patches info = Harmony.GetPatchInfo(target);
            if (info != null && info.Owners.Contains(HarmonyId)) continue;

            Uninstall();
            api.Logger.Warning(
                "[{0}] The translocator search patch for {1} did not attach, so linking a " +
                "translocator will generate terrain at the game's own pace.",
                DiffusionPaths.ModId, target.Name);
            return;
        }

        Installed = true;
        api.Logger.Notification(
            "[{0}] Translocator search: stall watchdog armed, peeked terrain evicted first.",
            DiffusionPaths.ModId);
    }

    private static HarmonyMethod Method(string name) =>
        new(AccessTools.Method(typeof(TranslocatorSearchCompat), name));

    /// <summary>Removes the patches. Safe to call when nothing is installed.</summary>
    public static void Uninstall()
    {
        try
        {
            _harmony?.UnpatchAll(HarmonyId);
        }
        catch (Exception e)
        {
            _api?.Logger.Warning("[{0}] Could not remove the translocator search patches: {1}",
                DiffusionPaths.ModId, e.Message);
        }

        _harmony = null;
        _api = null;
        Installed = false;
    }

    // ------------------------------------------------------------------ the search tick

    private static void BeforeTranslocatorTick(BlockEntityStaticTranslocator __instance)
    {
        // Narrowing the ring keeps candidates inside terrain that is likely already generated and
        // still cached, which is what actually costs time here - not the number of attempts.
        int cap = DiffusionConfig.Instance.WorldGen.TranslocatorMaxRangeBlocks;
        if (cap > 0 && __instance.findNextChunk)
        {
            __instance.MaxTeleporterRangeInBlocks =
                Math.Max(__instance.MinTeleporterRangeInBlocks + 1, cap);
        }
    }

    /// <summary>
    /// A finalizer rather than a postfix, so a throw inside the tick still runs the watchdog.
    /// </summary>
    private static void AfterTranslocatorTick(BlockEntityStaticTranslocator __instance)
        => RunStallWatchdog(__instance);

    /// <summary>
    /// Notes that a peek found a ruin, so the watchdog leaves this translocator alone while the
    /// real chunk load for the far end is running.
    ///
    /// That load goes through the ordinary request queue rather than the peek queue, so it cannot
    /// be dropped the way a peek can - but under this mod it generates a whole column to the Done
    /// pass plus its 3x3 neighbourhood, which can take minutes. Re-arming the search on top of it
    /// would leave two searches running, and if both found a ruin one end would be linked to a
    /// translocator that points somewhere else.
    ///
    /// Runs on the peek thread, not the tick thread.
    /// </summary>
    private static void AfterTestForExitPoint(BlockEntityStaticTranslocator __instance)
    {
        if (__instance.findNextChunk) return;
        States.GetOrCreateValue(__instance).AwaitingExitChunk = true;
    }

    /// <summary>
    /// Puts <c>findNextChunk</c> back when a peek was dispatched and never came back.
    ///
    /// <c>ServerSystemSupplyChunks</c> drops a queued peek if it cannot pause every worldgen thread
    /// within 3.6 seconds, and those threads only park between chunk columns - here a column can be
    /// waiting on the model behind the provider's inference gate for longer than that. The
    /// translocator cleared the flag before dispatching, so a dropped peek strands it for good.
    /// This is the same recovery the game itself does, but without needing the chunk to be
    /// unloaded and read back from disk first.
    /// </summary>
    private static void RunStallWatchdog(BlockEntityStaticTranslocator __instance)
    {
        WorldGenConfig config = DiffusionConfig.Instance.WorldGen;
        int timeoutSeconds = config.TranslocatorSearchTimeoutSeconds;
        if (timeoutSeconds <= 0) return;

        // Never fire while a peek could still legitimately be waiting for worldgen to pause, or
        // the watchdog starts a second search on top of one that was about to succeed.
        timeoutSeconds = Math.Max(timeoutSeconds, config.TranslocatorPeekPauseSeconds + 60);

        SearchState state = States.GetOrCreateValue(__instance);

        // Searching again, already linked, or not repaired yet: nothing is outstanding.
        if (__instance.tpLocation != null)
        {
            state.StartedMs = 0;
            state.AwaitingExitChunk = false;
            state.Restarts = 0;
            return;
        }

        if (__instance.findNextChunk || !__instance.FullyRepaired)
        {
            state.StartedMs = 0;
            state.AwaitingExitChunk = false;
            return;
        }

        // A ruin was found and its chunk is being generated for real. That is slow here, but it is
        // progress, and starting a second search over the top of it is how a one-way link is made.
        if (state.AwaitingExitChunk)
        {
            state.StartedMs = 0;
            return;
        }

        long now = Environment.TickCount64;
        if (state.StartedMs == 0)
        {
            state.StartedMs = now;
            return;
        }

        // Each restart waits longer than the last, to four times the base. A server this far
        // behind is not helped by being handed another peek the moment one is given up on.
        long deadline = timeoutSeconds * 1000L * Math.Min(4, state.Restarts + 1);
        if (now - state.StartedMs < deadline) return;

        __instance.findNextChunk = true;
        state.StartedMs = 0;
        state.Restarts++;
        Interlocked.Increment(ref _rearms);

        if (state.Warned) return;
        state.Warned = true;
        _api?.Logger.Debug(
            "[{0}] The translocator at {1} waited {2} s for a chunk peek that never returned, so " +
            "its search was started again. This means the server could not pause worldgen in time.",
            DiffusionPaths.ModId, __instance.Pos, timeoutSeconds);
    }

    // ------------------------------------------------------------------ the peek itself

    /// <summary>
    /// <c>coords</c> is the chunk column the peek is centred on; the game generates the 3x3 around
    /// it and runs the pass that places structures on the middle one.
    /// </summary>
    /// <summary>
    /// Gives the pause before a chunk peek a deadline that suits modelled terrain.
    ///
    /// The server waits 3.6 s for every worldgen thread to park and drops the peek if they do not.
    /// A thread only parks between chunk columns, and a column here waits on the model behind a
    /// single inference gate, so with several threads queued the wait is routinely longer than
    /// that - the peek is thrown away after the work was already done, and the search starts over.
    /// There is no way to cut a column short: neither the pipeline nor ONNX Runtime can be
    /// interrupted once a tile is being generated. Waiting is the only thing left.
    ///
    /// Only the peek's own 3600 is touched. The other caller passes 5000 and is left alone.
    /// </summary>
    private static bool BeforePauseAllWorldgenThreads(ref int timeoutms)
    {
        if (timeoutms != PeekPauseTimeoutMs) return true;

        int seconds = DiffusionConfig.Instance.WorldGen.TranslocatorPeekPauseSeconds;
        if (seconds > 0) timeoutms = seconds * 1000;
        return true;
    }

    /// <summary>The deadline the server uses before a chunk peek, and nothing else.</summary>
    private const int PeekPauseTimeoutMs = 3600;

    private static void BeforePeekArea(Vec2i coords)
    {
        Interlocked.Increment(ref _peeks);
        ChunkPeekScope.Enter(coords.X, coords.Y);
    }

    private static void AfterPeekArea() => ChunkPeekScope.Exit();
}
