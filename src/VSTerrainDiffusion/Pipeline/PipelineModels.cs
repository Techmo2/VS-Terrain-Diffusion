using System;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Owns the three ONNX graphs used by <see cref="WorldPipeline"/>. Loading happens once on a
/// background thread; anything that needs the models waits on <see cref="Await"/>.
/// </summary>
public sealed class PipelineModels : IDisposable
{
    private static readonly object Gate = new();
    private static PipelineModels _instance;
    private static Thread _loadThread;
    private static readonly ManualResetEventSlim Loaded = new(false);
    private static Exception _loadFailure;

    public OnnxModel Coarse { get; private set; }
    public OnnxModel Base { get; private set; }
    public OnnxModel Decoder { get; private set; }

    private PipelineModels() { }

    /// <summary>True once the models are resident and ready to run.</summary>
    public static bool IsReady => _instance != null;

    /// <summary>Set when loading failed; the mod reports this instead of stalling worldgen forever.</summary>
    public static Exception LoadFailure => _loadFailure;

    /// <summary>
    /// Kicks off asset download, native runtime resolution and model loading on a background
    /// thread. Returns immediately.
    /// </summary>
    public static void BeginLoad(ILogger logger)
    {
        lock (Gate)
        {
            if (_loadThread != null || _instance != null) return;

            _loadThread = new Thread(() => Load(logger))
            {
                IsBackground = true,
                Name = "terrain-diffusion-model-load"
            };
            _loadThread.Start();
        }
    }

    private static void Load(ILogger logger)
    {
        try
        {
            ModelAssetManager.EnsureAssetsReady(logger);
            OnnxRuntimeBootstrap.Initialize(logger);

            // Closes off whichever of the two downloads announced itself. Nothing is said at all on
            // a server that already had its files.
            if (ModelAssetManager.Downloaded || OnnxRuntimeBootstrap.Downloaded)
            {
                LoadingNotice.Post(logger, "Downloads complete.");
            }

            var models = new PipelineModels
            {
                Coarse = new OnnxModel(ModelAssetManager.ResolveAssetPath("coarse_model.onnx"), "coarse", logger),
                Base = new OnnxModel(ModelAssetManager.ResolveAssetPath("base_model.onnx"), "base", logger),
                Decoder = new OnnxModel(ModelAssetManager.ResolveAssetPath("decoder_model.onnx"), "decoder", logger)
            };

            _instance = models;
            logger.Notification("[{0}] Terrain Diffusion models ready ({1})",
                DiffusionPaths.ModId, OnnxModel.ActiveProvider);
        }
        catch (Exception e)
        {
            _loadFailure = e;
            logger.Error("[{0}] Failed to load the Terrain Diffusion models: {1}", DiffusionPaths.ModId, e);
        }
        finally
        {
            Loaded.Set();
        }
    }

    /// <summary>Blocks until loading finishes, then returns the models or throws the load failure.</summary>
    public static PipelineModels Await()
    {
        if (_instance != null) return _instance;
        Loaded.Wait();
        if (_instance != null) return _instance;
        throw new InvalidOperationException(
            "Terrain Diffusion models are unavailable", _loadFailure);
    }

    public void Dispose()
    {
        Coarse?.Dispose();
        Base?.Dispose();
        Decoder?.Dispose();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    /// <summary>Drops the shared instance; used when the server shuts down.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            _instance?.Dispose();
            _instance = null;
            _loadThread = null;
            _loadFailure = null;
            Loaded.Reset();
        }
    }
}
