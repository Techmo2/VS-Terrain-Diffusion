using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace VSTerrainDiffusion.Native;

/// <summary>Failure reported by, or while communicating with, the isolated OpenVINO worker.</summary>
public sealed class OpenVinoWorkerException : Exception
{
    internal OpenVinoWorkerException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// Hosts OpenVINO behind a process boundary. The CPU plugin performs native JIT compilation, so a
/// plugin abort or access violation must not be allowed to terminate the game server.
/// </summary>
public sealed class OpenVinoWorkerClient : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] WorkerFiles =
    {
        "VSTerrainDiffusion.OpenVinoWorker.dll",
        "VSTerrainDiffusion.OpenVinoWorker.deps.json",
        "VSTerrainDiffusion.OpenVinoWorker.runtimeconfig.json"
    };

    private readonly object _gate = new();
    private readonly Process _process;
    private readonly BinaryWriter _writer;
    private readonly BinaryReader _reader;
    private readonly Action<string>? _progress;
    private readonly string _modelName;
    private readonly StringBuilder _standardError = new();
    private bool _disposed;

    public OpenVinoWorkerClient(string modelPath, string modelName, string cacheDirectory,
                                string nativeDirectory, int threadCount,
                                Action<string>? progress = null,
                                CancellationToken cancellation = default)
    {
        _progress = progress;
        _modelName = modelName;
        string workerDirectory = Path.Combine(Path.GetFullPath(nativeDirectory), "worker");
        string workerPath = ExtractWorker(workerDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetHost(),
            WorkingDirectory = workerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(workerPath);
        startInfo.ArgumentList.Add(Path.GetFullPath(modelPath));
        startInfo.ArgumentList.Add(Path.GetFullPath(cacheDirectory));
        startInfo.ArgumentList.Add(Path.GetFullPath(nativeDirectory));
        startInfo.ArgumentList.Add(Math.Max(1, threadCount).ToString(CultureInfo.InvariantCulture));

        string? inheritedLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(inheritedLibraryPath)
            ? Path.GetFullPath(nativeDirectory)
            : Path.GetFullPath(nativeDirectory) + Path.PathSeparator + inheritedLibraryPath;
        startInfo.Environment.Remove("LD_PRELOAD");

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

    private float[] ReadResult()
    {
        int status = _reader.ReadInt32();
        if (status == OpenVinoWorkerProtocol.Failure)
            throw new InvalidOperationException(_reader.ReadString());
        if (status != OpenVinoWorkerProtocol.Success)
            throw new InvalidDataException($"OpenVINO worker returned status {status}");
        return OpenVinoWorkerProtocol.ReadFloats(_reader.BaseStream, _reader.ReadInt32());
    }

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

    private OpenVinoWorkerException WorkerFailure(string message, Exception? inner = null)
    {
        string details;
        lock (_standardError) details = _standardError.ToString().Trim();
        if (_process.HasExited) message += $" (exit code {_process.ExitCode})";
        if (details.Length != 0) message += ": " + details;
        return new OpenVinoWorkerException(message, inner);
    }

    private static string ExtractWorker(string directory)
    {
        Directory.CreateDirectory(directory);
        Assembly assembly = typeof(OpenVinoWorkerClient).Assembly;
        foreach (string name in WorkerFiles)
        {
            using Stream resource = assembly.GetManifestResourceStream(name)
                ?? throw new FileNotFoundException($"Embedded OpenVINO worker file is missing: {name}");
            string target = Path.Combine(directory, name);
            string temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    resource.CopyTo(output);
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); } catch { }
            }
        }
        return Path.Combine(directory, WorkerFiles[0]);
    }

    private static string ResolveDotnetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;

        string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            string candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpenVinoWorkerClient));
    }

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

    private void DisposeProcessResources()
    {
        _reader.Dispose();
        _writer.Dispose();
        _process.Dispose();
    }

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
