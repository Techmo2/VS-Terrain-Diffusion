using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Finds a new world's spawn on dry land in its starting climate, and moves the save's spawn there
/// once the game has created the spawn record. An existing world never has its spawn moved.
/// </summary>
public sealed class SpawnPlacement
{
    /// <summary>Furthest a spawn in a lake or river is moved to reach dry land, in blocks.</summary>
    private const int ShoreSearchBlocks = 256;

    /// <summary>The server API.</summary>
    private readonly ICoreServerAPI _api;

    /// <summary>The spawn to apply when the game reaches RunGame, or null.</summary>
    private (int X, int Y, int Z)? _pendingSpawn;

    /// <summary>Creates the placement for one server.</summary>
    /// <param name="api">The server API.</param>
    public SpawnPlacement(ICoreServerAPI api)
    {
        _api = api;
    }

    /// <summary>
    /// Finds somewhere to wake up. Vanilla guarantees land at the map centre by forcing its ocean
    /// map, and conditioning the terrain on that map carries the guarantee through, so this usually
    /// only has to move the spawn far enough to satisfy the starting climate - and in "output" mode,
    /// where the model decides the coastline itself, far enough to find land at all.
    /// </summary>
    /// <param name="provider">The world's terrain.</param>
    /// <param name="settings">The world's settings.</param>
    /// <returns>The spawn column, or null when there is no land within reach.</returns>
    public TerrainDiffusionProvider.SpawnCandidate? Find(TerrainDiffusionProvider provider, DiffusionWorldSettings settings)
    {
        try
        {
            // A new world starts at 08:00 on the first day of the fifth month (the game's
            // SaveGame.GetTotalGameSecondsStart), so the starting climate is what a player reads then:
            // the middle of that day, with the calendar's own test for which way the year runs.
            IGameCalendar calendar = _api.World.Calendar;
            int daysPerMonth = Math.Max(1, calendar?.DaysPerMonth ?? 12);
            WorldGenConfig worldGen = DiffusionConfig.Instance.WorldGen;
            provider.ArrivalSeason = (4.0 + 0.5 / daysPerMonth,
                z => worldGen.SeasonHemispheres && calendar?.OnGetLatitude != null && !(calendar.OnGetLatitude(z) > 0.0),
                worldGen.SeasonalTemperatureStrength,
                z => DiffusionSeasons.WinterGain(_api.World, z));
            TerrainDiffusionProvider.SpawnCandidate? land = provider.FindSpawn();
            if (land == null)
            {
                _api.Logger.Warning(
                    "[{0}] No land found near the world centre; the spawn point was left where it was. " +
                    "Try a different seed if you start in the ocean.", DiffusionPaths.ModId);
                return null;
            }

            ReportStartingClimate(land.Value, settings);
            return land;
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger, "The spawn search failed.", e);
        }
    }

    /// <summary>
    /// Turns the spawn column <see cref="Find"/> chose into a position to apply once the save game's
    /// spawn record exists, moving it out of any lake or river. Only for brand new saves.
    /// </summary>
    /// <param name="land">The spawn column, or null to leave the spawn alone.</param>
    /// <param name="provider">The world's terrain.</param>
    /// <param name="settings">The world's settings.</param>
    /// <param name="water">The world's lakes and rivers, or null when it has none.</param>
    public void Record(TerrainDiffusionProvider.SpawnCandidate? land, TerrainDiffusionProvider provider,
        DiffusionWorldSettings settings, WorldWater water)
    {
        if (land == null || !_api.WorldManager.SaveGame.IsNew) return;

        try
        {
            int blockX = land.Value.BlockX, blockZ = land.Value.BlockZ;
            if (water != null && water.IsWater(blockX, blockZ))
            {
                (int x, int z)? shore = NearestDryLand(blockX, blockZ, provider, settings, water);
                if (shore != null)
                {
                    _api.Logger.Notification("[{0}] The spawn search's column ({1}, {2}) is in a lake or river; moved to " +
                        "dry land at ({3}, {4}).", DiffusionPaths.ModId, blockX, blockZ, shore.Value.x, shore.Value.z);
                    (blockX, blockZ) = shore.Value;
                }
            }

            TerrainTile tile = provider.GetTileAt(blockX, blockZ);
            int index = tile.Index(blockX - tile.BlockX, blockZ - tile.BlockZ);
            int y = Math.Min(settings.MapSizeY - 2, tile.SurfaceY[index] + 1);

            _pendingSpawn = (blockX, y, blockZ);
            _api.Logger.Notification("[{0}] Found a land spawn at ({1}, {2}, {3}), {4} m above sea level.",
                DiffusionPaths.ModId, blockX, y, blockZ, (int)tile.ElevationMeters[index]);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger, "The world spawn could not be placed on land.", e);
        }
    }

    /// <summary>Moves the save's spawn to the position <see cref="Record"/> chose, if any. Call at RunGame.</summary>
    public void ApplyPending()
    {
        if (_pendingSpawn == null) return;
        (int x, int y, int z) = _pendingSpawn.Value;
        _pendingSpawn = null;

        try
        {
            // A brand new save has no spawn record at all, and SetDefaultSpawnPosition assumes one
            // exists. The serverconfig command creates it, so go through that instead.
            _api.InjectConsole($"/serverconfig setspawn {x} {y} {z}");
            _api.Logger.Notification("[{0}] World spawn set to ({1}, {2}, {3}).", DiffusionPaths.ModId, x, y, z);
        }
        catch (Exception e)
        {
            throw DiffusionFailure.Fatal(_api.Logger, "The world spawn could not be moved to land.", e);
        }
    }

    /// <summary>
    /// Says whether the world's starting climate was honoured. A miss is a warning: the player picked
    /// a climate and is not getting it, and the reason - no land that cold or hot within reach of this
    /// seed's map centre - is not something they could work out from where they wake up.
    /// </summary>
    private void ReportStartingClimate(TerrainDiffusionProvider.SpawnCandidate spawn, DiffusionWorldSettings settings)
    {
        StartingClimate? band = settings.StartingClimate;
        if (band == null) return;

        int distance = (int)Math.Sqrt(
            Math.Pow(spawn.BlockX - settings.OriginBlockX, 2) + Math.Pow(spawn.BlockZ - settings.OriginBlockZ, 2));

        if (spawn.MatchedClimate)
        {
            _api.Logger.Notification(
                "[{0}] Starting climate {1}: spawning near sea level at about {2:0.#} C on the first day, {3} blocks from the map centre.",
                DiffusionPaths.ModId, band.Value, spawn.TemperatureC, distance);
            return;
        }

        _api.Logger.Warning(
            "[{0}] No {1} land within range of the map centre on this seed; spawning at about {2:0.#} C instead, " +
            "{3} blocks out. Raise worldGen.startingClimateSearchRadiusBlocks to look further, or try another seed.",
            DiffusionPaths.ModId, band.Value, spawn.TemperatureC, distance);
    }

    /// <summary>
    /// The nearest column to a spawn in a lake or river that is land above the sea and dry, searching
    /// outward ring by ring up to <see cref="ShoreSearchBlocks"/>.
    /// </summary>
    /// <returns>The column, or null if there is none in reach.</returns>
    private static (int x, int z)? NearestDryLand(int blockX, int blockZ, TerrainDiffusionProvider provider,
        DiffusionWorldSettings settings, WorldWater water)
    {
        TerrainTile tile = null;
        for (int ring = 1; ring <= ShoreSearchBlocks; ring++)
        {
            (int x, int z)? best = null;
            long bestDistance = long.MaxValue;
            for (int dz = -ring; dz <= ring; dz++)
            for (int dx = -ring; dx <= ring; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                long distance = (long)dx * dx + (long)dz * dz;
                if (distance >= bestDistance) continue;

                int x = blockX + dx, z = blockZ + dz;
                if (!settings.IsInsideWorld(x, z)) continue;
                provider.GetTileAt(x, z, ref tile);
                int index = tile.Index(x - tile.BlockX, z - tile.BlockZ);
                if (tile.ElevationMeters[index] <= 0f || tile.SurfaceY[index] < settings.SeaLevel) continue;
                if (water.IsWater(x, z)) continue;

                best = (x, z);
                bestDistance = distance;
            }
            if (best != null) return best;
        }
        return null;
    }
}
