using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// A world's finished terrain tiles on disk, so that ground already modelled once - for chunks,
/// for another mod's height queries, for a translocator search - is read back instead of run
/// through the model again. Optional, per world (<c>terraindiffusionTileDatabase</c>), unbounded.
///
/// One SQLite file per world, through the copy of Microsoft.Data.Sqlite the game itself ships.
/// Tiles are stored exactly: a tile read back is bit for bit the one that was generated.
///
/// The file also records what the tiles were generated under - see <see cref="Open"/> - and is
/// emptied when any of it has changed, because a tile that disagrees with what this setup would
/// generate now would put a seam wherever the stored ground meets the new.
///
/// Writes go through a background thread, so generating terrain never waits on the disk; a tile
/// still queued is served from the queue.
///
/// The coarse model's windows are kept too (<see cref="CoarseWindows"/>), in a table of their own
/// under the same fingerprint: a few hundred kilobytes for a 32 km window, so ground any distance
/// from the tiles can be answered without the model.
/// </summary>
public sealed partial class TerrainTileStore : IDisposable
{
    /// <summary>The layout of a stored tile. Raise it whenever <see cref="TileCodec"/> changes.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// What a tile contains, as far as this mod's own code decides it. Raise it with any change that
    /// alters generated terrain for the same settings and models - the pipeline, the post-processing
    /// a tile goes through, how heights or climate are derived - and every world's stored tiles are
    /// emptied on the next load. Left alone, an update keeps them, so a world pregenerated on one
    /// version is still served after the mod is updated.
    /// </summary>
    public const int TerrainVersion = 1;

    /// <summary>Tiles written per transaction, at most.</summary>
    private const int WriteBatch = 16;

    /// <summary>The database file.</summary>
    private readonly string _path;

    /// <summary>Edge length of the tiles held, in blocks.</summary>
    private readonly int _tileSize;

    /// <summary>The server log.</summary>
    private readonly ILogger _logger;

    /// <summary>Serialises use of <see cref="_connection"/>.</summary>
    private readonly object _gate = new();

    /// <summary>The open database, or null once disposed.</summary>
    private SqliteConnection _connection;

    /// <summary>Tiles handed over but not yet on disk, by tile key.</summary>
    private readonly ConcurrentDictionary<long, TerrainTile> _pending = new();

    /// <summary>Tiles waiting for <see cref="_writer"/>.</summary>
    private readonly BlockingCollection<(long Key, TerrainTile Tile)> _queue = new();

    /// <summary>Writes queued tiles in batches.</summary>
    private readonly Thread _writer;

    /// <summary>Set by the first read error; the database is not read again this session.</summary>
    private volatile bool _readFailed;

    /// <summary>Set by the first write error; nothing more is written this session.</summary>
    private volatile bool _writeFailed;

    /// <summary>Tiles read this session.</summary>
    private long _loaded;

    /// <summary>Tiles written this session.</summary>
    private long _saved;

    /// <summary>Encoded bytes written this session.</summary>
    private long _bytesSaved;

    /// <summary>Stopwatch ticks spent reading and decoding.</summary>
    private long _loadTicks;

    /// <summary>Stopwatch ticks spent encoding and writing.</summary>
    private long _saveTicks;

    /// <summary>Coarse windows read this session.</summary>
    private long _coarseLoaded;

    /// <summary>Coarse windows written this session.</summary>
    private long _coarseSaved;

    /// <summary>Wraps an opened database and starts the writer.</summary>
    /// <param name="path">The file.</param>
    /// <param name="tileSize">Edge length of the tiles held.</param>
    /// <param name="connection">The open, checked connection, now owned by the store.</param>
    /// <param name="logger">The server log.</param>
    private TerrainTileStore(string path, int tileSize, SqliteConnection connection, ILogger logger)
    {
        _path = path;
        _tileSize = tileSize;
        _connection = connection;
        _logger = logger;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "TerrainDiffusion tile database" };
        _writer.Start();
    }

    /// <summary>The database file.</summary>
    public string Path => _path;

    /// <summary>Tiles read from disk this session.</summary>
    public long TilesLoaded => Interlocked.Read(ref _loaded);

    /// <summary>Tiles written to disk this session.</summary>
    public long TilesSaved => Interlocked.Read(ref _saved);

    /// <summary>Tiles generated but not yet written.</summary>
    public int TilesPending => _pending.Count;

    /// <summary>Coarse windows read from disk this session.</summary>
    public long CoarseWindowsLoaded => Interlocked.Read(ref _coarseLoaded);

    /// <summary>Coarse windows written to disk this session.</summary>
    public long CoarseWindowsSaved => Interlocked.Read(ref _coarseSaved);

    /// <summary>Mean stored size of the tiles written this session, in bytes.</summary>
    public long AverageTileBytes => TilesSaved == 0 ? 0 : Interlocked.Read(ref _bytesSaved) / TilesSaved;

    /// <summary>Mean time to read and decode a tile, in milliseconds.</summary>
    public double AverageLoadMillis =>
        TilesLoaded == 0 ? 0 : Interlocked.Read(ref _loadTicks) * 1000.0 / Stopwatch.Frequency / TilesLoaded;

    /// <summary>Mean time to encode and write a tile, in milliseconds.</summary>
    public double AverageSaveMillis =>
        TilesSaved == 0 ? 0 : Interlocked.Read(ref _saveTicks) * 1000.0 / Stopwatch.Frequency / TilesSaved;

    /// <summary>
    /// Opens or creates the database at <paramref name="path"/> for tiles of
    /// <paramref name="tileSize"/> blocks.
    ///
    /// <paramref name="fingerprint"/> names everything that decides what a tile contains, one entry
    /// per input (the seed, the world's settings, the mods...), each a hash of that input. A file
    /// recorded under a different fingerprint is emptied, and the inputs that changed are logged.
    /// </summary>
    public static TerrainTileStore Open(string path, int tileSize, IReadOnlyDictionary<string, string> fingerprint,
                                        ILogger logger)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var expected = new Dictionary<string, string>(fingerprint)
        {
            ["format"] = FormatVersion.ToString(),
            ["tilesize"] = tileSize.ToString()
        };

        SqliteConnection connection = Connect(path);
        try
        {
            Dictionary<string, string> recorded = ReadMeta(connection);
            long count = Scalar(connection, "SELECT COUNT(*) FROM tiles");
            long coarse = Scalar(connection, "SELECT COUNT(*) FROM coarse");

            List<string> changed = expected.Keys.Union(recorded.Keys)
                .Where(key => !(expected.TryGetValue(key, out string a) && recorded.TryGetValue(key, out string b) && a == b))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            if (changed.Count > 0)
            {
                if (count > 0 || coarse > 0)
                {
                    // Deleting the file gives the space back at once; emptying the table would not.
                    connection.Dispose();
                    DeleteDatabaseFiles(path);
                    connection = Connect(path);
                    logger.Notification(
                        "[{0}] Emptied the tile database ({1} tiles, {2} coarse windows): they were generated " +
                        "under different {3}.", DiffusionPaths.ModId, count, coarse, string.Join(", ", changed));
                    count = 0;
                    coarse = 0;
                }
                WriteMeta(connection, expected);
            }

            logger.Notification("[{0}] Tile database: {1} tiles and {2} coarse windows stored in {3}",
                DiffusionPaths.ModId, count, coarse, path);
            return new TerrainTileStore(path, tileSize, connection, logger);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>The stored tile at tile coordinates (<paramref name="tileX"/>, <paramref name="tileZ"/>), or null.</summary>
    public TerrainTile TryLoad(int tileX, int tileZ)
    {
        long key = Key(tileX, tileZ);
        if (_pending.TryGetValue(key, out TerrainTile queued)) return queued;
        if (_readFailed) return null;

        long start = Stopwatch.GetTimestamp();
        byte[] blob;
        try
        {
            lock (_gate)
            {
                if (_connection == null) return null;
                using SqliteCommand select = _connection.CreateCommand();
                select.CommandText = "SELECT data FROM tiles WHERE key = $key";
                select.Parameters.AddWithValue("$key", key);
                blob = select.ExecuteScalar() as byte[];
            }
            if (blob == null) return null;

            TerrainTile tile = TileCodec.Decode(blob, tileX * _tileSize, tileZ * _tileSize, _tileSize);
            Interlocked.Add(ref _loadTicks, Stopwatch.GetTimestamp() - start);
            Interlocked.Increment(ref _loaded);
            return tile;
        }
        catch (Exception e)
        {
            // Generating the tile again is always possible, so a database that cannot be read costs
            // time, not terrain.
            _readFailed = true;
            _logger.Warning("[{0}] The tile database could not be read; tiles will be generated instead: {1}",
                DiffusionPaths.ModId, e.Message);
            return null;
        }
    }

    /// <summary>Whether a tile is stored or queued to be, without reading it.</summary>
    public bool Contains(int tileX, int tileZ)
    {
        long key = Key(tileX, tileZ);
        if (_pending.ContainsKey(key)) return true;
        if (_readFailed) return false;

        try
        {
            lock (_gate)
            {
                if (_connection == null) return false;
                using SqliteCommand select = _connection.CreateCommand();
                select.CommandText = "SELECT EXISTS(SELECT 1 FROM tiles WHERE key = $key)";
                select.Parameters.AddWithValue("$key", key);
                return Convert.ToInt64(select.ExecuteScalar()) != 0;
            }
        }
        catch (Exception e)
        {
            _readFailed = true;
            _logger.Warning("[{0}] The tile database could not be read; tiles will be generated instead: {1}",
                DiffusionPaths.ModId, e.Message);
            return false;
        }
    }

    /// <summary>
    /// The tile size the database at <paramref name="path"/> holds tiles of, or null when there is
    /// no such file, it holds no tiles, or it cannot be read. A world keeps the size its tiles were
    /// made at: the automatic size differs between GPU and CPU, and a database moved from one
    /// machine to the other would otherwise be emptied for it.
    /// </summary>
    public static int? ReadTileSize(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM tiles)";
            if (Convert.ToInt64(command.ExecuteScalar()) == 0) return null;
            command.CommandText = "SELECT value FROM meta WHERE name = 'tilesize'";
            return int.TryParse(command.ExecuteScalar() as string, out int size) && size > 0 ? size : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Queues a freshly generated tile to be written.</summary>
    public void Save(int tileX, int tileZ, TerrainTile tile)
    {
        if (_writeFailed || _queue.IsAddingCompleted) return;

        long key = Key(tileX, tileZ);
        _pending[key] = tile;
        try
        {
            _queue.Add((key, tile));
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
            _pending.TryRemove(new KeyValuePair<long, TerrainTile>(key, tile));
        }
    }

    /// <summary>How many tiles the file holds.</summary>
    public long CountTiles()
    {
        lock (_gate)
        {
            return _connection == null ? 0 : Scalar(_connection, "SELECT COUNT(*) FROM tiles");
        }
    }

    /// <summary>How many coarse windows the file holds.</summary>
    public long CountCoarseWindows()
    {
        lock (_gate)
        {
            return _connection == null ? 0 : Scalar(_connection, "SELECT COUNT(*) FROM coarse");
        }
    }

    /// <summary>The file's size on disk, write-ahead log included.</summary>
    public long SizeOnDisk()
    {
        long total = 0;
        foreach (string file in DatabaseFiles(_path))
        {
            try
            {
                if (File.Exists(file)) total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
        }
        return total;
    }

    /// <summary>
    /// Removes every stored tile, and the coarse windows too when <paramref name="coarse"/>, and gives
    /// the space back. Returns how many tiles went. Tiles still queued are written after.
    /// </summary>
    public long Clear(bool coarse)
    {
        lock (_gate)
        {
            if (_connection == null) return 0;
            long count = Scalar(_connection, "SELECT COUNT(*) FROM tiles");
            Execute(_connection, "DELETE FROM tiles");
            if (coarse) Execute(_connection, "DELETE FROM coarse");
            Execute(_connection, "VACUUM");
            Execute(_connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            return count;
        }
    }

    /// <summary>
    /// The writer thread: takes up to <see cref="WriteBatch"/> queued tiles at a time, encodes them and
    /// writes them in one transaction. A write error stops all writing for the session; the tiles stay
    /// generated, just not kept.
    /// </summary>
    private void WriteLoop()
    {
        var batch = new List<(long Key, TerrainTile Tile)>(WriteBatch);
        var encoded = new List<(long Key, byte[] Blob)>(WriteBatch);
        try
        {
            foreach ((long Key, TerrainTile Tile) first in _queue.GetConsumingEnumerable())
            {
                batch.Clear();
                batch.Add(first);
                while (batch.Count < WriteBatch && _queue.TryTake(out (long, TerrainTile) next)) batch.Add(next);

                if (_writeFailed)
                {
                    foreach ((long key, TerrainTile tile) in batch)
                        _pending.TryRemove(new KeyValuePair<long, TerrainTile>(key, tile));
                    continue;
                }

                long start = Stopwatch.GetTimestamp();
                encoded.Clear();
                foreach ((long key, TerrainTile tile) in batch) encoded.Add((key, TileCodec.Encode(tile)));

                try
                {
                    lock (_gate)
                    {
                        if (_connection == null) return;
                        using SqliteTransaction transaction = _connection.BeginTransaction();
                        using SqliteCommand insert = _connection.CreateCommand();
                        insert.Transaction = transaction;
                        insert.CommandText = "INSERT OR REPLACE INTO tiles (key, data) VALUES ($key, $data)";
                        SqliteParameter keyParameter = insert.Parameters.Add("$key", SqliteType.Integer);
                        SqliteParameter dataParameter = insert.Parameters.Add("$data", SqliteType.Blob);
                        foreach ((long key, byte[] blob) in encoded)
                        {
                            keyParameter.Value = key;
                            dataParameter.Value = blob;
                            insert.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }

                    Interlocked.Add(ref _saveTicks, Stopwatch.GetTimestamp() - start);
                    Interlocked.Add(ref _saved, encoded.Count);
                    foreach ((long _, byte[] blob) in encoded) Interlocked.Add(ref _bytesSaved, blob.Length);
                }
                catch (Exception e)
                {
                    _writeFailed = true;
                    _logger.Warning(
                        "[{0}] The tile database could not be written, so tiles generated from now on will not be kept: {1}",
                        DiffusionPaths.ModId, e.Message);
                }
                finally
                {
                    foreach ((long key, TerrainTile tile) in batch)
                        _pending.TryRemove(new KeyValuePair<long, TerrainTile>(key, tile));
                }
            }
        }
        catch (Exception e)
        {
            _writeFailed = true;
            _logger.Warning("[{0}] The tile database writer stopped: {1}", DiffusionPaths.ModId, e.Message);
        }
    }

    /// <summary>Writes out everything queued, then closes the file.</summary>
    public void Dispose()
    {
        if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
        if (!_writer.Join(TimeSpan.FromSeconds(60)))
        {
            _logger.Warning("[{0}] The tile database was closed with {1} tiles still unwritten; they will be generated again.",
                DiffusionPaths.ModId, _pending.Count);
        }

        lock (_gate)
        {
            if (_connection == null) return;
            try
            {
                Execute(_connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            }
            catch (Exception)
            {
                // The log is replayed on the next open.
            }
            _connection.Dispose();
            _connection = null;
        }
    }

    /// <summary>A tile's row key: X in the high half, Z in the low.</summary>
    /// <param name="tileX">Tile column.</param>
    /// <param name="tileZ">Tile row.</param>
    /// <returns>The key.</returns>
    private static long Key(int tileX, int tileZ) => ((long)tileX << 32) ^ (uint)tileZ;
}
