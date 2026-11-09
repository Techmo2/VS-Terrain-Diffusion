using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Points the managed ONNX Runtime binding's P/Invoke imports at the build the game prepared. Its
/// imports live in the ONNX Runtime assembly rather than this one, so that assembly gets the
/// resolver. A worker process serves one game and loads one build, so this is set once.
/// </summary>
internal static class NativeLibraryResolver
{
    /// <summary>The ONNX Runtime build's directory, once set.</summary>
    private static string? _directory;

    /// <summary>Resolves the binding's imports from <paramref name="directory"/> from now on.</summary>
    /// <param name="directory">The ONNX Runtime build the game prepared.</param>
    /// <exception cref="InvalidOperationException">Already set; a process loads one build.</exception>
    internal static void UseOnnxRuntime(string directory)
    {
        if (_directory != null) throw new InvalidOperationException("ONNX Runtime is already loaded");
        _directory = Path.GetFullPath(directory);
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, Resolve);
    }

    /// <summary>The import resolver: loads ONNX Runtime libraries from the build's directory.</summary>
    /// <param name="libraryName">The name in the DllImport.</param>
    /// <param name="assembly">The importing assembly.</param>
    /// <param name="searchPath">The import's search path, unused.</param>
    /// <returns>The library handle, or zero to fall back to the default search.</returns>
    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (_directory == null || !libraryName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (string candidate in CandidateFileNames(libraryName))
        {
            string path = Path.Combine(_directory, candidate);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle)) return handle;
        }
        return IntPtr.Zero;
    }

    /// <summary>The file names an import name may have on this platform, bare name first.</summary>
    /// <param name="libraryName">The name in the DllImport, e.g. <c>onnxruntime</c>.</param>
    /// <returns>Candidate file names.</returns>
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
