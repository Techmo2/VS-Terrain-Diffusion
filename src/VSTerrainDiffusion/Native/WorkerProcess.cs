using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VSTerrainDiffusion.Core;

#nullable enable

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Starts the mod's worker executable (<c>VSTerrainDiffusion.Worker</c>), which the mod carries
/// embedded and writes out on first use. It runs on the same shared .NET install as the game.
/// </summary>
internal static class WorkerProcess
{
    private static readonly string[] Files =
    {
        "VSTerrainDiffusion.Worker.dll",
        "VSTerrainDiffusion.Worker.deps.json",
        "VSTerrainDiffusion.Worker.runtimeconfig.json",
        "Microsoft.ML.OnnxRuntime.dll",
        "System.Numerics.Tensors.dll"
    };

    private const string CompleteMarker = ".complete";

    private static readonly object Gate = new();
    private static string? _workerPath;

    /// <summary>A start description for the worker with <paramref name="arguments"/>, output redirected.</summary>
    internal static ProcessStartInfo StartInfo(params string[] arguments)
    {
        string workerPath = Extract();
        var startInfo = new ProcessStartInfo
        {
            FileName = DotnetHost(),
            WorkingDirectory = Path.GetDirectoryName(workerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(workerPath);
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        // Overlays and profilers preloaded into the game have no business in a headless worker.
        startInfo.Environment.Remove("LD_PRELOAD");
        startInfo.Environment.Remove("DYLD_INSERT_LIBRARIES");
        return startInfo;
    }

    /// <summary>
    /// Writes the worker out once per build, into a directory named after its contents. A worker
    /// from an earlier game that has not quite exited, or the OpenVINO worker beside this one, keeps
    /// its files: Windows will not replace a DLL a process has open.
    /// </summary>
    private static string Extract()
    {
        lock (Gate)
        {
            if (_workerPath != null) return _workerPath;

            Assembly assembly = typeof(WorkerProcess).Assembly;
            var contents = new byte[Files.Length][];
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                for (int i = 0; i < Files.Length; i++)
                {
                    using Stream resource = assembly.GetManifestResourceStream(Files[i])
                        ?? throw new FileNotFoundException($"Embedded worker file is missing: {Files[i]}");
                    using var buffer = new MemoryStream();
                    resource.CopyTo(buffer);
                    contents[i] = buffer.ToArray();
                    hash.AppendData(contents[i]);
                }

                string name = Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant();
                string directory = Path.Combine(DiffusionPaths.RuntimeDirectory, "worker", name);
                if (!File.Exists(Path.Combine(directory, CompleteMarker)))
                {
                    string staging = directory + ".install-" + Guid.NewGuid().ToString("N");
                    Directory.CreateDirectory(staging);
                    try
                    {
                        for (int i = 0; i < Files.Length; i++)
                            File.WriteAllBytes(Path.Combine(staging, Files[i]), contents[i]);
                        File.WriteAllText(Path.Combine(staging, CompleteMarker), "");
                        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                        Directory.Move(staging, directory);
                    }
                    catch (IOException) when (File.Exists(Path.Combine(directory, CompleteMarker)))
                    {
                        // Another game process on this data folder wrote the same files first.
                    }
                    finally
                    {
                        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
                    }
                }

                _workerPath = Path.Combine(directory, Files[0]);
                return _workerPath;
            }
        }
    }

    /// <summary>
    /// The <c>dotnet</c> host of the .NET install the game is running on. The game is started by
    /// its own launcher, so neither the process path nor PATH can be relied on to name it; the
    /// runtime's own directory (<c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;</c>) can.
    /// </summary>
    private static string DotnetHost()
    {
        string executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetFileName(processPath), executable, StringComparison.OrdinalIgnoreCase))
            return processPath;

        string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        string root = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
        string candidate = Path.Combine(root, executable);
        if (File.Exists(candidate)) return candidate;

        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            candidate = Path.Combine(dotnetRoot, executable);
            if (File.Exists(candidate)) return candidate;
        }
        return executable;
    }
}
