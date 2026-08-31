using System.Reflection;
using System.Runtime.InteropServices;

namespace VSTerrainDiffusion.Native;

internal static class NativeLibraryResolver
{
    private static string? _directory;

    internal static void ConfigureOpenVino(string directory)
    {
        _directory = Path.GetFullPath(directory);
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "openvino_c" || _directory == null) return IntPtr.Zero;
        foreach (string candidate in new[] { "libopenvino_c.so.2232", "libopenvino_c.so" })
        {
            string path = Path.Combine(_directory, candidate);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle)) return handle;
        }
        return IntPtr.Zero;
    }
}
