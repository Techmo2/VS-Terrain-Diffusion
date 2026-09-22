using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Pipeline;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.WorldGen;

namespace VSTerrainDiffusion.Debug;

/// <summary>
/// A small read-only HTTP server that shows the model's heightmap and climate maps as the world
/// generates, for diagnosing what the model is actually producing.
///
/// It records a thumbnail of every terrain tile at the moment it is generated rather than reading
/// the provider's cache, because that cache is an LRU sized for world generation and drops tiles
/// as soon as the generator has moved on - which is exactly when you want to look at them. The
/// thumbnails are quantised to a byte per column per layer with the tile's own range alongside, so
/// remembering thousands of tiles costs megabytes rather than hundreds of them; the browser
/// dequantises back to real values for the colour ramp and the readout.
///
/// Off unless <see cref="DiffusionConfig.DebugMapPort"/> is set, and bound to loopback unless the
/// bind address is changed. Nothing here accepts input or mutates the world.
/// </summary>
public sealed class DebugMapServer : IDisposable
{
    /// <summary>Columns per thumbnail edge. One byte per column per layer.</summary>
    public const int ThumbSize = 32;

    private readonly ILogger _log;
    private readonly IRiverBasinSource _riverBasins;
    private readonly TerrainDiffusionProvider _provider;
    private readonly DiffusionWorldSettings _settings;
    private readonly Layer[] _layers;
    private readonly int _historyLimit;

    private readonly ConcurrentDictionary<long, RecordedTile> _tiles = new();
    private long _sequence;

    private HttpListener _listener;
    private Thread _thread;
    private volatile bool _stopping;

    public string Url { get; private set; }

    /// <summary>Number of tiles currently remembered.</summary>
    public int TileCount => _tiles.Count;

    /// <summary>Whether the listener is up.</summary>
    public bool IsRunning => _listener != null && _listener.IsListening;

    /// <summary>One displayable quantity per column, in the order the wire format carries them.</summary>
    private readonly record struct Layer(string Id, string Label, string Unit, string Category);

    private sealed class RecordedTile
    {
        public int X;
        public int Z;
        public long Seq;
        public float[] Min;
        public float[] Max;
        public byte[] Data;
    }

    /// <summary>Where everyone is, for the markers. Supplied by the mod system, which can see them.</summary>
    private readonly System.Func<IReadOnlyList<(string Name, int X, int Z)>> _players;

    public DebugMapServer(ILogger logger, TerrainDiffusionProvider provider, DiffusionWorldSettings settings,
                          IRiverBasinSource riverBasins = null,
                          System.Func<IReadOnlyList<(string Name, int X, int Z)>> players = null)
    {
        _players = players;
        _log = logger;
        _provider = provider;
        _riverBasins = riverBasins;
        _settings = settings;
        _historyLimit = DiffusionConfig.Instance.DebugMapHistoryTiles;

        // Three groups, because the question worth asking of this map is whether the model did what
        // it was told: what the coarse stage was handed, what it answered, and what came out the far
        // end once the base and decoder had filled in everything below half a kilometre.
        const string Input = "Coarse model input";
        const string Coarse = "Coarse model output";
        const string Final = "Full resolution";

        _layers = new[]
        {
            new Layer("inElevation", "Elevation asked for", "m", Input),
            new Layer("inTemperature", "Temperature asked for", "°C", Input),
            new Layer("inTempSeasonality", "Temperature seasonality asked for", "BIO4", Input),
            new Layer("inPrecipitation", "Precipitation asked for", "mm", Input),
            new Layer("inPrecipCv", "Precipitation seasonality asked for", "% CV", Input),
            new Layer("inSeaFraction", "Ocean map (the world's own)", "0-1", Input),
            new Layer("riverBasin", "River basin conditioning", "0-1", Input),

            new Layer("coarseElevation", "Elevation", "m", Coarse),
            new Layer("coarseTemperature", "Mean temperature", "°C", Coarse),
            new Layer("coarseTempSeasonality", "Temperature seasonality", "BIO4", Coarse),
            new Layer("coarsePrecipitation", "Annual precipitation", "mm", Coarse),
            new Layer("coarsePrecipCv", "Precipitation seasonality", "% CV", Coarse),

            new Layer("surfaceY", "Surface height", "blocks", Final),
            new Layer("elevation", "Model elevation", "m", Final),
            new Layer("slope", "Slope", "rise/run", Final),
            new Layer("temperature", "Mean temperature", "°C", Final),
            new Layer("tempSeasonality", "Temperature seasonality", "BIO4", Final),
            new Layer("precipitation", "Annual precipitation", "mm", Final),
            new Layer("precipCv", "Precipitation seasonality", "% CV", Final),
            new Layer("rainfall", "Rainfall byte (as the game reads it)", "0-255", Final)
        };
    }

    public bool Start(string bindAddress, int port)
    {
        string prefix = $"http://{bindAddress}:{port}/";
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.Start();
        }
        catch (Exception e)
        {
            _log.Warning("[{0}] Could not start the debug map on {1}: {2}", DiffusionPaths.ModId, prefix, e.Message);
            _listener = null;
            return false;
        }

        Url = bindAddress is "+" or "*" or "0.0.0.0" ? $"http://localhost:{port}/" : prefix;

        _thread = new Thread(Serve) { IsBackground = true, Name = "tdiff-debugmap" };
        _thread.Start();

        _provider.TileGenerated += Record;

        _log.Notification("[{0}] Debug map at {1} - the heightmap and climate maps as they generate.",
            DiffusionPaths.ModId, Url);
        if (bindAddress != "127.0.0.1" && bindAddress != "localhost")
        {
            _log.Warning(
                "[{0}] The debug map is bound to '{1}' rather than loopback, so anything that can reach this " +
                "machine on port {2} can read the world's terrain and climate. Use 127.0.0.1 unless you meant this.",
                DiffusionPaths.ModId, bindAddress, port);
        }

        return true;
    }

    /// <summary>Downsamples one finished tile and files it under the next sequence number.</summary>
    private void Record(TerrainTile tile)
    {
        try
        {
            RecordedTile recorded = Downsample(tile);
            long key = ((long)recorded.X << 32) ^ (uint)recorded.Z;
            _tiles[key] = recorded;
            EvictOldest();
        }
        catch (Exception e)
        {
            // Never let the debug view break world generation.
            _log.Warning("[{0}] Debug map could not record a tile: {1}", DiffusionPaths.ModId, e.Message);
        }
    }

    /// <summary>
    /// What the model was told about rivers at a position, on the same 0 to 1 scale the
    /// conditioning uses. Zero where this world has no river network.
    /// </summary>
    private float BasinAt(int blockX, int blockZ)
    {
        if (_riverBasins == null) return 0f;

        try
        {
            int blocksPerCell = 32 * WorldPipelineModelConfig.Instance.LatentCompression
                                * Math.Max(1, _settings.Scale);
            int j = (int)Math.Floor((blockX - (double)_settings.OriginBlockX) / blocksPerCell);
            int i = (int)Math.Floor((blockZ - (double)_settings.OriginBlockZ) / blocksPerCell);

            // x then y, the same order the landmask and the synthetic map use.
            float[] strength = _riverBasins.BasinStrength(j, i, j + 1, i + 1);
            return strength is { Length: > 0 } ? strength[0] : 0f;
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    private RecordedTile Downsample(TerrainTile tile)
    {
        int size = tile.Size;
        int step = Math.Max(1, size / ThumbSize);
        int cells = ThumbSize * ThumbSize;
        int layerCount = _layers.Length;

        var values = new float[layerCount * cells];
        RainfallScale rainfall = RainfallScale.FromConfig(DiffusionConfig.Instance.WorldGen);

        // One reading for the whole tile: a coarse cell is wider than a tile, so there is no detail
        // below this to lose.
        int centreX = tile.BlockX + size / 2, centreZ = tile.BlockZ + size / 2;
        TerrainDiffusionProvider.CoarseProbe? probe = _provider.ProbeCoarseAt(centreX, centreZ);
        float basin = BasinAt(centreX, centreZ);

        // Seven input layers then five coarse outputs, all flat across the tile.
        var coarseValues = new float[12];
        if (probe != null)
        {
            TerrainDiffusionProvider.CoarseProbe p = probe.Value;
            for (int c = 0; c < 5; c++) coarseValues[c] = p.Conditioning[c];
            coarseValues[5] = p.SeaFraction < 0f ? 0f : p.SeaFraction;
            coarseValues[6] = basin;
            for (int c = 0; c < 5; c++) coarseValues[7 + c] = p.Output[c];
        }
        else
        {
            coarseValues[6] = basin;
        }

        for (int v = 0; v < ThumbSize; v++)
        {
            for (int u = 0; u < ThumbSize; u++)
            {
                int cell = v * ThumbSize + u;
                int x0 = u * step, z0 = v * step;
                int x1 = Math.Min(size, x0 + step), z1 = Math.Min(size, z0 + step);

                double surfaceY = 0, elevation = 0, slope = 0;
                double temperature = 0, tempSeasonality = 0, precipitation = 0, precipCv = 0;
                int n = 0;

                for (int z = z0; z < z1; z++)
                {
                    int row = z * size;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = row + x;
                        surfaceY += tile.SurfaceY[i];
                        elevation += tile.ElevationMeters[i];
                        slope += tile.Slope[i];
                        temperature += tile.TemperatureC[i];
                        tempSeasonality += tile.TemperatureSeasonality[i];
                        precipitation += tile.PrecipitationMm[i];
                        precipCv += tile.PrecipitationCv[i];
                        n++;
                    }
                }

                if (n == 0) continue;

                float meanTemperature = (float)(temperature / n);
                float meanSeasonality = (float)(tempSeasonality / n);
                float meanPrecipitation = (float)(precipitation / n);
                float meanCv = (float)(precipCv / n);

                values[12 * cells + cell] = (float)(surfaceY / n);
                values[13 * cells + cell] = (float)(elevation / n);
                values[14 * cells + cell] = (float)(slope / n);
                values[15 * cells + cell] = meanTemperature;
                values[16 * cells + cell] = meanSeasonality;
                values[17 * cells + cell] = meanPrecipitation;
                values[18 * cells + cell] = meanCv;

                // Derived from the cell's averaged climate rather than per column: the quantile map
                // costs a log and an error function, and this is an overview.
                values[19 * cells + cell] = rainfall.ToRainfall(
                    new Bioclim(meanTemperature, meanSeasonality, meanPrecipitation, meanCv));

                for (int c = 0; c < coarseValues.Length; c++) values[c * cells + cell] = coarseValues[c];
            }
        }

        var min = new float[layerCount];
        var max = new float[layerCount];
        var data = new byte[layerCount * cells];

        for (int layer = 0; layer < layerCount; layer++)
        {
            int off = layer * cells;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < cells; i++)
            {
                float value = values[off + i];
                if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
                values[off + i] = value;
                if (value < lo) lo = value;
                if (value > hi) hi = value;
            }

            min[layer] = lo;
            max[layer] = hi;

            float span = hi - lo;
            if (span <= 0f)
            {
                // Flat cell: every byte zero, and the client reads min == max and paints the floor.
                continue;
            }

            for (int i = 0; i < cells; i++)
            {
                data[off + i] = (byte)Math.Clamp((int)Math.Round((values[off + i] - lo) / span * 255f), 0, 255);
            }
        }

        return new RecordedTile
        {
            X = tile.BlockX / tile.Size,
            Z = tile.BlockZ / tile.Size,
            Seq = Interlocked.Increment(ref _sequence),
            Min = min,
            Max = max,
            Data = data
        };
    }

    /// <summary>
    /// Keeps the history inside its budget, dropping the tiles recorded longest ago. A tile that is
    /// regenerated is re-recorded under a new sequence number, so a busy area stays.
    /// </summary>
    private void EvictOldest()
    {
        if (_tiles.Count <= _historyLimit) return;

        foreach (KeyValuePair<long, RecordedTile> entry in _tiles.OrderBy(e => e.Value.Seq))
        {
            if (_tiles.Count <= _historyLimit) return;
            _tiles.TryRemove(entry.Key, out _);
        }
    }

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
                        + $"\"unit\":\"{Escape(_layers[i].Unit)}\",\"category\":\"{Escape(_layers[i].Category)}\"}}");
        }
        json.Append("]}");

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

    private static void Write(HttpListenerContext context, string contentType, byte[] body)
    {
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.OutputStream.Close();
    }

    private static string Fixed(float value) =>
        float.IsNaN(value) || float.IsInfinity(value)
            ? "0"
            : value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;

        _provider.TileGenerated -= Record;

        try { _listener?.Stop(); } catch { /* already down */ }
        try { _listener?.Close(); } catch { /* already down */ }
        _listener = null;

        _thread = null;
        _tiles.Clear();
    }
}
