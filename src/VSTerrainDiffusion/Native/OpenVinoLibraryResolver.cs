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
    /// <summary>Serialises configuration and loading.</summary>
    private static readonly object Gate = new();

    /// <summary>Handles of the libraries the C API links against, kept so they stay loaded.</summary>
    private static readonly List<IntPtr> DependencyHandles = new();

    /// <summary>The OpenVINO runtime directory, once configured; fixed for the life of the process.</summary>
    private static string? _directory;

    /// <summary>Whether the import resolver is registered on this assembly.</summary>
    private static bool _installed;

    /// <summary>Whether <see cref="DependencyHandles"/> has been filled.</summary>
    private static bool _dependenciesLoaded;

    /// <summary>
    /// Points the <c>openvino_c</c> imports at a runtime directory and registers the resolver the first time.
    /// </summary>
    /// <param name="directory">The OpenVINO runtime directory.</param>
    /// <remarks>
    /// A process can only use one OpenVINO runtime, so a second, different directory is refused.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Already configured with another directory.</exception>
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

    /// <summary>
    /// The import resolver: loads the C API, after its dependencies, for <c>openvino_c</c> and defers
    /// anything else to the default search.
    /// </summary>
    /// <param name="libraryName">The name in the DllImport.</param>
    /// <param name="assembly">The importing assembly.</param>
    /// <param name="searchPath">The import's search path, unused.</param>
    /// <returns>The library handle, or zero to fall back.</returns>
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

    /// <summary>
    /// Loads pugixml, oneTBB and OpenVINO core by absolute path, once, so the C API finds them.
    /// </summary>
    /// <param name="directory">The OpenVINO runtime directory.</param>
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

    /// <summary>Loads the first of several file names that exists and loads.</summary>
    /// <param name="directory">The directory to look in.</param>
    /// <param name="description">What the library is, for the error.</param>
    /// <param name="candidates">File names, most specific first.</param>
    /// <returns>The library handle.</returns>
    /// <exception cref="DllNotFoundException">None of them loaded.</exception>
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
