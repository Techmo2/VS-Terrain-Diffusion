using System;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Owns the three model graphs used by <see cref="WorldPipeline"/>. Loading happens once on a
/// background thread; anything that needs the models waits on <see cref="Await"/>.
/// </summary>
public sealed class PipelineModels : IDisposable
{
    private static readonly object Gate = new();
    private static volatile PipelineModels _instance;
    private static Thread _loadThread;
    private static volatile ManualResetEventSlim Loaded = new(false);
    private static volatile Exception _loadFailure;
    private static CancellationTokenSource _loadCancellation;
    private static ILogger _pendingLoadLogger;
    private static int _loadGeneration;

    public IModelRunner Coarse { get; private set; }
    public IModelRunner Base { get; private set; }
    public IModelRunner Decoder { get; private set; }

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
            if (_instance != null) return;
            if (_loadThread != null)
            {
                // Shutdown may invalidate a load while it is inside a native session constructor,
                // which cannot be cancelled. Queue this request; the old loader starts it only
                // after its partial models are disposed and shared ONNX state is safe to reset.
                if (_loadThread.IsAlive && _loadCancellation?.IsCancellationRequested == true)
                {
                    if (_pendingLoadLogger == null)
                    {
                        _pendingLoadLogger = logger;
                        Loaded = new ManualResetEventSlim(false);
                        _loadFailure = null;
                    }
                }
                return;
            }

            StartLoadLocked(logger);
        }
    }

    private static void StartLoadLocked(ILogger logger, bool completionAlreadyPrepared = false)
    {
        if (!completionAlreadyPrepared)
        {
            Loaded = new ManualResetEventSlim(false);
            _loadFailure = null;
        }
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        int generation = ++_loadGeneration;
        _loadThread = new Thread(() => Load(logger, generation, cancellation))
        {
            Name = "terrain-diffusion-model-load",
            IsBackground = true
        };
        _loadThread.Start();
    }

    private static void Load(ILogger logger, int generation, CancellationTokenSource cancellation)
    {
        PipelineModels loading = null;
        try
        {
            CancellationToken token = cancellation.Token;
            ModelAssetManager.EnsureAssetsReady(logger, token);
            OnnxRuntimeBootstrap.Initialize(logger, token);
            token.ThrowIfCancellationRequested();

            // Closes off whichever of the two downloads announced itself. Nothing is said at all on
            // a server that already had its files.
            if (ModelAssetManager.Downloaded || OnnxRuntimeBootstrap.Downloaded)
            {
                LoadingNotice.Post(logger, "Downloads complete.");
            }

            loading = new PipelineModels();
            string decoderPath = ModelAssetManager.ResolveDecoderPath(logger);
            if (OnnxRuntimeBootstrap.Provider == InferenceProvider.OpenVino)
            {
                loading.Decoder = LoadOpenVinoOrCpu(
                    decoderPath, decoderPath, "decoder", logger, token);
                token.ThrowIfCancellationRequested();

                // OpenVINO is effectively tied with ONNX Runtime on the FP32 coarse and base
                // graphs, while compiling the 1.9 GB base graph needs substantially more memory.
                // Load the decoder first so its temporary compilation work does not overlap the
                // base model's resident CPU session.
                loading.Coarse = new OnnxModel(
                    ModelAssetManager.ResolveAssetPath("coarse_model.onnx"), "coarse", logger);
                token.ThrowIfCancellationRequested();
                loading.Base = new OnnxModel(
                    ModelAssetManager.ResolveAssetPath("base_model.onnx"), "base", logger);
            }
            else
            {
                loading.Coarse = new OnnxModel(
                    ModelAssetManager.ResolveAssetPath("coarse_model.onnx"), "coarse", logger);
                token.ThrowIfCancellationRequested();
                loading.Base = new OnnxModel(
                    ModelAssetManager.ResolveAssetPath("base_model.onnx"), "base", logger);
                token.ThrowIfCancellationRequested();
                loading.Decoder = new OnnxModel(decoderPath, "decoder", logger);
            }

            token.ThrowIfCancellationRequested();
            lock (Gate)
            {
                if (generation != _loadGeneration || !ReferenceEquals(_loadCancellation, cancellation)) return;
                logger.Notification(
                    "[{0}] Terrain Diffusion models ready (coarse: {1}, base: {2}, decoder: {3})",
                    DiffusionPaths.ModId, loading.Coarse.Backend, loading.Base.Backend, loading.Decoder.Backend);
                _instance = loading;
                loading = null;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Server shutdown invalidated this load generation. Its partial models are disposed below.
        }
        catch (Exception e)
        {
            bool current;
            lock (Gate)
            {
                current = generation == _loadGeneration && ReferenceEquals(_loadCancellation, cancellation);
                if (current) _loadFailure = e;
            }
            if (current)
                logger.Error("[{0}] Failed to load the Terrain Diffusion models: {1}", DiffusionPaths.ModId, e);
        }
        finally
        {
            try
            {
                loading?.Dispose();
            }
            finally
            {
                lock (Gate)
                {
                    bool current = generation == _loadGeneration &&
                                   ReferenceEquals(_loadCancellation, cancellation);
                    if (current)
                    {
                        // Model construction and partial cleanup are complete before this lock.
                        // Clear ownership here, rather than on physical thread exit, so Shutdown
                        // cannot catch the thread in its harmless epilogue and defer cleanup forever.
                        if (_instance == null) OnnxModel.ResetSharedState();
                        _loadCancellation = null;
                        _loadThread = null;
                        Loaded.Set();
                    }
                    else if (ReferenceEquals(_loadThread, Thread.CurrentThread))
                    {
                        if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
                        OnnxModel.ResetSharedState();
                        _loadThread = null;

                        ILogger pending = _pendingLoadLogger;
                        _pendingLoadLogger = null;
                        if (pending != null) StartLoadLocked(pending, completionAlreadyPrepared: true);
                    }
                }
                cancellation.Dispose();
            }
        }
    }

    private static IModelRunner LoadOpenVinoOrCpu(string openVinoPath, string cpuPath,
                                                   string name, ILogger logger,
                                                   CancellationToken cancellation)
    {
        try
        {
            return new OpenVinoModel(
                openVinoPath, cpuPath ?? openVinoPath, name, logger, cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            cancellation.ThrowIfCancellationRequested();
            logger.Warning(
                "[{0}] OpenVINO could not compile '{1}' ({2}); using ONNX Runtime CPU for this model. " +
                "This provider change can alter newly generated terrain slightly.",
                DiffusionPaths.ModId, name, e.Message);
            return new OnnxModel(cpuPath ?? openVinoPath, name, logger);
        }
    }

    /// <summary>Blocks until loading finishes, then returns the models or throws the load failure.</summary>
    public static PipelineModels Await()
    {
        if (_instance != null) return _instance;
        ManualResetEventSlim loaded = Loaded;
        loaded.Wait();
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
            ++_loadGeneration;
            _loadCancellation?.Cancel();
            _pendingLoadLogger = null;
            _instance?.Dispose();
            _instance = null;
            _loadFailure = new OperationCanceledException(
                "Terrain Diffusion model loading was cancelled because the server is shutting down.");
            Loaded.Set();

            // A live loader owns the shared ONNX state until it exits its current constructor and
            // disposes any partial models. Its finally block performs the reset and clears the
            // thread. If it is already stopped, cleanup is safe here.
            if (_loadThread == null || !_loadThread.IsAlive)
            {
                _loadThread = null;
                _loadCancellation = null;
                OnnxModel.ResetSharedState();
            }
        }
    }
}
