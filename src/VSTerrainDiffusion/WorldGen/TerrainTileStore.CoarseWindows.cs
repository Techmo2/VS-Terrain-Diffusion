using System;
using System.Threading;
using Microsoft.Data.Sqlite;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>The coarse model's windows, in a table of their own beside the tiles.</summary>
public sealed partial class TerrainTileStore
{
    /// <summary>
    /// One pipeline's coarse windows in this file. <paramref name="variant"/> keeps pipelines with
    /// different inputs apart: 0 for the main one, 1 for the river-free one behind the coarse sea
    /// test Rivers uses.
    /// </summary>
    public ICoarseWindowStore CoarseWindows(int variant) => new CoarseWindowTable(this, variant);

    /// <summary>
    /// One pipeline's view of the coarse table: its windows, keyed apart from other pipelines' by variant.
    /// </summary>
    private sealed class CoarseWindowTable : ICoarseWindowStore
    {
        /// <summary>The store holding the table.</summary>
        private readonly TerrainTileStore _store;

        /// <summary>Which pipeline's windows these are.</summary>
        private readonly int _variant;

        /// <summary>Creates the view.</summary>
        /// <param name="store">The store.</param>
        /// <param name="variant">Which pipeline.</param>
        public CoarseWindowTable(TerrainTileStore store, int variant)
        {
            _store = store;
            _variant = variant;
        }

        /// <summary>The stored window, or null.</summary>
        /// <param name="windowI">Window column.</param>
        /// <param name="windowJ">Window row.</param>
        /// <param name="length">Values expected.</param>
        /// <returns>The window's values, or null.</returns>
        public float[] TryLoad(int windowI, int windowJ, int length) =>
            _store.TryLoadCoarse(CoarseKey(_variant, windowI, windowJ), length);

        /// <summary>Stores a window.</summary>
        /// <param name="windowI">Window column.</param>
        /// <param name="windowJ">Window row.</param>
        /// <param name="data">The values.</param>
        /// <param name="planes">How many square planes they make.</param>
        public void Save(int windowI, int windowJ, float[] data, int planes) =>
            _store.SaveCoarse(CoarseKey(_variant, windowI, windowJ), data, planes);
    }

    /// <summary>A window's row key: variant in the top 16 bits, then 24 bits each of I and J.</summary>
    /// <param name="variant">Which pipeline.</param>
    /// <param name="windowI">Window column.</param>
    /// <param name="windowJ">Window row.</param>
    /// <returns>The key.</returns>
    private static long CoarseKey(int variant, int windowI, int windowJ) =>
        ((long)variant << 48) | ((long)(windowI & 0xFFFFFF) << 24) | (uint)(windowJ & 0xFFFFFF);

    /// <summary>Reads and decodes a window. A read error stops all reading for the session.</summary>
    /// <param name="key">The window's key.</param>
    /// <param name="length">Values expected.</param>
    /// <returns>The values, or null when missing or unreadable.</returns>
    private float[] TryLoadCoarse(long key, int length)
    {
        if (_readFailed) return null;
        try
        {
            byte[] blob;
            lock (_gate)
            {
                if (_connection == null) return null;
                using SqliteCommand select = _connection.CreateCommand();
                select.CommandText = "SELECT data FROM coarse WHERE key = $key";
                select.Parameters.AddWithValue("$key", key);
                blob = select.ExecuteScalar() as byte[];
            }
            if (blob == null) return null;

            float[] data = TileCodec.DecodeWindow(blob, length);
            Interlocked.Increment(ref _coarseLoaded);
            return data;
        }
        catch (Exception e)
        {
            _readFailed = true;
            _logger.Warning("[{0}] The tile database could not be read; the model will run instead: {1}",
                DiffusionPaths.ModId, e.Message);
            return null;
        }
    }

    /// <summary>
    /// Written at once rather than queued: a window is rare, small, and comes from inside the
    /// pipeline, which has just spent far longer computing it.
    /// </summary>
    private void SaveCoarse(long key, float[] data, int planes)
    {
        if (_writeFailed) return;
        byte[] blob = TileCodec.EncodeWindow(data, planes);
        try
        {
            lock (_gate)
            {
                if (_connection == null) return;
                using SqliteCommand insert = _connection.CreateCommand();
                insert.CommandText = "INSERT OR REPLACE INTO coarse (key, data) VALUES ($key, $data)";
                insert.Parameters.AddWithValue("$key", key);
                insert.Parameters.Add("$data", SqliteType.Blob).Value = blob;
                insert.ExecuteNonQuery();
            }
            Interlocked.Increment(ref _coarseSaved);
        }
        catch (Exception e)
        {
            _writeFailed = true;
            _logger.Warning(
                "[{0}] The tile database could not be written, so terrain generated from now on will not be kept: {1}",
                DiffusionPaths.ModId, e.Message);
        }
    }
}
