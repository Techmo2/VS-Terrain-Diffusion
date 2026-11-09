using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
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
public sealed partial class DebugMapServer : IDisposable
{
    /// <summary>Columns per thumbnail edge. One byte per column per layer.</summary>
    public const int ThumbSize = 32;

    /// <summary>The server log.</summary>
    private readonly ILogger _log;

    /// <summary>What the model was told about rivers, or null without a river network.</summary>
    private readonly IRiverBasinSource _riverBasins;

    /// <summary>The world's terrain, whose tiles are recorded as they generate.</summary>
    private readonly TerrainDiffusionProvider _provider;

    /// <summary>The world's settings, for the conditioning grid.</summary>
    private readonly DiffusionWorldSettings _settings;

    /// <summary>One displayable quantity per column, in the order the wire format carries them.</summary>
    private readonly Layer[] _layers;

    /// <summary>Each layer's slot in <see cref="_layers"/> and the wire format, by id.</summary>
    private readonly Dictionary<string, int> _slot;

    /// <summary>The coarse probe's twelve readings, in the order <see cref="Downsample"/> fills them.</summary>
    private static readonly string[] CoarseLayerIds =
    {
        "inElevation", "inTemperature", "inTempSeasonality", "inPrecipitation", "inPrecipCv",
        "inSeaFraction", "riverBasin",
        "coarseElevation", "coarseTemperature", "coarseTempSeasonality", "coarsePrecipitation", "coarsePrecipCv"
    };

    /// <summary>Most tiles remembered; see <see cref="DiffusionConfig.DebugMapHistoryTiles"/>.</summary>
    private readonly int _historyLimit;

    /// <summary>The recorded thumbnails, by tile coordinates packed into a long.</summary>
    private readonly ConcurrentDictionary<long, RecordedTile> _tiles = new();

    /// <summary>The last sequence number handed out; the page asks for tiles newer than its own.</summary>
    private long _sequence;

    /// <summary>Whether the built-surface layer has already reported falling back to the model's height.</summary>
    private bool _builtSurfaceWarned;

    /// <summary>The HTTP listener; null when stopped.</summary>
    private HttpListener _listener;

    /// <summary>Serves requests, one at a time.</summary>
    private Thread _thread;

    /// <summary>Set by <see cref="Dispose"/>, ending the serve loop.</summary>
    private volatile bool _stopping;

    /// <summary>The address to open in a browser.</summary>
    public string Url { get; private set; }

    /// <summary>Number of tiles currently remembered.</summary>
    public int TileCount => _tiles.Count;

    /// <summary>Whether the listener is up.</summary>
    public bool IsRunning => _listener != null && _listener.IsListening;

    /// <summary>
    /// One map layer. A categorical layer stores a class index in each byte (its range is fixed at
    /// 0-255, so the byte is the value) and the page paints it from <see cref="Api.Koppen.Classes"/>.
    /// </summary>
    private readonly record struct Layer(string Id, string Label, string Unit, string Category, bool Categorical = false);

    /// <summary>One tile's thumbnail, as the wire format sends it.</summary>
    private sealed class RecordedTile
    {
        /// <summary>Tile column, in tiles.</summary>
        public int X;

        /// <summary>Tile row, in tiles.</summary>
        public int Z;

        /// <summary>When it was recorded, as a sequence number.</summary>
        public long Seq;

        /// <summary>Each layer's minimum.</summary>
        public float[] Min;

        /// <summary>Each layer's maximum.</summary>
        public float[] Max;

        /// <summary>
        /// One byte per cell per layer, layer-major, scaled between <see cref="Min"/> and <see cref="Max"/>.
        /// </summary>
        public byte[] Data;
    }

    /// <summary>Where everyone is, for the markers. Supplied by the mod system, which can see them.</summary>
    private readonly System.Func<IReadOnlyList<(string Name, int X, int Z)>> _players;

    /// <summary>
    /// The forest and shrub map layers the game reads, or nulls while the model's climate is off.
    /// Asked per tile because they are installed after the map has started.
    /// </summary>
    private readonly System.Func<(DiffusionForestMapLayer Forest, DiffusionForestMapLayer Shrub)> _vegetation;

    /// <summary>Sets up the layers; nothing listens until <see cref="Start"/>.</summary>
    /// <param name="logger">The server log.</param>
    /// <param name="provider">The world's terrain.</param>
    /// <param name="settings">The world's settings.</param>
    /// <param name="riverBasins">What the model was told about rivers, or null.</param>
    /// <param name="players">Where the players are, or null for no markers.</param>
    /// <param name="vegetation">The forest and shrub map layers, or null.</param>
    public DebugMapServer(ILogger logger, TerrainDiffusionProvider provider, DiffusionWorldSettings settings,
                          IRiverBasinSource riverBasins = null,
                          System.Func<IReadOnlyList<(string Name, int X, int Z)>> players = null,
                          System.Func<(DiffusionForestMapLayer, DiffusionForestMapLayer)> vegetation = null)
    {
        _players = players;
        _vegetation = vegetation;
        _log = logger;
        _provider = provider;
        _riverBasins = riverBasins;
        _settings = settings;
        _historyLimit = DiffusionConfig.Instance.DebugMapHistoryTiles;

        // Three groups, because the question worth asking of this map is whether the model did what
        // it was told: what the coarse stage was handed, what it answered, and what came out the far
        // end once the base and decoder had filled in everything below half a kilometre. A fourth
        // holds what the game itself reads: its own noise maps, and the ground as built.
        const string Input = "Coarse model input";
        const string Coarse = "Coarse model output";
        const string Final = "Full resolution";
        const string Vanilla = "Vanilla channels";

        _layers = new[]
        {
            new Layer("inElevation", "Elevation asked for", "m", Input),
            new Layer("inTemperature", "Temperature asked for", "°C", Input),
            new Layer("inTempSeasonality", "Temperature seasonality asked for", "BIO4", Input),
            new Layer("inPrecipitation", "Precipitation asked for", "mm", Input),
            new Layer("inPrecipCv", "Precipitation seasonality asked for", "% CV", Input),
            new Layer("riverBasin", "River basin conditioning", "0-1", Input),

            new Layer("coarseElevation", "Elevation", "m", Coarse),
            new Layer("coarseTemperature", "Mean temperature", "°C", Coarse),
            new Layer("coarseTempSeasonality", "Temperature seasonality", "BIO4", Coarse),
            new Layer("coarsePrecipitation", "Annual precipitation", "mm", Coarse),
            new Layer("coarsePrecipCv", "Precipitation seasonality", "% CV", Coarse),

            new Layer("elevation", "Model elevation", "m", Final),
            new Layer("slope", "Slope", "rise/run", Final),
            new Layer("temperature", "Mean temperature", "°C", Final),
            new Layer("tempSeasonality", "Temperature seasonality", "BIO4", Final),
            new Layer("precipitation", "Annual precipitation", "mm", Final),
            new Layer("precipCv", "Precipitation seasonality", "% CV", Final),
            new Layer("koppen", "Köppen class", "", Final, Categorical: true),
            new Layer("diurnalRange", "Diurnal temperature range", "°C", Final),
            new Layer("rainfall", "Rainfall byte (as the game reads it)", "0-255", Final),
            new Layer("forestModel", "Forest cover from the model", "0-255", Final),
            new Layer("forest", "Forest map (as the game reads it)", "0-255", Final),
            new Layer("shrubModel", "Shrub cover from the model", "0-255", Final),
            new Layer("shrub", "Shrub map (as the game reads it)", "0-255", Final),

            // The ocean map is what the coarse stage is conditioned on, read per coarse cell; the
            // forest and shrub maps are where the model's cover is cleared, read per map pixel.
            new Layer("surfaceY", "Surface height (as built, with river valleys)", "blocks", Vanilla),
            new Layer("inSeaFraction", "Ocean map (sea share per coarse cell)", "0-1", Vanilla),
            new Layer("vanillaForest", "Forest map", "0-255", Vanilla),
            new Layer("vanillaShrub", "Shrub map", "0-255", Vanilla)
        };

        _slot = new Dictionary<string, int>(_layers.Length);
        for (int i = 0; i < _layers.Length; i++) _slot[_layers[i].Id] = i;
    }

    /// <summary>Starts listening and recording tiles.</summary>
    /// <param name="bindAddress">The address to bind, normally 127.0.0.1.</param>
    /// <param name="port">The TCP port.</param>
    /// <returns>False when the listener could not start; the reason is logged.</returns>
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

    /// <summary>Stops listening and recording, and forgets the history. Safe to call twice.</summary>
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
