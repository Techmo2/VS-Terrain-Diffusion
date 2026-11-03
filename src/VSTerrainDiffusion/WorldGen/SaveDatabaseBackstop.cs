using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Keeps a world's save from being left open when it shuts down.
///
/// The server closes the save database only when its chunk thread stops by itself
/// (<c>ServerThread.ShutDown</c>). If the thread dies on an exception instead, the connection stays
/// open for the rest of the game, and the save can't be opened again: the single player list shows
/// "database is locked" until the game is restarted.
///
/// The server disposes mods during its shutdown phase, before its final save and before it tells
/// the chunk thread to stop, so that thread may still be generating a column with this mod.
/// Releasing the models under it kills it. <see cref="ReleaseAfterChunkThread"/> waits for it.
/// </summary>
public static class SaveDatabaseBackstop
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string ChunkThreadName = "chunkdbthread";

    /// <summary>Under the 60 s the server gives its threads, so this never outlasts a stopping server.</summary>
    private static readonly TimeSpan ChunkThreadWait = TimeSpan.FromSeconds(55);

    /// <summary>How long to wait for the server to finish its final save and stop.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Runs <paramref name="release"/> once the chunk thread has stopped (or after
    /// <see cref="ChunkThreadWait"/>), then closes the save once the server has stopped if the
    /// thread died without closing it. Returns at once; the waiting happens on its own thread.
    /// </summary>
    public static void ReleaseAfterChunkThread(ICoreServerAPI api, Action release)
    {
        Thread chunkThread = FindChunkThread(api);
        ILogger logger = api.Logger;
        var watcher = new Thread(() =>
        {
            // Nothing here may throw: an exception on this thread would take the whole game down.
            try
            {
                WaitFor(chunkThread);
                try { release(); }
                catch (Exception e) { Log(logger, l => l.Warning("[{0}] Shutting down: {1}", Core.DiffusionPaths.ModId, e)); }

                // The final save runs after mods are disposed, so only once the server has stopped is
                // its connection done with. Never closed under a thread that is still running.
                var clock = Stopwatch.StartNew();
                while (api.Server.CurrentRunPhase < EnumServerRunPhase.Exit && clock.Elapsed < ExitWait) Thread.Sleep(200);
                if (api.Server.CurrentRunPhase < EnumServerRunPhase.Exit || chunkThread is { IsAlive: true }) return;
                CloseIfLeftOpen(api, logger);
            }
            catch (Exception e)
            {
                Log(logger, l => l.Warning("[{0}] Shutdown watcher: {1}", Core.DiffusionPaths.ModId, e.Message));
            }
        }) { IsBackground = true, Name = "TerrainDiffusion shutdown" };
        watcher.Start();
    }

    /// <summary>
    /// Waits for the chunk thread to stop, up to <see cref="ChunkThreadWait"/>. A thread that was
    /// never started has nothing to wait for; Join would throw on it.
    /// </summary>
    private static void WaitFor(Thread chunkThread)
    {
        if (chunkThread == null || (chunkThread.ThreadState & System.Threading.ThreadState.Unstarted) != 0) return;
        try
        {
            chunkThread.Join(ChunkThreadWait);
        }
        catch (ThreadStateException)
        {
            // Not started after all, or already gone.
        }
    }

    private static Thread FindChunkThread(ICoreServerAPI api) =>
        (Field(api.World, "Serverthreads") as IEnumerable<Thread>)?.FirstOrDefault(t => t.Name == ChunkThreadName);

    private static void CloseIfLeftOpen(ICoreServerAPI api, ILogger logger)
    {
        try
        {
            object saveSystem = (Field(api.World, "Systems") as Array)?.Cast<object>()
                .FirstOrDefault(s => s?.GetType().Name == "ServerSystemLoadAndSaveGame");
            object database = Field(Field(saveSystem, "chunkthread"), "gameDatabase");
            if (database == null) return;
            if (Field(database, "conn") == null)
            {
                Log(logger, l => l.VerboseDebug("[{0}] The save was closed normally.", Core.DiffusionPaths.ModId));
                return;
            }

            database.GetType().GetMethod("Dispose", Any, Type.EmptyTypes)?.Invoke(database, null);
            Log(logger, l => l.Notification("[{0}] The chunk thread stopped without closing the save; closed it, " +
                "so the world can be opened again without restarting the game.", Core.DiffusionPaths.ModId));
        }
        catch (Exception e)
        {
            Log(logger, l => l.Warning("[{0}] Could not check whether the save was left open: {1}", Core.DiffusionPaths.ModId, e.Message));
        }
    }

    private static object Field(object instance, string name)
    {
        for (Type type = instance?.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, Any | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(instance);
        }
        return null;
    }

    private static void Log(ILogger logger, Action<ILogger> write)
    {
        try { write(logger); }
        catch (Exception) { /* The server's logger closes with it. */ }
    }
}
