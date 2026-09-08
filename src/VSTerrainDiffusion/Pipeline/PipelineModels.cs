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

    /// <summary>
    /// Bumped by <see cref="Shutdown"/>. A load that was still running when its world went away
    /// belongs to a generation nobody is waiting on any more, and publishes nothing.
    /// </summary>
    private static int _generation;

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

            int generation = _generation;
            _loadThread = new Thread(() => Load(logger, generation))
            {
                IsBackground = true,
                Name = "terrain-diffusion-model-load"
            };
            _loadThread.Start();
        }
    }

    private static void Load(ILogger logger, int generation)
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

            lock (Gate)
            {
                // The world these were being loaded for was abandoned partway through - the player
                // backed out of world creation, or the server stopped. Publishing them now would
                // leave a full set of sessions, well over a gigabyte of them, owned by nobody.
                if (_generation != generation)
                {
                    models.Dispose();
                    return;
                }

                _instance = models;
            }

            logger.Notification("[{0}] Terrain Diffusion models ready ({1})",
                DiffusionPaths.ModId, OnnxModel.ActiveProvider);
        }
        catch (Exception e)
        {
            lock (Gate)
            {
                if (_generation != generation) return;
                _loadFailure = e;
            }
            logger.Error("[{0}] Failed to load the Terrain Diffusion models: {1}", DiffusionPaths.ModId, e);
        }
        finally
        {
            // Never for a stale generation: Shutdown cleared this so the next world can wait on it.
            lock (Gate)
            {
                if (_generation == generation) Loaded.Set();
            }
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
            _generation++;
            _instance?.Dispose();
            _instance = null;
            _loadThread = null;
            _loadFailure = null;
            Loaded.Reset();

            // The sessions that just went away were most of a gigabyte of native memory. Without
            // this the allocator keeps every byte of it mapped, and each world a player creates
            // looks like it leaks the whole model set.
            NativeHeap.ReleaseFreeArenas();
        }
    }
}
