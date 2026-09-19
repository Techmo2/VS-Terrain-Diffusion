using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

#nullable enable

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Resolves the standalone OpenVINO runtime from its private installation directory. Loading the
/// C API by absolute path is not enough on Linux: its versioned sibling libraries are not searched
/// for beside it unless they are loaded first.
/// </summary>
internal static class OpenVinoLibraryResolver
{
    private static readonly object Gate = new();
    private static readonly List<IntPtr> DependencyHandles = new();
    private static string? _directory;
    private static bool _installed;
    private static bool _dependenciesLoaded;

    internal static void Configure(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        lock (Gate)
        {
            if (_directory != null &&
                !string.Equals(_directory, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"OpenVINO was already initialised from {_directory}; cannot switch to {fullPath}");
            }

            _directory = fullPath;
            if (_installed) return;
            NativeLibrary.SetDllImportResolver(typeof(OpenVinoLibraryResolver).Assembly, Resolve);
            _installed = true;
        }
    }

    private static IntPtr Resolve(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "openvino_c", StringComparison.Ordinal)) return IntPtr.Zero;

        lock (Gate)
        {
            string? directory = _directory;
            if (directory == null) return IntPtr.Zero;
            EnsureDependenciesLoaded(directory);
            return LoadFirst(directory, "OpenVINO C API", "libopenvino_c.so.2232", "libopenvino_c.so");
        }
    }

    private static void EnsureDependenciesLoaded(string directory)
    {
        if (_dependenciesLoaded) return;

        DependencyHandles.Add(LoadFirst(
            directory, "pugixml", "libpugixml.so.1", "libpugixml.so"));
        DependencyHandles.Add(LoadFirst(
            directory, "oneTBB", "libtbb.so.2", "libtbb.so"));
        DependencyHandles.Add(LoadFirst(
            directory, "OpenVINO core", "libopenvino.so.2232", "libopenvino.so"));
        _dependenciesLoaded = true;
    }

    private static IntPtr LoadFirst(string directory, string description, params string[] candidates)
    {
        Exception? failure = null;
        foreach (string candidate in candidates)
        {
            string path = Path.Combine(directory, candidate);
            if (!File.Exists(path)) continue;
            try
            {
                return NativeLibrary.Load(path);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                failure = ex;
            }
        }

        throw new DllNotFoundException(
            $"Could not load {description} from the OpenVINO runtime directory {directory}", failure);
    }
}
