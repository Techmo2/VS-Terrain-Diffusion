using Vintagestory.API.Common;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Machine-level settings, stored in <c>ModConfig/vsterraindiffusion.json</c>. Everything except
/// <see cref="WorldGen"/> describes the hardware the server is running on, not the world itself.
/// How the model runs (device, precisions, throttle) is set per world; see <see cref="InferenceSettings"/>.
/// </summary>
public class DiffusionConfig
{
    /// <summary>Total megabytes of decoded tensor windows kept across all pipeline stages.</summary>
    public int TileCacheMegabytes { get; set; } = 256;

    /// <summary>Latent windows per base-model call. Zero selects one on CPU and four on GPU.</summary>
    public int LatentBatchSize { get; set; }

    /// <summary>
    /// Megabytes of finished terrain tiles to keep. This has to cover everything world generation
    /// touches at once — a spawn area alone can span a hundred tiles — or tiles get evicted while
    /// still in use and are rebuilt from scratch.
    /// </summary>
    public int TerrainTileCacheMegabytes { get; set; } = 256;

    /// <summary>
    /// Number of chunk columns' worth of terrain generated per model query, in blocks. Larger
    /// values amortise model latency over more chunks at the cost of a longer first-visit stall.
    /// Zero selects 128 on CPU and 256 on GPU. Explicit values must be a multiple of 32.
    /// </summary>
    public int TerrainTileSizeBlocks { get; set; }

    /// <summary>
    /// Log a line at notification level for every terrain tile generated. Very noisy; useful when
    /// profiling. With this off the same lines are still written to the debug log, and only a tile
    /// far out of step with the rest of the session reaches the main one.
    /// </summary>
    public bool VerboseInference { get; set; }

    /// <summary>
    /// Port for the debug map, a small read-only web page showing the model's heightmap and
    /// climate maps as tiles are generated. Zero, the default, does not open a port at all.
    /// </summary>
    public int DebugMapPort { get; set; }

    /// <summary>
    /// Address the debug map listens on. Loopback by default, so only this machine can reach it;
    /// set to <c>0.0.0.0</c> to expose it to the network, which publishes the world's terrain and
    /// climate to anything that can reach the port.
    /// </summary>
    public string DebugMapBindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// How many generated tiles the debug map remembers. Each costs about eight kilobytes, and the
    /// oldest are dropped past this. The provider's own tile cache is no use here: it is sized for
    /// world generation and drops a tile as soon as the generator has moved on.
    /// </summary>
    public int DebugMapHistoryTiles { get; set; } = 2048;

    /// <summary>
    /// How this mod answers Algernon's Terrain Sampler, which other mods use to predict terrain
    /// without generating it. "full" builds the model's terrain tile around the position: exact, but
    /// a model run for any ground not generated yet. "coarse" answers from the coarse model alone,
    /// blurred to about 512 blocks but far cheaper. "stored" is exact where the tile is already in
    /// memory or the tile database and coarse elsewhere, so it never generates a tile: for a server
    /// whose terrain was pregenerated. Takes effect immediately.
    /// </summary>
    public string TerrainSamplerHeight { get; set; } = "full";

    /// <summary>
    /// World shaping and climate. Unlike the rest of this file these change what the world looks
    /// like, so editing them after a world has been explored will make new chunks disagree with old
    /// ones.
    /// </summary>
    public WorldGenConfig WorldGen { get; set; } = new();

    /// <summary>The loaded config, or defaults until <see cref="Load"/> runs.</summary>
    private static DiffusionConfig _instance;

    /// <summary>
    /// The loaded config; the defaults before <see cref="Load"/>, as the offline tools use it.
    /// </summary>
    public static DiffusionConfig Instance => _instance ??= new DiffusionConfig();

    /// <summary>
    /// Reads the config file, puts every value in range, writes it back so new keys appear, and makes it
    /// <see cref="Instance"/>.
    /// </summary>
    /// <param name="api">The game API whose mod config folder holds the file.</param>
    /// <returns>The loaded config.</returns>
    /// <remarks>
    /// A file that exists but cannot be read stops the game through
    /// <see cref="DiffusionFailure.Fatal(ILogger,string,System.Exception)"/>: defaults would generate
    /// terrain to settings the player never chose.
    /// </remarks>
    public static DiffusionConfig Load(ICoreAPI api)
    {
        DiffusionConfig config;
        try
        {
            config = api.LoadModConfig<DiffusionConfig>(DiffusionPaths.ModId + ".json");
        }
        catch (System.Exception e)
        {
            // Carrying on with defaults would generate terrain to settings the player never chose,
            // and would not match whatever this world was generated with before.
            throw DiffusionFailure.Fatal(api.Logger,
                $"The mod config ({DiffusionPaths.ModId}.json) could not be read. Fix or delete it.", e);
        }

        config ??= new DiffusionConfig();
        config.Sanitize();

        api.StoreModConfig(config, DiffusionPaths.ModId + ".json");
        _instance = config;
        return config;
    }

    /// <summary>Clamps every machine setting to its supported range and normalises the text ones.</summary>
    private void Sanitize()
    {
        (WorldGen ??= new WorldGenConfig()).Sanitize();

        if (DebugMapPort != 0 && (DebugMapPort < 1024 || DebugMapPort > 65535)) DebugMapPort = 0;
        DebugMapBindAddress = (DebugMapBindAddress ?? "127.0.0.1").Trim();
        if (DebugMapBindAddress.Length == 0) DebugMapBindAddress = "127.0.0.1";
        if (DebugMapHistoryTiles < 64) DebugMapHistoryTiles = 64;
        if (DebugMapHistoryTiles > 65536) DebugMapHistoryTiles = 65536;

        if (TileCacheMegabytes < 32) TileCacheMegabytes = 32;
        if (TileCacheMegabytes > 4096) TileCacheMegabytes = 4096;

        if (LatentBatchSize < 0) LatentBatchSize = 0;
        if (LatentBatchSize > 16) LatentBatchSize = 16;

        if (TerrainTileCacheMegabytes < 32) TerrainTileCacheMegabytes = 32;
        if (TerrainTileCacheMegabytes > 4096) TerrainTileCacheMegabytes = 4096;

        if (TerrainTileSizeBlocks != 0)
        {
            if (TerrainTileSizeBlocks < 64) TerrainTileSizeBlocks = 64;
            if (TerrainTileSizeBlocks > 1024) TerrainTileSizeBlocks = 1024;
            TerrainTileSizeBlocks -= TerrainTileSizeBlocks % 32;
        }

        TerrainSamplerHeight = (TerrainSamplerHeight ?? "full").Trim().ToLowerInvariant();
        if (TerrainSamplerHeight is not ("coarse" or "stored")) TerrainSamplerHeight = "full";
    }
}
