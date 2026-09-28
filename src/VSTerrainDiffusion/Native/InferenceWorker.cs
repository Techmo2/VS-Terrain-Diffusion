using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;

#nullable enable

namespace VSTerrainDiffusion.Native;

/// <summary>Failure reported by, or while talking to, the inference worker.</summary>
public sealed class InferenceWorkerException : Exception
{
    internal InferenceWorkerException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// ONNX Runtime in a process of its own, one per game (see <c>OnnxWorkerHost</c> in the worker).
///
/// Once a native library is bound into a .NET process it stays: the managed binding caches its
/// entry points and Vintage Story's mod assemblies are never unloaded. So the runtime lives in a
/// worker the world starts and ends, and a second world in the same session starts from nothing -
/// whichever device and build it asks for. A native crash also takes down the worker rather than
/// the game.
///
/// Requests are numbered and can be in flight together; the worker answers each once, as its
/// models finish. A worker that dies fails every request waiting on it.
/// </summary>
public sealed class InferenceWorker : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private const int PipeBufferBytes = 1 << 20;

    private readonly ILogger _logger;
    private readonly Process _process;
    private readonly NamedPipeServerStream _requestPipe;
    private readonly NamedPipeServerStream _replyPipe;
    private readonly BinaryWriter _writer;
    private readonly BinaryReader _reader;
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly StringBuilder _standardError = new();
    private readonly Thread _readerThread;
    private int _nextRequest;
    private int _nextModel;
    private volatile string? _stopped;
    private int _disposed;

    /// <summary>The provider the worker was initialised for.</summary>
    internal InferenceProvider Provider { get; }

    private InferenceWorker(RuntimePlan plan, ILogger logger, CancellationToken cancellation)
    {
        _logger = logger;
        Provider = plan.Provider;

        string name = "vstd-" + Guid.NewGuid().ToString("N")[..20];
        string nonce = Guid.NewGuid().ToString("N");
        _requestPipe = new NamedPipeServerStream(name + "-q", PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, PipeBufferBytes, PipeBufferBytes);
        _replyPipe = new NamedPipeServerStream(name + "-r", PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, PipeBufferBytes, PipeBufferBytes);

        _process = Process.Start(WorkerProcess.StartInfo("onnx", name + "-q", name + "-r", nonce))
                   ?? throw new InferenceWorkerException("Could not start the inference worker");
        _process.OutputDataReceived += (_, e) => OnWorkerOutput(e.Data);
        _process.ErrorDataReceived += (_, e) => OnWorkerOutput(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _process.StandardInput.Close();

        _writer = new BinaryWriter(new BufferedStream(_requestPipe, 64 * 1024), Encoding.UTF8);
        _reader = new BinaryReader(new BufferedStream(_replyPipe, 64 * 1024), Encoding.UTF8);

        try
        {
            AwaitStartup(Task.WhenAll(_requestPipe.WaitForConnectionAsync(cancellation),
                                      _replyPipe.WaitForConnectionAsync(cancellation)), cancellation);

            // The pipe name is guessable by anything on the machine; the nonce was handed only to
            // the process started above.
            AwaitStartup(Task.Run(() =>
            {
                if (_reader.ReadInt32() != InferenceWorkerProtocol.HandshakeMagic || _reader.ReadString() != nonce)
                    throw new InvalidDataException("The inference worker answered with the wrong handshake");
            }, cancellation), cancellation);
        }
        catch
        {
            Kill();
            DisposeResources();
            throw;
        }

        _readerThread = new Thread(ReadReplies) { Name = "inference-worker-replies", IsBackground = true };
        _readerThread.Start();
    }

    /// <summary>
    /// Starts a worker and has it load the runtime <paramref name="plan"/> describes. Throws
    /// <see cref="InferenceWorkerException"/> if the runtime cannot be loaded there.
    /// </summary>
    internal static InferenceWorker Start(RuntimePlan plan, ILogger logger, CancellationToken cancellation)
    {
        var stopwatch = Stopwatch.StartNew();
        var worker = new InferenceWorker(plan, logger, cancellation);
        try
        {
            worker.Request(InferenceWorkerProtocol.Initialise,
                writer => InferenceWorkerProtocol.WriteJson(writer, plan), cancellation);
        }
        catch
        {
            worker.Dispose();
            throw;
        }

        logger.Notification("[{0}] Inference worker started (pid {1}) in {2} ms: {3} from {4}",
            DiffusionPaths.ModId, worker._process.Id, stopwatch.ElapsedMilliseconds, plan.Provider,
            plan.OnnxRuntimeDirectory);
        return worker;
    }

    /// <summary>Loads a model in the worker. Returns its id there and what it loaded on.</summary>
    internal (int Model, ModelInfo Info) LoadModel(ModelSpec spec, CancellationToken cancellation)
    {
        int model = Interlocked.Increment(ref _nextModel);
        Pending reply = Request(InferenceWorkerProtocol.LoadModel, writer =>
        {
            writer.Write(model);
            InferenceWorkerProtocol.WriteJson(writer, spec);
        }, cancellation);
        return (model, System.Text.Json.JsonSerializer.Deserialize<ModelInfo>(reply.Json!)!);
    }

    /// <summary>Runs a model. <paramref name="elapsedNanoseconds"/> is the run itself, inside the worker.</summary>
    internal float[] Run(int model, IReadOnlyList<(float[] Data, long[] Shape)> inputs, out long elapsedNanoseconds)
    {
        Pending reply = Request(InferenceWorkerProtocol.Run, writer =>
        {
            writer.Write(model);
            InferenceWorkerProtocol.WriteTensors(writer, inputs);
        }, CancellationToken.None);
        elapsedNanoseconds = reply.ElapsedNanoseconds;
        return reply.Tensor!;
    }

    /// <summary>Drops a model and its session. Does nothing once the worker has stopped.</summary>
    internal void Unload(int model)
    {
        if (_stopped != null || Volatile.Read(ref _disposed) != 0) return;
        try
        {
            Request(InferenceWorkerProtocol.UnloadModel, writer => writer.Write(model), CancellationToken.None);
        }
        catch (InferenceWorkerException)
        {
            // Stopping anyway; the process takes the session with it.
        }
    }

    private Pending Request(int kind, Action<BinaryWriter> payload, CancellationToken cancellation)
    {
        int id = Interlocked.Increment(ref _nextRequest);
        var pending = new Pending();
        _pending[id] = pending;
        try
        {
            ThrowIfStopped();
            lock (_writeGate)
            {
                _writer.Write(kind);
                _writer.Write(id);
                payload(_writer);
                _writer.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw Failure("The inference worker stopped taking requests", e);
        }

        // The reply reader fails whatever is pending when it stops; this covers a request that
        // registered just after it looked.
        if (_stopped != null && !pending.Done.IsSet)
        {
            pending.Error ??= _stopped;
            pending.Done.Set();
        }

        if (cancellation.CanBeCanceled)
        {
            if (WaitHandle.WaitAny(new[] { pending.Done.WaitHandle, cancellation.WaitHandle }) == 1)
            {
                // A load cannot be abandoned half way inside the worker, so the worker goes. Only
                // the model loader passes a token, and a cancelled load is followed by disposal.
                Kill();
                cancellation.ThrowIfCancellationRequested();
            }
        }
        else
        {
            pending.Done.Wait();
        }

        _pending.TryRemove(id, out _);
        if (pending.Error != null) throw Failure(pending.Error);
        return pending;
    }

    private void ReadReplies()
    {
        try
        {
            while (true)
            {
                int kind = _reader.ReadInt32();
                int id = _reader.ReadInt32();
                switch (kind)
                {
                    case InferenceWorkerProtocol.Log:
                    {
                        int level = _reader.ReadInt32();
                        string message = _reader.ReadString();
                        if (level == InferenceWorkerProtocol.LogWarning)
                            _logger.Warning("[{0}] {1}", DiffusionPaths.ModId, message);
                        else if (level == InferenceWorkerProtocol.LogError)
                            _logger.Error("[{0}] {1}", DiffusionPaths.ModId, message);
                        else
                            _logger.Notification("[{0}] {1}", DiffusionPaths.ModId, message);
                        continue;
                    }

                    case InferenceWorkerProtocol.Ok:
                    {
                        Pending pending = Take(id);
                        switch (_reader.ReadInt32())
                        {
                            case InferenceWorkerProtocol.PayloadJson:
                                pending.Json = _reader.ReadString();
                                break;
                            case InferenceWorkerProtocol.PayloadTensor:
                                pending.ElapsedNanoseconds = _reader.ReadInt64();
                                pending.Tensor = InferenceWorkerProtocol.ReadFloats(_reader);
                                break;
                        }
                        pending.Done.Set();
                        continue;
                    }

                    case InferenceWorkerProtocol.Failed:
                    {
                        Pending pending = Take(id);
                        pending.Error = _reader.ReadString();
                        pending.Done.Set();
                        continue;
                    }

                    default:
                        throw new InvalidDataException($"Unknown reply {kind} from the inference worker");
                }
            }
        }
        catch (Exception e)
        {
            if (e is not (EndOfStreamException or IOException or ObjectDisposedException))
                _logger.Error("[{0}] Lost the inference worker's replies: {1}", DiffusionPaths.ModId, e);
        }

        // Whatever ended the replies, nothing more is coming: fail everything still waiting.
        _process.WaitForExit(2000);
        _stopped = _process.HasExited ? $"The inference worker stopped (exit code {_process.ExitCode})"
                                       : "The inference worker stopped answering";
        foreach (Pending pending in _pending.Values)
        {
            pending.Error ??= _stopped;
            pending.Done.Set();
        }
    }

    private Pending Take(int id) => _pending.TryGetValue(id, out Pending? pending)
        ? pending
        : throw new InvalidDataException($"The inference worker answered request {id}, which nothing asked for");

    private void AwaitStartup(Task task, CancellationToken cancellation)
    {
        var deadline = Stopwatch.StartNew();
        while (!task.Wait(100))
        {
            cancellation.ThrowIfCancellationRequested();
            if (_process.HasExited)
                throw Failure($"The inference worker exited during startup (exit code {_process.ExitCode})");
            if (deadline.Elapsed > StartupTimeout)
                throw Failure("The inference worker did not start in time");
        }
        if (task.IsFaulted) throw Failure("The inference worker failed to start", task.Exception?.GetBaseException());
    }

    private void OnWorkerOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_standardError)
        {
            _standardError.AppendLine(line);
            if (_standardError.Length > 8192) _standardError.Remove(0, _standardError.Length - 8192);
        }
        _logger.Debug("[{0}] worker: {1}", DiffusionPaths.ModId, line);
    }

    private void ThrowIfStopped()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(InferenceWorker));
        if (_stopped != null) throw Failure(_stopped);
    }

    private InferenceWorkerException Failure(string message, Exception? inner = null)
    {
        string details;
        lock (_standardError) details = _standardError.ToString().Trim();
        return new InferenceWorkerException(details.Length == 0 ? message : message + ":\n" + details, inner);
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
            // Already gone.
        }
    }

    private void DisposeResources()
    {
        try { _writer.Dispose(); } catch { }
        try { _reader.Dispose(); } catch { }
        _requestPipe.Dispose();
        _replyPipe.Dispose();
        _process.Dispose();
    }

    /// <summary>
    /// Asks the worker to finish and exit, and kills it if it does not. The process exiting is
    /// what unloads the native runtime.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            if (_stopped == null && !_process.HasExited)
            {
                lock (_writeGate)
                {
                    _writer.Write(InferenceWorkerProtocol.Shutdown);
                    _writer.Write(0);
                    _writer.Flush();
                }
                if (!_process.WaitForExit((int)ShutdownTimeout.TotalMilliseconds))
                {
                    _logger.Warning("[{0}] The inference worker did not exit within {1} s; stopping it.",
                        DiffusionPaths.ModId, ShutdownTimeout.TotalSeconds);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Already gone.
        }

        Kill();
        _readerThread?.Join(2000);
        DisposeResources();
    }

    private sealed class Pending
    {
        public readonly ManualResetEventSlim Done = new(false);
        public string? Json;
        public float[]? Tensor;
        public long ElapsedNanoseconds;
        public string? Error;
    }
}
