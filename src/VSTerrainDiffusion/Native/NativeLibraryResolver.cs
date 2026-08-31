using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

#nullable enable

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Installs each native resolver at most once. ONNX Runtime's imports live in its managed assembly,
/// while the standalone OpenVINO imports live in the mod assembly, so each assembly needs its own
/// resolver and each registration has to survive a failed-and-retried initialisation.
/// </summary>
internal static class NativeLibraryResolver
{
    private static readonly object Gate = new();
    private static bool _onnxInstalled;
    private static bool _openVinoInstalled;
    private static string? _onnxDirectory;
    private static string? _openVinoDirectory;

    internal static void ConfigureOnnxRuntime(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        lock (Gate)
        {
            EnsureSameDirectory(_onnxDirectory, fullPath, "ONNX Runtime");
            _onnxDirectory = fullPath;
            if (_onnxInstalled) return;

            // Use the assembly containing the binding this mod will actually call. Assembly.Load by
            // name can select a copy in another mod's load context on a dedicated server.
            Assembly onnxAssembly = typeof(InferenceSession).Assembly;
            try
            {
                NativeLibrary.SetDllImportResolver(onnxAssembly, ResolveOnnxRuntime);
            }
            catch (InvalidOperationException)
            {
                // Another mod may share this managed ORT assembly and have installed its resolver
                // first. There can only be one; in that case its already-loaded compatible runtime
                // remains authoritative.
            }
            _onnxInstalled = true;
        }
    }

    internal static void ConfigureOpenVino(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        lock (Gate)
        {
            EnsureSameDirectory(_openVinoDirectory, fullPath, "OpenVINO");
            _openVinoDirectory = fullPath;
            if (_openVinoInstalled) return;
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, ResolveOpenVino);
            _openVinoInstalled = true;
        }
    }

    private static void EnsureSameDirectory(string? configuredDirectory, string requestedDirectory, string runtimeName)
    {
        if (configuredDirectory != null &&
            !string.Equals(configuredDirectory, requestedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{runtimeName} was already initialised from {configuredDirectory}; cannot switch to {requestedDirectory}");
        }
    }

    private static IntPtr ResolveOnnxRuntime(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        string? directory = _onnxDirectory;
        if (directory == null) return IntPtr.Zero;
        foreach (string candidate in CandidateFileNames(libraryName))
        {
            string path = Path.Combine(directory, candidate);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle)) return handle;
        }
        return IntPtr.Zero;
    }

    private static IntPtr ResolveOpenVino(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "openvino_c", StringComparison.Ordinal)) return IntPtr.Zero;
        string? directory = _openVinoDirectory;
        if (directory == null) return IntPtr.Zero;
        foreach (string candidate in new[] { "libopenvino_c.so.2232", "libopenvino_c.so" })
        {
            string path = Path.Combine(directory, candidate);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle)) return handle;
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidateFileNames(string libraryName)
    {
        yield return libraryName;
        if (OperatingSystem.IsWindows())
        {
            yield return libraryName + ".dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "lib" + libraryName + ".dylib";
            yield return libraryName + ".dylib";
        }
        else
        {
            yield return "lib" + libraryName + ".so";
            yield return libraryName + ".so";
        }
    }
}
