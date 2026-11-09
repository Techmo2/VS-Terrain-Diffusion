using System;
using System.Runtime.InteropServices;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// The operating system and architecture the native runtime is chosen for. It is the real machine,
/// except that a test harness can set the overrides (by reflection) to have
/// <see cref="OnnxRuntimeBootstrap"/> pick and download another platform's files on this one. Nothing
/// in the mod sets them.
/// </summary>
internal static class HostPlatform
{
    /// <summary>
    /// The operating system to pretend to be, or null for the real one. Test harnesses only.
    /// </summary>
    internal static OSPlatform? OverrideOs;

    /// <summary>The architecture to pretend to be, or null for the real one. Test harnesses only.</summary>
    internal static Architecture? OverrideArchitecture;

    /// <summary>Whether the (possibly overridden) platform is Windows.</summary>
    public static bool IsWindows => OverrideOs is { } os ? os == OSPlatform.Windows : OperatingSystem.IsWindows();

    /// <summary>Whether the (possibly overridden) platform is Linux.</summary>
    public static bool IsLinux => OverrideOs is { } os ? os == OSPlatform.Linux : OperatingSystem.IsLinux();

    /// <summary>Whether the (possibly overridden) platform is macOS.</summary>
    public static bool IsMacOS => OverrideOs is { } os ? os == OSPlatform.OSX : OperatingSystem.IsMacOS();

    /// <summary>The (possibly overridden) processor architecture.</summary>
    public static Architecture Architecture => OverrideArchitecture ?? RuntimeInformation.OSArchitecture;
}
