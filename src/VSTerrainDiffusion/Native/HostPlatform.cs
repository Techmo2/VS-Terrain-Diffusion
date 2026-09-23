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
    internal static OSPlatform? OverrideOs;
    internal static Architecture? OverrideArchitecture;

    public static bool IsWindows => OverrideOs is { } os ? os == OSPlatform.Windows : OperatingSystem.IsWindows();

    public static bool IsLinux => OverrideOs is { } os ? os == OSPlatform.Linux : OperatingSystem.IsLinux();

    public static bool IsMacOS => OverrideOs is { } os ? os == OSPlatform.OSX : OperatingSystem.IsMacOS();

    public static Architecture Architecture => OverrideArchitecture ?? RuntimeInformation.OSArchitecture;
}
