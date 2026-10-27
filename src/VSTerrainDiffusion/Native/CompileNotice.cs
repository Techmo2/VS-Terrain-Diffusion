using System;
using System.Diagnostics;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;

#nullable enable

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Tells the player while the GPU runtime compiles a model, which it does the first time a model
/// runs and which takes minutes - measured on a Radeon Pro WX 9100 with MIGraphX: 23 s for the
/// coarse model, 3 min 50 s for the base model, 2 min 23 s for the decoder. Nothing else moves on
/// the loading screen meanwhile, so without this the world looks hung.
///
/// The runtime only says when a compile begins and ends, with no progress in between, so the
/// notice is the model's name and the time so far, every <see cref="Interval"/>. The compiled
/// programs are cached, so a later world skips all of it.
/// </summary>
internal sealed class CompileNotice
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>The lines MIGraphX's ONNX Runtime provider prints around a compile.</summary>
    private const string MigraphxBegin = "Model Compile: Begin", MigraphxComplete = "Model Compile: Complete";

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Timer? _timer;
    private Stopwatch? _elapsed;
    private string _model = "";

    public CompileNotice(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Looks at one line of the worker's output. <paramref name="runningModel"/> is the model the
    /// worker was asked to run, which is the one being compiled.
    /// </summary>
    public void OnWorkerOutput(string line, string? runningModel)
    {
        if (line.Contains(MigraphxBegin, StringComparison.Ordinal)) Begin(runningModel == null ? "a model" : $"the {runningModel} model");
        else if (line.Contains(MigraphxComplete, StringComparison.Ordinal)) Complete();
    }

    private void Begin(string model)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _model = model;
            _elapsed = Stopwatch.StartNew();
            _timer = new Timer(_ => Report(), null, Interval, Interval);
        }
        LoadingNotice.Post(_logger,
            "Preparing {0} for this GPU. This happens once and can take several minutes.", model);
    }

    private void Report()
    {
        string model;
        TimeSpan elapsed;
        lock (_gate)
        {
            if (_elapsed == null) return;
            model = _model;
            elapsed = _elapsed.Elapsed;
        }
        LoadingNotice.Post(_logger, "Still preparing {0}: {1} so far.", model, Describe(elapsed));
    }

    private void Complete()
    {
        string model;
        TimeSpan elapsed;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_elapsed == null) return;
            model = _model;
            elapsed = _elapsed.Elapsed;
            _elapsed = null;
        }
        LoadingNotice.Post(_logger, "Prepared {0} in {1}.", model, Describe(elapsed));
    }

    private static string Describe(TimeSpan time) => time.TotalMinutes < 1
        ? $"{time.TotalSeconds:0} s"
        : $"{(int)time.TotalMinutes} min {time.Seconds} s";

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _elapsed = null;
        }
    }
}
