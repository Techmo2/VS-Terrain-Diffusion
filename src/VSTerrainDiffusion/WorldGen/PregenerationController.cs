using System;
using System.Globalization;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Owns the world's <c>/tdiff pregen</c> job: starts it, records it in the save while it runs so a
/// server restart picks it up again, reports progress, and stops it.
/// </summary>
public sealed class PregenerationController
{
    /// <summary>
    /// Where a running job is recorded in the save, as "x,z,radius". Emptied once it finishes or is
    /// stopped.
    /// </summary>
    private const string SaveKey = "terraindiffusionPregen";

    /// <summary>Furthest a pregeneration may reach, in blocks: 100 km, about 480,000 tiles of 256.</summary>
    public const int MaxRadius = 100_000;

    private readonly ICoreServerAPI _api;

    /// <summary>The world's current lakes and rivers, which a running job holds back for.</summary>
    private readonly Func<WorldWater> _water;

    /// <summary>Creates the controller for one server.</summary>
    /// <param name="api">The server API.</param>
    /// <param name="water">Returns the world's current lakes and rivers, or null when it has none.</param>
    public PregenerationController(ICoreServerAPI api, Func<WorldWater> water)
    {
        _api = api;
        _water = water;
    }

    /// <summary>The job started this session, running or not; null if none has been.</summary>
    public TilePregenerator Current { get; private set; }

    /// <summary>Whether a job is running now.</summary>
    public bool IsRunning => Current?.IsRunning == true;

    /// <summary>
    /// Starts a job and records it in the save. Progress goes to the log, and to
    /// <paramref name="playerUid"/>'s chat when a player started it.
    /// </summary>
    /// <param name="provider">The world's terrain, which must have a tile database attached.</param>
    /// <param name="x">Centre block X.</param>
    /// <param name="z">Centre block Z.</param>
    /// <param name="radius">Radius in blocks.</param>
    /// <param name="playerUid">The player to tell, or null.</param>
    /// <returns>The running job.</returns>
    public TilePregenerator Start(TerrainDiffusionProvider provider, int x, int z, int radius, string playerUid)
    {
        var job = new TilePregenerator(provider, _api.Logger, x, z, radius, _api.WorldManager.RegionSize);
        // A fast GPU would otherwise queue tiles for the water without bound.
        job.Hold = () => _water() is { } water && water.Backlog > WorldWater.PregenBacklogLimit;
        job.Progress += j =>
        {
            string line = "Pregen: " + j.Describe();
            _api.Logger.Notification("[{0}] {1}", DiffusionPaths.ModId, line);
            TellPlayer(playerUid, line);
        };
        job.Finished += j =>
        {
            TellPlayer(playerUid, $"Pregen {(j.Completed ? "finished" : "stopped")}: {j.Describe()}");
            if (!j.Completed) return;
            _api.Event.EnqueueMainThreadTask(() =>
            {
                if (ReferenceEquals(Current, j)) Forget();
            }, "terraindiffusion-pregen-done");
        };

        Current = job;
        _api.WorldManager.SaveGame.StoreData(SaveKey,
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{x},{z},{radius}")));
        job.Start();
        return job;
    }

    /// <summary>Picks up a job the save says was still running when the server last stopped.</summary>
    /// <param name="provider">The world's terrain, or null if it is not generating.</param>
    public void Resume(TerrainDiffusionProvider provider)
    {
        byte[] stored = _api.WorldManager.SaveGame.GetData(SaveKey);
        if (stored == null || stored.Length == 0) return;

        string[] parts = Encoding.UTF8.GetString(stored).Split(',');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int radius))
        {
            Forget();
            return;
        }

        if (provider?.Store == null)
        {
            _api.Logger.Warning(
                "[{0}] Pregen not resumed: this world no longer has a tile database.", DiffusionPaths.ModId);
            Forget();
            return;
        }

        _api.Logger.Notification("[{0}] Resuming pregen: {1} blocks around ({2}, {3}).",
            DiffusionPaths.ModId, TilePregenerator.Count(radius), x, z);
        Start(provider, x, z, radius, null);
    }

    /// <summary>Stops the running job, waiting for its tile.</summary>
    /// <param name="forget">Also drop it from the save, so it is not resumed.</param>
    public void Stop(bool forget)
    {
        Current?.Dispose();
        Current = null;
        if (forget) Forget();
    }

    /// <summary>Drops the job from the save, so the next start does not resume it.</summary>
    public void Forget() =>
        _api.WorldManager.SaveGame.StoreData(SaveKey, Array.Empty<byte>());

    /// <summary>One line on the session's job: its state, area and progress.</summary>
    public string Describe()
    {
        TilePregenerator job = Current;
        if (job == null)
        {
            return "No pregen has run this session. Start one with /tdiff pregen <radius> [x z].";
        }
        string state = job.IsStopping ? "stopping" : job.IsRunning ? "running"
            : job.Completed ? "finished" : "stopped";
        return $"Pregen {state}, {TilePregenerator.Count(job.RadiusBlocks)} blocks around " +
               $"({job.CentreX}, {job.CentreZ}): " +
               job.Describe();
    }

    /// <summary>A chat line to a player, if they are still online. Safe from any thread.</summary>
    private void TellPlayer(string playerUid, string message)
    {
        if (playerUid == null) return;
        _api.Event.EnqueueMainThreadTask(() =>
        {
            if (_api.World.PlayerByUid(playerUid) is IServerPlayer player &&
                player.ConnectionState == EnumClientState.Playing)
            {
                player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
            }
        }, "terraindiffusion-pregen-message");
    }
}
