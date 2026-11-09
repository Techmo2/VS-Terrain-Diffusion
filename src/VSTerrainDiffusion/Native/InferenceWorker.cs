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
    /// <summary>Creates the exception.</summary>
    /// <param name="message">
    /// What failed, with the worker's recent output appended when there was any.
    /// </param>
    /// <param name="inner">The underlying failure, if any.</param>
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
    /// <summary>How long the worker has to connect its pipes and answer the handshake.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long the worker has to exit after a shutdown request before it is killed.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Kernel buffer size of each pipe.</summary>
    private const int PipeBufferBytes = 1 << 20;

    /// <summary>The server log, which the worker's log frames are written to.</summary>
    private readonly ILogger _logger;

    /// <summary>The worker process.</summary>
    private readonly Process _process;

    /// <summary>Game to worker.</summary>
    private readonly NamedPipeServerStream _requestPipe;

    /// <summary>Worker to game.</summary>
    private readonly NamedPipeServerStream _replyPipe;

    /// <summary>Writes requests to <see cref="_requestPipe"/>, under <see cref="_writeGate"/>.</summary>
    private readonly BinaryWriter _writer;

    /// <summary>Reads replies from <see cref="_replyPipe"/>, on <see cref="_readerThread"/> only.</summary>
    private readonly BinaryReader _reader;

    /// <summary>Keeps each request's frame in one piece when several threads send at once.</summary>
    private readonly object _writeGate = new();

    /// <summary>Requests sent and not yet answered, by request id.</summary>
    private readonly ConcurrentDictionary<int, Pending> _pending = new();

    /// <summary>The last 8 KiB of the worker's stdout and stderr, appended to failures.</summary>
    private readonly StringBuilder _standardError = new();

    /// <summary>Tells the player while the GPU runtime compiles a model.</summary>
    private readonly CompileNotice _compileNotice;

    /// <summary>Each loaded model's name, by its id in the worker.</summary>
    private readonly ConcurrentDictionary<int, string> _modelNames = new();

    /// <summary>The model whose run was sent last, which is the one a compile message is about.</summary>
    private volatile string? _runningModel;

    /// <summary>Reads replies and hands each to its waiting request.</summary>
    private readonly Thread _readerThread;

    /// <summary>The last request id handed out.</summary>
    private int _nextRequest;

    /// <summary>The last model id handed out.</summary>
    private int _nextModel;

    /// <summary>Why the worker stopped answering, once it has; every later request fails with it.</summary>
    private volatile string? _stopped;

    /// <summary>1 once <see cref="Dispose"/> has begun.</summary>
    private int _disposed;

    /// <summary>The provider the worker was initialised for.</summary>
    internal InferenceProvider Provider { get; }

    /// <summary>Starts the worker process, connects both pipes and checks its handshake.</summary>
    /// <param name="plan">The runtime the worker is for.</param>
    /// <param name="logger">The server log.</param>
    /// <param name="cancellation">Abandons the start.</param>
    /// <exception cref="InferenceWorkerException">The worker did not start or answered wrongly.</exception>
    private InferenceWorker(RuntimePlan plan, ILogger logger, CancellationToken cancellation)
    {
        _logger = logger;
        Provider = plan.Provider;
        _compileNotice = new CompileNotice(logger);

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
        _modelNames[model] = spec.Name;
        return (model, System.Text.Json.JsonSerializer.Deserialize<ModelInfo>(reply.Json!)!);
    }

    /// <summary>Runs a model. <paramref name="elapsedNanoseconds"/> is the run itself, inside the worker.</summary>
    internal float[] Run(int model, IReadOnlyList<(float[] Data, long[] Shape)> inputs, out long elapsedNanoseconds)
    {
        _runningModel = _modelNames.TryGetValue(model, out string? name) ? name : null;
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

    /// <summary>Sends one request and waits for its answer.</summary>
    /// <param name="kind">The request kind, from <see cref="InferenceWorkerProtocol"/>.</param>
    /// <param name="payload">Writes the request's payload after its kind and id.</param>
    /// <param name="cancellation">
    /// Abandons the wait, which kills the worker; only model loads pass one.
    /// </param>
    /// <returns>The answer.</returns>
    /// <exception cref="InferenceWorkerException">The worker failed the request or stopped.</exception>
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

    /// <summary>
    /// The reply reader's loop: logs log frames and completes each answered request. When the replies end,
    /// for whatever reason, every request still waiting fails.
    /// </summary>
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

    /// <summary>The pending request a reply answers.</summary>
    /// <param name="id">The reply's request id.</param>
    /// <returns>The request.</returns>
    /// <exception cref="InvalidDataException">Nothing is waiting on that id.</exception>
    private Pending Take(int id) => _pending.TryGetValue(id, out Pending? pending)
        ? pending
        : throw new InvalidDataException($"The inference worker answered request {id}, which nothing asked for");

    /// <summary>
    /// Waits for a startup step while watching for the worker exiting, the deadline and cancellation.
    /// </summary>
    /// <param name="task">The step.</param>
    /// <param name="cancellation">Abandons the wait.</param>
    /// <exception cref="InferenceWorkerException">
    /// The worker exited, timed out or the step failed.
    /// </exception>
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

    /// <summary>
    /// Keeps one line of the worker's console output for error reports, logs it at debug level and watches it
    /// for compile messages.
    /// </summary>
    /// <param name="line">The line, or null at the end of the stream.</param>
    private void OnWorkerOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_standardError)
        {
            _standardError.AppendLine(line);
            if (_standardError.Length > 8192) _standardError.Remove(0, _standardError.Length - 8192);
        }
        _logger.Debug("[{0}] worker: {1}", DiffusionPaths.ModId, line);
        _compileNotice.OnWorkerOutput(line, _runningModel);
    }

    /// <summary>Refuses new requests once disposed or stopped.</summary>
    /// <exception cref="ObjectDisposedException">Disposed.</exception>
    /// <exception cref="InferenceWorkerException">The worker stopped.</exception>
    private void ThrowIfStopped()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(InferenceWorker));
        if (_stopped != null) throw Failure(_stopped);
    }

    /// <summary>An exception with the worker's recent console output appended.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="inner">The underlying failure, if any.</param>
    /// <returns>The exception, to throw.</returns>
    private InferenceWorkerException Failure(string message, Exception? inner = null)
    {
        string details;
        lock (_standardError) details = _standardError.ToString().Trim();
        return new InferenceWorkerException(details.Length == 0 ? message : message + ":\n" + details, inner);
    }

    /// <summary>Kills the worker and anything it started, if it is still running.</summary>
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

    /// <summary>Releases the pipes, streams, process handle and compile notice.</summary>
    private void DisposeResources()
    {
        try { _writer.Dispose(); } catch { }
        try { _reader.Dispose(); } catch { }
        _requestPipe.Dispose();
        _replyPipe.Dispose();
        _process.Dispose();
        _compileNotice.Dispose();
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

    /// <summary>One request in flight, and its answer once it arrives.</summary>
    private sealed class Pending
    {
        /// <summary>Set once the request is answered or has failed.</summary>
        public readonly ManualResetEventSlim Done = new(false);

        /// <summary>The JSON payload of the answer, if it had one.</summary>
        public string? Json;

        /// <summary>The tensor payload of the answer, if it had one.</summary>
        public float[]? Tensor;

        /// <summary>How long the worker spent running the model, for a tensor answer.</summary>
        public long ElapsedNanoseconds;

        /// <summary>Why the request failed, or null.</summary>
        public string? Error;
    }
}
