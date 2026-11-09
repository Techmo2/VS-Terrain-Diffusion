using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.Debug;

/// <summary>
/// The HTTP side: the listener thread, the request router and the page, players, info and tiles
/// responses.
/// </summary>
public sealed partial class DebugMapServer
{
    /// <summary>
    /// The listener thread: answers requests one at a time until <see cref="Dispose"/> stops the listener. A
    /// failed request is logged and aborted, never fatal.
    /// </summary>
    private void Serve()
    {
        while (!_stopping)
        {
            HttpListenerContext context;
            try
            {
                context = _listener.GetContext();
            }
            catch (Exception)
            {
                // Stop() disposes the listener out from under GetContext; that is the exit path.
                return;
            }

            try
            {
                Handle(context);
            }
            catch (Exception e)
            {
                _log.Warning("[{0}] Debug map request failed: {1}", DiffusionPaths.ModId, e.Message);
                try { context.Response.Abort(); } catch { /* the client went away */ }
            }
        }
    }

    /// <summary>
    /// Routes one request by path: the page, <c>/api/info</c>, <c>/api/tiles</c> or <c>/api/players</c>.
    /// </summary>
    private void Handle(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";

        switch (path)
        {
            case "/":
            case "/index.html":
                WritePage(context);
                return;
            case "/favicon.ico":
                // The browser asks unprompted; answering keeps a 404 out of the console.
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            case "/api/info":
                WriteInfo(context);
                return;
            case "/api/tiles":
                WriteTiles(context);
                return;
            case "/api/players":
                WritePlayers(context);
                return;
            default:
                context.Response.StatusCode = 404;
                Write(context, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"));
                return;
        }
    }

    /// <summary>Sends the map page, embedded in the assembly as <c>map.html</c>.</summary>
    private void WritePage(HttpListenerContext context)
    {
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("VSTerrainDiffusion.Debug.map.html");

        if (stream == null)
        {
            context.Response.StatusCode = 500;
            Write(context, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("map.html is missing from the build"));
            return;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Write(context, "text/html; charset=utf-8", buffer.ToArray());
    }

    /// <summary>
    /// Where each player is, in world blocks. Polled far more often than it changes, so it is kept
    /// small and never cached.
    /// </summary>
    private void WritePlayers(HttpListenerContext context)
    {
        var json = new StringBuilder();
        json.Append('[');

        try
        {
            IReadOnlyList<(string Name, int X, int Z)> players = _players?.Invoke();
            for (int i = 0; players != null && i < players.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append($"{{\"name\":\"{Escape(players[i].Name)}\",\"x\":{players[i].X},\"z\":{players[i].Z}}}");
            }
        }
        catch (Exception)
        {
            // Somebody joining or leaving mid-read is not worth breaking the map over.
        }

        json.Append(']');
        context.Response.Headers["Cache-Control"] = "no-store";
        Write(context, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json.ToString()));
    }

    /// <summary>
    /// Sends the world, the layer list and the history's state as JSON, for the page to set itself up.
    /// </summary>
    private void WriteInfo(HttpListenerContext context)
    {
        var json = new StringBuilder();
        json.Append('{');
        json.Append($"\"tileSize\":{_provider.TileSize},");
        json.Append($"\"thumbSize\":{ThumbSize},");
        json.Append($"\"seq\":{Interlocked.Read(ref _sequence)},");
        json.Append($"\"tiles\":{_tiles.Count},");
        json.Append($"\"historyLimit\":{_historyLimit},");
        json.Append($"\"tilesGenerated\":{_provider.TilesGenerated},");
        json.Append($"\"averageTileMillis\":{_provider.AverageTileMillis},");
        json.Append($"\"metersPerBlock\":{Fixed(_settings.MetersPerBlock)},");
        json.Append($"\"metersPerBlockVertical\":{Fixed(_settings.MetersPerBlockVertical)},");
        json.Append($"\"seaLevel\":{_settings.SeaLevel},");
        json.Append($"\"mapSizeY\":{_settings.MapSizeY},");
        json.Append("\"layers\":[");
        for (int i = 0; i < _layers.Length; i++)
        {
            if (i > 0) json.Append(',');
            json.Append($"{{\"id\":\"{_layers[i].Id}\",\"label\":\"{Escape(_layers[i].Label)}\","
                        + $"\"unit\":\"{Escape(_layers[i].Unit)}\",\"category\":\"{Escape(_layers[i].Category)}\""
                        + (_layers[i].Categorical ? ",\"categorical\":true" : "") + "}");
        }
        json.Append("],");

        // The classes a categorical layer's bytes index, with their usual map colours; sea last.
        json.Append($"\"climateModel\":\"{_provider.CoarseSpec.Code}\",");
        json.Append("\"koppen\":[");
        for (int i = 0; i < Api.Koppen.Count; i++)
        {
            Api.Koppen.ClassInfo c = Api.Koppen.Classes[i];
            json.Append($"{{\"code\":\"{c.Code}\",\"name\":\"{Escape(c.Name)}\",\"rgb\":\"#{c.Rgb:x6}\"}},");
        }
        json.Append("{\"code\":\"sea\",\"name\":\"Sea\",\"rgb\":\"#1c3a5e\"}]}");

        context.Response.Headers["Cache-Control"] = "no-store";
        Write(context, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json.ToString()));
    }

    /// <summary>
    /// Everything recorded since the client's last sequence number, as one binary blob:
    /// magic, the server's sequence, a tile count, then per tile its grid coordinates, a min and a
    /// max per layer, and the quantised bytes.
    /// </summary>
    private void WriteTiles(HttpListenerContext context)
    {
        long since = 0;
        string raw = context.Request.QueryString["since"];
        if (raw != null) long.TryParse(raw, out since);

        RecordedTile[] pending = _tiles.Values
            .Where(t => t.Seq > since)
            .OrderBy(t => t.Seq)
            .ToArray();

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(new[] { (byte)'T', (byte)'D', (byte)'M', (byte)'P' });
            writer.Write(Interlocked.Read(ref _sequence));
            writer.Write(pending.Length);

            foreach (RecordedTile tile in pending)
            {
                writer.Write(tile.X);
                writer.Write(tile.Z);
                for (int i = 0; i < _layers.Length; i++)
                {
                    writer.Write(tile.Min[i]);
                    writer.Write(tile.Max[i]);
                }
                writer.Write(tile.Data);
            }
        }

        context.Response.Headers["Cache-Control"] = "no-store";
        Write(context, "application/octet-stream", buffer.ToArray());
    }

    /// <summary>Sends a complete response and closes it.</summary>
    /// <param name="context">The request.</param>
    /// <param name="contentType">The MIME type.</param>
    /// <param name="body">The response body.</param>
    private static void Write(HttpListenerContext context, string contentType, byte[] body)
    {
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.OutputStream.Close();
    }

    /// <summary>A float for JSON: invariant culture, four decimals at most, non-finite as 0.</summary>
    private static string Fixed(float value) =>
        float.IsNaN(value) || float.IsInfinity(value)
            ? "0"
            : value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Escapes backslashes and quotes for a JSON string.</summary>
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
