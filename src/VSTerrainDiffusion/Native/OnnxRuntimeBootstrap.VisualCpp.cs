using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// The Visual C++ runtime every Windows x64 library this mod downloads imports, for machines that do
/// not have a current redistributable installed.
/// </summary>
public static partial class OnnxRuntimeBootstrap
{
    /// <summary>
    /// The Visual C++ 2015-2022 runtime. Every Windows x64 library this mod downloads imports it -
    /// ONNX Runtime, DirectML, the CUDA libraries, TensorRT RTX - and it is not part of Windows, so a
    /// machine without the redistributable installed gets it from Microsoft's VCLibs desktop
    /// framework package: a signed, versioned ZIP of the same DLLs the redistributable installs.
    /// This version exports every one of the 222 runtime functions those libraries import. The ARM64
    /// builds link the runtime statically and need nothing.
    /// </summary>
    private const string VcRuntimeVersion = "14.39.33321";

    /// <summary>Microsoft's VCLibs desktop framework package.</summary>
    private const string VcRuntimePackageUrl =
        "https://download.microsoft.com/download/4/7/c/47c6134b-d61f-4024-83bd-b9c9ea951c25/" +
        "Microsoft.VCLibs.x64.14.00.Desktop.appx";

    /// <summary>SHA-256 of <see cref="VcRuntimePackageUrl"/>.</summary>
    private const string VcRuntimePackageSha256 =
        "b56a9101f706f9d95f815f5b7fa6efbac972e86573d378b96a07cff5540c5961";

    /// <summary>Size of <see cref="VcRuntimePackageUrl"/>, for the download notice.</summary>
    private const long VcRuntimePackageBytes = 6764349;

    /// <summary>In load order: each after the ones it imports.</summary>
    private static readonly (string Name, long Size, string Sha256)[] VcRuntimeFiles =
    {
        ("vcruntime140.dll", 109088,
            "d5a99b38807c3df803876ce5f1e71dc43cd7ac2c882582387e550941bc99cd00"),
        ("vcruntime140_1.dll", 39360,
            "68f948ea61695f85ff24d249077e312abb23267b3adc43679d35d781e9aa0244"),
        ("msvcp140.dll", 563232,
            "6fdc2c6fc1362c317ed65ae331dda329d4d03826feb8d79d635eaf8ba49764bc"),
        ("msvcp140_1.dll", 25536,
            "6284349a2262b2329aa75aa8559980f9b2775dc5ec3a9e9ecdf55648ac7f625a")
    };

    /// <summary>The runtime as a pinned set.</summary>
    private static readonly PinnedFileSet VcRuntime = new("Visual C++ runtime", VcRuntimeFiles,
        VerificationFileName, VcRuntimePackageSha256 + "  Microsoft.VCLibs.x64.14.00.Desktop.appx\n");

    /// <summary>Serialises preparation across worlds.</summary>
    private static readonly object VcRuntimeGate = new();

    /// <summary>The libraries to preload, once prepared; empty when the machine's own runtime is current.</summary>
    private static string[] _vcRuntimePreload;

    /// <summary>
    /// The Visual C++ runtime for the worker to load before anything that imports it, on 64-bit
    /// Windows only. A machine whose own runtime is at least <see cref="VcRuntimeVersion"/> keeps
    /// using it and nothing is returned; otherwise the pinned copy is downloaded once and its files
    /// returned, so the worker loads them by absolute path and every library after binds to them.
    /// </summary>
    /// <returns>Absolute paths to preload, in load order; empty when nothing is needed.</returns>
    /// <exception cref="InvalidOperationException">Downloads are off and the machine's runtime is missing or old.</exception>
    private static string[] PrepareVisualCppRuntime(ILogger logger, CancellationToken cancellation)
    {
        if (!HostPlatform.IsWindows || HostPlatform.Architecture != Architecture.X64) return Array.Empty<string>();

        lock (VcRuntimeGate)
        {
            if (_vcRuntimePreload != null) return _vcRuntimePreload;

            if (SystemVisualCppRuntimeIsCurrent(out string found, out _))
            {
                logger.Notification("[{0}] Using the Visual C++ runtime installed on this machine ({1}).",
                    DiffusionPaths.ModId, found);
                return _vcRuntimePreload = Array.Empty<string>();
            }

            string directory = Path.Combine(
                DiffusionPaths.RuntimeDirectory, "vc-runtime", VcRuntimeVersion, CurrentRid());
            if (!VcRuntime.IsPresent(directory) || !VcRuntime.IsVerified(directory))
            {
                // Unlike the other add-ons, files without a marker are not accepted.
                if (!InferenceSettings.Current.DownloadRuntime)
                {
                    throw new InvalidOperationException(
                        "Runtime downloads are disabled and this machine's Visual C++ runtime is missing or older " +
                        $"than {VcRuntimeVersion} ({found}). Install the latest Microsoft Visual C++ " +
                        $"Redistributable (x64), or turn {DownloadRuntimeCode} back on.");
                }

                Downloaded = true;
                LoadingNotice.Post(logger,
                    "Downloading the Visual C++ runtime ({0}). This happens once.",
                    ModelAssetManager.HumanBytes(VcRuntimePackageBytes));
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                VcRuntime.Install(directory, staging => NativeDownloads.ExtractVerifiedZip(client, new NativeSource
                {
                    Name = $"Visual C++ runtime {VcRuntimeVersion} (Microsoft VCLibs)",
                    Url = VcRuntimePackageUrl,
                    Kind = ArchiveKind.Zip,
                    Sha256 = VcRuntimePackageSha256,
                    EntryPaths = Array.ConvertAll(VcRuntimeFiles, f => f.Name)
                }, staging, logger, cancellation), "The verified VCLibs package did not contain the Visual C++ runtime");
            }

            logger.Notification("[{0}] Visual C++ runtime {1} prepared in {2} (this machine has {3}).",
                DiffusionPaths.ModId, VcRuntimeVersion, directory, found);
            return _vcRuntimePreload = VcRuntime.PathsIn(directory);
        }
    }

    /// <summary>
    /// Whether all four runtime DLLs resolve on this machine at <see cref="VcRuntimeVersion"/> or newer.
    /// The redistributable is backward compatible, so a newer one serves as well as the pinned copy.
    /// </summary>
    /// <param name="found">Each DLL with its version, or "missing", for the log.</param>
    /// <param name="resident">An outdated DLL that was already loaded before this looked, or null.</param>
    private static bool SystemVisualCppRuntimeIsCurrent(out string found, out string resident)
    {
        var minimum = new Version(VcRuntimeVersion);
        var parts = new List<string>();
        bool current = true;
        resident = null;

        foreach ((string name, _, _) in VcRuntimeFiles)
        {
            string path = LoadedModulePath(name);
            bool wasLoaded = path != null;
            IntPtr probe = IntPtr.Zero;
            try
            {
                if (path == null && NativeLibrary.TryLoad(name, out probe)) path = LoadedModulePath(name);
                Version version = path == null ? null : ModuleVersion(path);
                parts.Add($"{name} {version?.ToString() ?? "missing"}");
                if (version != null && version >= minimum) continue;

                current = false;
                if (wasLoaded) resident = $"{path}, {version?.ToString() ?? "unknown version"}";
            }
            catch
            {
                parts.Add($"{name} unreadable");
                current = false;
            }
            finally
            {
                // Only probing: unloaded again so the pinned copy can take the name if it is needed.
                if (probe != IntPtr.Zero) NativeLibrary.Free(probe);
            }
        }

        found = string.Join(", ", parts);
        return current;
    }

    /// <summary>
    /// The path a module of this name is loaded from in this process, or null if it is not loaded.
    /// </summary>
    private static string LoadedModulePath(string moduleName)
    {
        using Process process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            if (string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                return module.FileName;
        }
        return null;
    }

    /// <summary>A library's file version (major.minor.build) from its version resource.</summary>
    private static Version ModuleVersion(string path)
    {
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }
}
