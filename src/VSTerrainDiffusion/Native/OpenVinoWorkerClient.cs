using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace VSTerrainDiffusion.Native;

/// <summary>Failure reported by, or while communicating with, the isolated OpenVINO worker.</summary>
public sealed class OpenVinoWorkerException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed, with the exit code and recent stderr appended.</param>
    /// <param name="inner">The underlying failure, if any.</param>
    internal OpenVinoWorkerException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// Hosts OpenVINO behind a process boundary. The CPU plugin performs native JIT compilation, so a
/// plugin abort or access violation must not be allowed to terminate the game server.
/// </summary>
public sealed class OpenVinoWorkerClient : IDisposable
{
    /// <summary>How long the worker may take to compile the model and report ready.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long one inference exchange may take before the worker is killed.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    /// <summary>One exchange at a time; also guards disposal.</summary>
    private readonly object _gate = new();

    /// <summary>The worker process.</summary>
    private readonly Process _process;

    /// <summary>Writes requests to the worker's stdin.</summary>
    private readonly BinaryWriter _writer;

    /// <summary>Reads replies from the worker's stdout.</summary>
    private readonly BinaryReader _reader;

    /// <summary>Receives the worker's stderr lines, or null.</summary>
    private readonly Action<string>? _progress;

    /// <summary>The model's name, for messages.</summary>
    private readonly string _modelName;

    /// <summary>The last 4 KiB of the worker's stderr, appended to failures.</summary>
    private readonly StringBuilder _standardError = new();

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    private bool _disposed;

    /// <summary>
    /// Starts a worker that compiles <paramref name="modelPath"/> for the CPU, and waits until it is ready.
    /// </summary>
    /// <param name="modelPath">The ONNX model.</param>
    /// <param name="modelName">The model's name, for messages.</param>
    /// <param name="cacheDirectory">Where OpenVINO caches the compiled model.</param>
    /// <param name="nativeDirectory">The OpenVINO runtime directory.</param>
    /// <param name="threadCount">Inference threads; at least one.</param>
    /// <param name="progress">Receives the worker's progress lines, or null.</param>
    /// <param name="cancellation">Abandons the start, killing the worker.</param>
    /// <exception cref="OpenVinoWorkerException">The worker failed to start or compile the model.</exception>
    public OpenVinoWorkerClient(string modelPath, string modelName, string cacheDirectory,
                                string nativeDirectory, int threadCount,
                                Action<string>? progress = null,
                                CancellationToken cancellation = default)
    {
        _progress = progress;
        _modelName = modelName;
        ProcessStartInfo startInfo = WorkerProcess.StartInfo(
            "openvino",
            Path.GetFullPath(modelPath),
            Path.GetFullPath(cacheDirectory),
            Path.GetFullPath(nativeDirectory),
            Math.Max(1, threadCount).ToString(CultureInfo.InvariantCulture));

        string? inheritedLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(inheritedLibraryPath)
            ? Path.GetFullPath(nativeDirectory)
            : Path.GetFullPath(nativeDirectory) + Path.PathSeparator + inheritedLibraryPath;

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the OpenVINO worker process");
        _writer = new BinaryWriter(_process.StandardInput.BaseStream, Encoding.UTF8, leaveOpen: true);
        _reader = new BinaryReader(_process.StandardOutput.BaseStream, Encoding.UTF8, leaveOpen: true);

        try
        {
            _process.ErrorDataReceived += OnErrorData;
            _process.BeginErrorReadLine();
            int magic = CompleteWithTimeout(
                () => _reader.ReadInt32(), StartupTimeout, "start", cancellation);
            if (magic != OpenVinoWorkerProtocol.ReadyMagic)
                throw new InvalidDataException("OpenVINO worker returned an invalid startup response");
            progress?.Invoke($"OpenVINO worker ready (pid {_process.Id})");
        }
        catch (OperationCanceledException)
        {
            Terminate();
            DisposeProcessResources();
            throw;
        }
        catch (Exception exception)
        {
            Terminate();
            if (cancellation.IsCancellationRequested)
            {
                DisposeProcessResources();
                throw new OperationCanceledException(cancellation);
            }
            OpenVinoWorkerException failure = WorkerFailure(
                $"OpenVINO worker failed while compiling '{_modelName}'", exception);
            DisposeProcessResources();
            throw failure;
        }
    }

    /// <summary>
    /// Runs the model once. A worker that fails or takes longer than <see cref="RunTimeout"/> is killed, and
    /// every later run fails.
    /// </summary>
    /// <param name="inputs">Float32 tensors in the model's input order.</param>
    /// <returns>The single output, flattened.</returns>
    /// <exception cref="OpenVinoWorkerException">The run failed or the worker has stopped.</exception>
    public float[] Run(IReadOnlyList<(float[] Data, long[] Shape)> inputs)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_process.HasExited) throw WorkerFailure("OpenVINO worker stopped unexpectedly");
            ValidateInputs(inputs);

            try
            {
                // Time the complete exchange. Timing only the response read leaves a hung worker
                // free to block the server indefinitely while the request fills the stdin pipe.
                return CompleteWithTimeout(() => SendAndRead(inputs), RunTimeout, "run inference");
            }
            catch (Exception exception)
            {
                Terminate();
                throw WorkerFailure($"OpenVINO worker failed during '{_modelName}' inference", exception);
            }
        }
    }

    /// <summary>Writes one run request and reads its reply.</summary>
    /// <param name="inputs">The tensors.</param>
    /// <returns>The output.</returns>
    private float[] SendAndRead(IReadOnlyList<(float[] Data, long[] Shape)> inputs)
    {
        _writer.Write(OpenVinoWorkerProtocol.RunCommand);
        _writer.Write(inputs.Count);
        foreach ((float[] data, long[] shape) in inputs)
        {
            _writer.Write(shape.Length);
            foreach (long dimension in shape) _writer.Write(dimension);
            _writer.Write(data.Length);
            OpenVinoWorkerProtocol.WriteFloats(_writer.BaseStream, data);
        }
        _writer.Flush();
        return ReadResult();
    }

    /// <summary>Checks the inputs against the protocol's limits before anything is sent.</summary>
    /// <param name="inputs">The tensors.</param>
    /// <exception cref="ArgumentException">
    /// A count, rank or shape is out of range or does not match its data.
    /// </exception>
    private static void ValidateInputs(IReadOnlyList<(float[] Data, long[] Shape)> inputs)
    {
        if (inputs.Count < 1 || inputs.Count > OpenVinoWorkerProtocol.MaxInputs)
            throw new ArgumentOutOfRangeException(nameof(inputs));
        foreach ((float[] data, long[] shape) in inputs)
        {
            if (shape.Length < 1 || shape.Length > OpenVinoWorkerProtocol.MaxRank)
                throw new ArgumentOutOfRangeException(nameof(inputs), "Invalid tensor rank");
            long elements = 1;
            foreach (long dimension in shape)
            {
                if (dimension < 1 || elements > OpenVinoWorkerProtocol.MaxElements / dimension)
                    throw new ArgumentOutOfRangeException(nameof(inputs), "Invalid tensor shape");
                elements *= dimension;
            }
            if (elements != data.Length)
                throw new ArgumentException("Tensor shape does not match its data length", nameof(inputs));
        }
    }

    /// <summary>Reads a run reply.</summary>
    /// <returns>The output.</returns>
    /// <exception cref="InvalidOperationException">The worker reported a failure.</exception>
    private float[] ReadResult()
    {
        int status = _reader.ReadInt32();
        if (status == OpenVinoWorkerProtocol.Failure)
            throw new InvalidOperationException(_reader.ReadString());
        if (status != OpenVinoWorkerProtocol.Success)
            throw new InvalidDataException($"OpenVINO worker returned status {status}");
        return OpenVinoWorkerProtocol.ReadFloats(_reader.BaseStream, _reader.ReadInt32());
    }

    /// <summary>
    /// Runs a blocking pipe operation on the thread pool and kills the worker if it does not finish in time.
    /// </summary>
    /// <typeparam name="T">The operation's result.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="timeout">Its deadline.</param>
    /// <param name="description">What it does, for the message.</param>
    /// <param name="cancellation">Abandons the wait.</param>
    /// <returns>The result.</returns>
    /// <exception cref="TimeoutException">The deadline passed.</exception>
    private T CompleteWithTimeout<T>(Func<T> operation, TimeSpan timeout, string description,
                                     CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Task<T> task = Task.Run(operation);
        Task completed = Task.WhenAny(task, Task.Delay(timeout, cancellation)).GetAwaiter().GetResult();
        if (ReferenceEquals(completed, task)) return task.GetAwaiter().GetResult();
        Terminate();
        cancellation.ThrowIfCancellationRequested();
        throw new TimeoutException($"Timed out waiting for OpenVINO worker to {description}");
    }

    /// <summary>Keeps a stderr line for error reports and forwards it as progress.</summary>
    /// <param name="sender">The process.</param>
    /// <param name="args">The line.</param>
    private void OnErrorData(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data)) return;
        lock (_standardError)
        {
            _standardError.AppendLine(args.Data);
            if (_standardError.Length > 4096) _standardError.Remove(0, _standardError.Length - 4096);
        }
        _progress?.Invoke(args.Data);
    }

    /// <summary>An exception with the exit code and recent stderr appended.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="inner">The underlying failure, if any.</param>
    /// <returns>The exception, to throw.</returns>
    private OpenVinoWorkerException WorkerFailure(string message, Exception? inner = null)
    {
        string details;
        lock (_standardError) details = _standardError.ToString().Trim();
        if (_process.HasExited) message += $" (exit code {_process.ExitCode})";
        if (details.Length != 0) message += ": " + details;
        return new OpenVinoWorkerException(message, inner);
    }

    /// <summary>Refuses use after disposal.</summary>
    /// <exception cref="ObjectDisposedException">Disposed.</exception>
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpenVinoWorkerClient));
    }

    /// <summary>Kills the worker if it is still running.</summary>
    private void Terminate()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch { }
    }

    /// <summary>Releases the streams and the process handle.</summary>
    private void DisposeProcessResources()
    {
        _reader.Dispose();
        _writer.Dispose();
        _process.Dispose();
    }

    /// <summary>
    /// Asks the worker to exit, kills it if it does not within two seconds, and releases it.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    _writer.Write(OpenVinoWorkerProtocol.ShutdownCommand);
                    _writer.Flush();
                    if (!_process.WaitForExit(2000)) Terminate();
                }
            }
            catch { Terminate(); }
            DisposeProcessResources();
        }
    }
}
