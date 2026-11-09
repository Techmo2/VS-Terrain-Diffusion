using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>The SQLite file itself: opening it, its tables, and the fingerprint kept in <c>meta</c>.</summary>
public sealed partial class TerrainTileStore
{
    /// <summary>Opens or creates the file, in WAL mode, with its three tables.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The open connection.</returns>
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

    /// <summary>The fingerprint recorded in the file.</summary>
    /// <param name="connection">The open database.</param>
    /// <returns>Name to value.</returns>
    private static Dictionary<string, string> ReadMeta(SqliteConnection connection)
    {
        var meta = new Dictionary<string, string>();
        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT name, value FROM meta";
        using SqliteDataReader reader = select.ExecuteReader();
        while (reader.Read()) meta[reader.GetString(0)] = reader.GetString(1);
        return meta;
    }

    /// <summary>Replaces the recorded fingerprint.</summary>
    /// <param name="connection">The open database.</param>
    /// <param name="meta">Name to value.</param>
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

    /// <summary>Runs a query that returns one integer.</summary>
    /// <param name="connection">The open database.</param>
    /// <param name="sql">The query.</param>
    /// <returns>Its value.</returns>
    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Runs a statement.</summary>
    /// <param name="connection">The open database.</param>
    /// <param name="sql">The statement.</param>
    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>The database file and its WAL and shared-memory companions.</summary>
    /// <param name="path">The database file.</param>
    /// <returns>The three paths.</returns>
    private static IEnumerable<string> DatabaseFiles(string path) => new[] { path, path + "-wal", path + "-shm" };

    /// <summary>Deletes the database and its companions; the connection must be closed.</summary>
    /// <param name="path">The database file.</param>
    private static void DeleteDatabaseFiles(string path)
    {
        foreach (string file in DatabaseFiles(path))
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
