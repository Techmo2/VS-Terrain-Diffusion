using System;
using System.Runtime.InteropServices;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Hands memory freed by the ONNX runtime back to the operating system.
///
/// Tearing the models down frees well over a gigabyte of native allocations, but glibc keeps that
/// space mapped for reuse, so a client that creates several worlds in one session appears to grow
/// by gigabytes per world in a memory profiler even though nothing is leaking. There is nothing
/// left in this process that wants those arenas - the next world rebuilds its own - so it is worth
/// asking for them back. Measured over five world teardowns this holds resident memory flat at
/// about 1.1 GB instead of climbing past 4 GB.
/// </summary>
internal static class NativeHeap
{
    [DllImport("libc", EntryPoint = "malloc_trim", SetLastError = false)]
    private static extern int malloc_trim(nuint pad);

    /// <summary>
    /// Releases free heap arenas on Linux. A no-op everywhere else, and on any glibc that does not
    /// export the call (musl, most notably).
    /// </summary>
    public static void ReleaseFreeArenas()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

        try
        {
            malloc_trim(0);
        }
        catch (Exception)
        {
            // Nothing here is required for correctness; a runtime without the symbol simply keeps
            // the arenas mapped.
        }
    }
}
