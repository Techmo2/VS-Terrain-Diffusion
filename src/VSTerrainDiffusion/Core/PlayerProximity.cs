using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VSTerrainDiffusion.Core;

/// <summary>Where the online players are, for ordering and pruning work by distance to them.</summary>
public static class PlayerProximity
{
    /// <summary>
    /// Chebyshev distance in blocks from a column to the nearest online player. Chebyshev rather than
    /// squared Euclidean, which would overflow at map scale; it is monotone in what matters.
    /// </summary>
    /// <param name="world">The world, or null.</param>
    /// <param name="blockX">Column X.</param>
    /// <param name="blockZ">Column Z.</param>
    /// <returns>The distance, or <see cref="long.MaxValue"/> when nobody with an entity is online.</returns>
    public static long NearestPlayerDistance(IWorldAccessor world, int blockX, int blockZ)
    {
        IPlayer[] players = world?.AllOnlinePlayers;
        long best = long.MaxValue;
        if (players == null) return best;

        foreach (IPlayer player in players)
        {
            Entity entity = player?.Entity;
            if (entity == null) continue;
            long dx = Math.Abs((long)entity.Pos.X - blockX);
            long dz = Math.Abs((long)entity.Pos.Z - blockZ);
            best = Math.Min(best, Math.Max(dx, dz));
        }
        return best;
    }

    /// <summary>
    /// How urgent work at a column is: its distance to the nearest player, or 0 when nobody is
    /// connected, because during world creation everything is equally urgent.
    /// </summary>
    /// <param name="world">The world, or null.</param>
    /// <param name="blockX">Column X.</param>
    /// <param name="blockZ">Column Z.</param>
    /// <returns>Lower is more urgent.</returns>
    public static long Urgency(IWorldAccessor world, int blockX, int blockZ)
    {
        long distance = NearestPlayerDistance(world, blockX, blockZ);
        return distance == long.MaxValue ? 0 : distance;
    }

    /// <summary>Every online player's name and block position, for map markers.</summary>
    /// <param name="world">The world, or null.</param>
    public static IReadOnlyList<(string Name, int X, int Z)> Markers(IWorldAccessor world)
    {
        var markers = new List<(string, int, int)>();
        IPlayer[] players = world?.AllOnlinePlayers;
        if (players == null) return markers;

        foreach (IPlayer player in players)
        {
            Entity entity = player?.Entity;
            if (entity == null) continue;
            markers.Add((player.PlayerName ?? "?", (int)entity.Pos.X, (int)entity.Pos.Z));
        }
        return markers;
    }
}
