using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Owns the three model graphs used by <see cref="WorldPipeline"/>, and the
/// <see cref="InferenceWorker"/> they run in. Loading happens once per world on a background
/// thread; anything that needs the models waits on <see cref="Await"/>. <see cref="Shutdown"/>
/// ends the worker, and the native runtime with it.
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

    /// <summary>The process the models run in. Shared by <see cref="WithCoarse"/> copies, owned here.</summary>
    public InferenceWorker Worker { get; private set; }

    private PipelineModels() { }

    /// <summary>
    /// These models with a coarse model of its own, for a pipeline that only ever runs the coarse
    /// stage alongside another. The shared models are not owned by the copy: dispose
    /// <paramref name="coarse"/> separately, never the copy.
    /// </summary>
    internal PipelineModels WithCoarse(IModelRunner coarse) =>
        new() { Coarse = coarse, Base = Base, Decoder = Decoder, Worker = Worker };

    /// <summary>True once the models are resident and ready to run.</summary>
    public static bool IsReady => _instance != null;

    /// <summary>Set when loading failed; the mod reports this instead of stalling worldgen forever.</summary>
    public static Exception LoadFailure => _loadFailure;

    /// <summary>
    /// Kicks off asset download, native runtime preparation, the worker and model loading on a
    /// background thread. Returns immediately.
    /// </summary>
    public static void BeginLoad(ILogger logger)
    {
        lock (Gate)
        {
            if (_instance != null) return;
            if (_loadThread != null)
            {
                // Shutdown may invalidate a load that has not noticed yet, part way through a
                // download or a worker request. Queue this request; the old loader starts it once
                // its partial models and worker are disposed.
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

    /// <summary>
    /// ONNX Runtime's CPU kernels widen fp16 back to float, so on a CPU provider it costs speed and
    /// still changes the terrain.
    /// </summary>
    private static void WarnIfHalfPrecisionOnCpu(ILogger logger)
    {
        if (OnnxRuntimeBootstrap.Provider is not (InferenceProvider.Cpu or InferenceProvider.OpenVino)) return;

        InferenceSettings config = InferenceSettings.Current;
        var half = new List<string>();
        if (config.BasePrecision == "fp16") half.Add("base");
        if (config.DecoderPrecision == "fp16") half.Add("decoder");
        if (half.Count == 0) return;

        logger.Warning(
            "[{0}] FP16 is selected for the {1} model(s) but inference runs on {2}, which widens half precision " +
            "back to float: slower than FP32 and still different terrain. Use a GPU provider, or set these back to fp32.",
            DiffusionPaths.ModId, string.Join(" and ", half), OnnxRuntimeBootstrap.Provider);
    }

    /// <summary>
    /// INT8 is a smaller decoder for CPU-only servers. Set on a world that resolved a GPU provider it
    /// still runs, but the choice was probably made for another machine, so say so.
    /// </summary>
    private static void WarnIfInt8OnGpu(ILogger logger)
    {
        if (InferenceSettings.Current.DecoderPrecision != "int8") return;
        if (OnnxRuntimeBootstrap.Provider is InferenceProvider.Cpu or InferenceProvider.OpenVino) return;

        logger.Warning(
            "[{0}] the decoder is set to INT8, which is meant for CPU-only servers, but inference runs on {1}. " +
            "Nothing selects INT8 on its own: it is this world's {2}.",
            DiffusionPaths.ModId, OnnxRuntimeBootstrap.Provider, InferenceSettings.Code(nameof(InferenceSettings.DecoderPrecision)));
    }

    private static void Load(ILogger logger, int generation, CancellationTokenSource cancellation)
    {
        PipelineModels loading = null;
        try
        {
            CancellationToken token = cancellation.Token;
            ModelAssetManager.EnsureAssetsReady(logger, token);
            loading = new PipelineModels { Worker = StartWorker(logger, token) };
            token.ThrowIfCancellationRequested();

            // Closes off whichever of the two downloads announced itself. Nothing is said at all on
            // a server that already had its files.
            if (ModelAssetManager.Downloaded || OnnxRuntimeBootstrap.Downloaded)
            {
                LoadingNotice.Post(logger, "Downloads complete.");
            }

            logger.Notification("[{0}] Model graph loading: {1}", DiffusionPaths.ModId,
                InferenceSettings.Current.ModelLoadMode);
            string decoderPath = ModelAssetManager.ResolveDecoderPath(logger);
            string coarsePath = ModelAssetManager.ResolveCoarsePath(logger);
            string basePath = ModelAssetManager.ResolveBasePath(logger);
            WarnIfHalfPrecisionOnCpu(logger);
            WarnIfInt8OnGpu(logger);
            if (OnnxRuntimeBootstrap.Provider == InferenceProvider.OpenVino)
            {
                loading.Decoder = LoadOpenVinoOrCpu(
                    loading.Worker, decoderPath, decoderPath, "decoder", logger, token);
                token.ThrowIfCancellationRequested();

                // OpenVINO is effectively tied with ONNX Runtime on the FP32 coarse and base
                // graphs, while compiling the 1.9 GB base graph needs substantially more memory.
                // Load the decoder first so its temporary compilation work does not overlap the
                // base model's resident CPU session.
                loading.Coarse = new OnnxModel(loading.Worker, coarsePath, "coarse", logger, token);
                token.ThrowIfCancellationRequested();
                loading.Base = new OnnxModel(loading.Worker, basePath, "base", logger, token);
            }
            else
            {
                loading.Coarse = new OnnxModel(loading.Worker, coarsePath, "coarse", logger, token);
                token.ThrowIfCancellationRequested();
                loading.Base = new OnnxModel(loading.Worker, basePath, "base", logger, token);
                token.ThrowIfCancellationRequested();
                loading.Decoder = new OnnxModel(loading.Worker, decoderPath, "decoder", logger, token);
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
                        _loadCancellation = null;
                        _loadThread = null;
                        Loaded.Set();
                    }
                    else if (ReferenceEquals(_loadThread, Thread.CurrentThread))
                    {
                        if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
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

    /// <summary>
    /// A worker running the runtime this world asked for. A compatible provider whose runtime does
    /// not load there - a library that will not open, a plugin that will not register - gives way
    /// to <see cref="OnnxRuntimeBootstrap.FallbackFor"/> in a fresh worker, for this world only.
    /// </summary>
    private static InferenceWorker StartWorker(ILogger logger, CancellationToken cancellation)
    {
        RuntimePlan plan = OnnxRuntimeBootstrap.Prepare(logger, cancellation);
        InferenceWorker worker;
        try
        {
            worker = InferenceWorker.Start(plan, logger, cancellation);
        }
        catch (InferenceWorkerException e) when (plan.Provider != InferenceProvider.Cpu)
        {
            InferenceProvider fallback = OnnxRuntimeBootstrap.FallbackFor(plan.Provider, logger);
            OnnxRuntimeBootstrap.WarnFallback(plan.Provider, fallback, e, logger);
            plan = OnnxRuntimeBootstrap.Prepare(fallback, logger, cancellation);
            worker = InferenceWorker.Start(plan, logger, cancellation);
        }

        OnnxRuntimeBootstrap.Use(plan);
        return worker;
    }

    private static IModelRunner LoadOpenVinoOrCpu(InferenceWorker worker, string openVinoPath, string cpuPath,
                                                   string name, ILogger logger,
                                                   CancellationToken cancellation)
    {
        try
        {
            return new OpenVinoModel(
                worker, openVinoPath, cpuPath ?? openVinoPath, name, logger, cancellation);
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
            return new OnnxModel(worker, cpuPath ?? openVinoPath, name, logger, cancellation);
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

        // Last: the models above unload in it, and its exit is what frees the native runtime.
        Worker?.Dispose();
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

            // A live loader disposes its partial models and worker, and clears the thread, in its
            // finally block. If it is already stopped, there is nothing left to wait for.
            if (_loadThread == null || !_loadThread.IsAlive)
            {
                _loadThread = null;
                _loadCancellation = null;
            }
        }
    }
}
