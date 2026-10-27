using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;

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
public sealed class TerrainTileStore : IDisposable
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

    private readonly string _path;
    private readonly int _tileSize;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private SqliteConnection _connection;

    /// <summary>Tiles handed over but not yet on disk, by tile key.</summary>
    private readonly ConcurrentDictionary<long, TerrainTile> _pending = new();
    private readonly BlockingCollection<(long Key, TerrainTile Tile)> _queue = new();
    private readonly Thread _writer;

    private volatile bool _readFailed;
    private volatile bool _writeFailed;
    private long _loaded;
    private long _saved;
    private long _bytesSaved;
    private long _loadTicks;
    private long _saveTicks;
    private long _coarseLoaded;
    private long _coarseSaved;

    private TerrainTileStore(string path, int tileSize, SqliteConnection connection, ILogger logger)
    {
        _path = path;
        _tileSize = tileSize;
        _connection = connection;
        _logger = logger;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "TerrainDiffusion tile database" };
        _writer.Start();
    }

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

    /// <summary>
    /// One pipeline's coarse windows in this file. <paramref name="variant"/> keeps pipelines with
    /// different inputs apart: 0 for the main one, 1 for the river-free one behind the coarse sea
    /// test Rivers uses.
    /// </summary>
    public ICoarseWindowStore CoarseWindows(int variant) => new CoarseWindowTable(this, variant);

    private sealed class CoarseWindowTable : ICoarseWindowStore
    {
        private readonly TerrainTileStore _store;
        private readonly int _variant;

        public CoarseWindowTable(TerrainTileStore store, int variant)
        {
            _store = store;
            _variant = variant;
        }

        public float[] TryLoad(int windowI, int windowJ, int length) =>
            _store.TryLoadCoarse(CoarseKey(_variant, windowI, windowJ), length);

        public void Save(int windowI, int windowJ, float[] data) =>
            _store.SaveCoarse(CoarseKey(_variant, windowI, windowJ), data);
    }

    private static long CoarseKey(int variant, int windowI, int windowJ) =>
        ((long)variant << 48) | ((long)(windowI & 0xFFFFFF) << 24) | (uint)(windowJ & 0xFFFFFF);

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
    private void SaveCoarse(long key, float[] data)
    {
        if (_writeFailed) return;
        byte[] blob = TileCodec.EncodeWindow(data);
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

    private static long Key(int tileX, int tileZ) => ((long)tileX << 32) ^ (uint)tileZ;

    private static SqliteConnection Connect(string path)
    {
        // No pooling, so that closing the connection really releases the file and it can be deleted.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA journal_mode = WAL");
        Execute(connection, "PRAGMA synchronous = NORMAL");
        Execute(connection, "CREATE TABLE IF NOT EXISTS meta (name TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Execute(connection, "CREATE TABLE IF NOT EXISTS tiles (key INTEGER PRIMARY KEY, data BLOB NOT NULL)");
        Execute(connection, "CREATE TABLE IF NOT EXISTS coarse (key INTEGER PRIMARY KEY, data BLOB NOT NULL)");
        return connection;
    }

    private static Dictionary<string, string> ReadMeta(SqliteConnection connection)
    {
        var meta = new Dictionary<string, string>();
        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT name, value FROM meta";
        using SqliteDataReader reader = select.ExecuteReader();
        while (reader.Read()) meta[reader.GetString(0)] = reader.GetString(1);
        return meta;
    }

    private static void WriteMeta(SqliteConnection connection, IReadOnlyDictionary<string, string> meta)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM meta";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO meta (name, value) VALUES ($name, $value)";
        SqliteParameter name = command.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter value = command.Parameters.Add("$value", SqliteType.Text);
        foreach (KeyValuePair<string, string> entry in meta)
        {
            name.Value = entry.Key;
            value.Value = entry.Value;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IEnumerable<string> DatabaseFiles(string path) => new[] { path, path + "-wal", path + "-shm" };

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (string file in DatabaseFiles(path))
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}

/// <summary>
/// A terrain tile, or a coarse window, as bytes, losslessly. Each square plane of values is stored as
/// the bit patterns of its values, each minus its neighbour's (the one before, or above at the start
/// of a row), split into byte planes and compressed. Neighbouring values differ little, so the high
/// bytes of those differences are almost all zero and compress to nearly nothing.
/// </summary>
public static class TileCodec
{
    private const int Arrays = 7;
    private static readonly byte[] Magic = { (byte)'T', (byte)'D', (byte)'T' };
    private static readonly byte[] WindowMagic = { (byte)'T', (byte)'D', (byte)'W' };

    /// <summary>
    /// Brotli effort, 0 to 11. Measured on 140 stored 256-block tiles: 4 stores 506 KB of 1792 in
    /// 15 ms; 1 is 10% larger for 6 ms, and 8 saves 1% for 51 ms. Decoding is 7-10 ms at any of them.
    /// </summary>
    public static int Quality { get; set; } = 4;

    public static byte[] Encode(TerrainTile tile)
    {
        int size = tile.Size, plane = size * size;
        var raw = new byte[Arrays * plane * 4];
        var bits = new int[plane];
        for (int a = 0; a < Arrays; a++)
        {
            ReadBits(tile, a, bits);
            Split(bits, size, raw, a * plane * 4);
        }
        return Compress(Magic, size, raw);
    }

    public static TerrainTile Decode(byte[] blob, int blockX, int blockZ, int expectedSize)
    {
        int size = Header(blob, Magic);
        if (size != expectedSize) throw new InvalidDataException($"tile of {size} blocks where {expectedSize} were expected");

        int plane = size * size;
        byte[] raw = Decompress(blob, Arrays * plane * 4);
        var tile = new TerrainTile(blockX, blockZ, size);
        var bits = new int[plane];
        for (int a = 0; a < Arrays; a++)
        {
            Join(raw, a * plane * 4, size, bits);
            WriteBits(tile, a, bits);
        }
        return tile;
    }

    /// <summary>A coarse window: whole square planes of floats, channel after channel.</summary>
    public static byte[] EncodeWindow(float[] data)
    {
        int planes = 7;
        int size = (int)Math.Round(Math.Sqrt(data.Length / planes));
        if (planes * size * size != data.Length) throw new ArgumentException("not seven square planes", nameof(data));

        int plane = size * size;
        var raw = new byte[data.Length * 4];
        var bits = new int[plane];
        for (int p = 0; p < planes; p++)
        {
            for (int i = 0; i < plane; i++) bits[i] = BitConverter.SingleToInt32Bits(data[p * plane + i]);
            Split(bits, size, raw, p * plane * 4);
        }
        return Compress(WindowMagic, size, raw);
    }

    public static float[] DecodeWindow(byte[] blob, int expectedLength)
    {
        int size = Header(blob, WindowMagic);
        int plane = size * size;
        if (7 * plane != expectedLength)
            throw new InvalidDataException($"coarse window of {7 * plane} values where {expectedLength} were expected");

        byte[] raw = Decompress(blob, expectedLength * 4);
        var data = new float[expectedLength];
        var bits = new int[plane];
        for (int p = 0; p < 7; p++)
        {
            Join(raw, p * plane * 4, size, bits);
            for (int i = 0; i < plane; i++) data[p * plane + i] = BitConverter.Int32BitsToSingle(bits[i]);
        }
        return data;
    }

    /// <summary>One plane's differences, as four byte planes at <paramref name="offset"/>.</summary>
    private static void Split(int[] bits, int size, byte[] raw, int offset)
    {
        int plane = bits.Length;
        for (int i = 0; i < plane; i++)
        {
            int previous = i == 0 ? 0 : i % size == 0 ? bits[i - size] : bits[i - 1];
            int delta = unchecked(bits[i] - previous);
            raw[offset + i] = (byte)delta;
            raw[offset + plane + i] = (byte)(delta >> 8);
            raw[offset + 2 * plane + i] = (byte)(delta >> 16);
            raw[offset + 3 * plane + i] = (byte)(delta >> 24);
        }
    }

    /// <summary>The inverse of <see cref="Split"/>.</summary>
    private static void Join(byte[] raw, int offset, int size, int[] bits)
    {
        int plane = bits.Length;
        for (int i = 0; i < plane; i++)
        {
            int delta = raw[offset + i] | raw[offset + plane + i] << 8 |
                        raw[offset + 2 * plane + i] << 16 | raw[offset + 3 * plane + i] << 24;
            int previous = i == 0 ? 0 : i % size == 0 ? bits[i - size] : bits[i - 1];
            bits[i] = unchecked(previous + delta);
        }
    }

    private static byte[] Compress(byte[] magic, int size, byte[] raw)
    {
        using var output = new MemoryStream(raw.Length / 4);
        output.Write(magic);
        output.WriteByte((byte)TerrainTileStore.FormatVersion);
        output.Write(BitConverter.GetBytes(size));
        using (var brotli = new BrotliStream(output, new BrotliCompressionOptions { Quality = Quality }, leaveOpen: true))
        {
            brotli.Write(raw);
        }
        return output.ToArray();
    }

    /// <summary>The edge length a blob records, once its magic and format are checked.</summary>
    private static int Header(byte[] blob, byte[] magic)
    {
        if (blob.Length < 8 || blob[0] != magic[0] || blob[1] != magic[1] || blob[2] != magic[2] ||
            blob[3] != TerrainTileStore.FormatVersion)
        {
            throw new InvalidDataException("not stored terrain of this format");
        }
        return BitConverter.ToInt32(blob, 4);
    }

    private static byte[] Decompress(byte[] blob, int length)
    {
        var raw = new byte[length];
        using var brotli = new BrotliStream(new MemoryStream(blob, 8, blob.Length - 8), CompressionMode.Decompress);
        brotli.ReadExactly(raw);
        return raw;
    }

    private static void ReadBits(TerrainTile tile, int array, int[] bits)
    {
        if (array == 1)
        {
            Array.Copy(tile.SurfaceY, bits, bits.Length);
            return;
        }
        float[] values = Floats(tile, array);
        for (int i = 0; i < bits.Length; i++) bits[i] = BitConverter.SingleToInt32Bits(values[i]);
    }

    private static void WriteBits(TerrainTile tile, int array, int[] bits)
    {
        if (array == 1)
        {
            Array.Copy(bits, tile.SurfaceY, bits.Length);
            return;
        }
        float[] values = Floats(tile, array);
        for (int i = 0; i < bits.Length; i++) values[i] = BitConverter.Int32BitsToSingle(bits[i]);
    }

    private static float[] Floats(TerrainTile tile, int array) => array switch
    {
        0 => tile.ElevationMeters,
        2 => tile.Slope,
        3 => tile.TemperatureC,
        4 => tile.TemperatureSeasonality,
        5 => tile.PrecipitationMm,
        6 => tile.PrecipitationCv,
        _ => throw new ArgumentOutOfRangeException(nameof(array))
    };
}
