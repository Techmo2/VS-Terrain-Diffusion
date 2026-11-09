using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.Native;

/// <summary>The standalone OpenVINO C runtime the OpenVINO worker runs the decoder on (64-bit Linux).</summary>
public static partial class OnnxRuntimeBootstrap
{
    /// <summary>
    /// OpenVINO version. Later releases inspect host cache topology through sysfs and can crash when a
    /// restricted container exposes sibling CPU IDs outside its virtual CPU set. The 2022.3 LTS runtime
    /// uses /proc and process affinity instead, and supports both decoder graphs used by the mod.
    /// </summary>
    public const string OpenVinoVersion = "2022.3.2";

    /// <summary>Intel's RHEL 8 archive: the runtime, its CPU plugin and ONNX frontend.</summary>
    private const string OpenVinoArchiveUrl =
        "https://storage.openvinotoolkit.org/repositories/openvino/packages/2022.3.2/linux/" +
        "l_openvino_toolkit_rhel8_2022.3.2.9279.e2c7e4d7b4d_x86_64.tgz";

    /// <summary>The PyPI wheel of the same release, for the TBB, hwloc and pugixml libraries the archive lacks.</summary>
    private const string OpenVinoWheelUrl =
        "https://files.pythonhosted.org/packages/df/6f/44de968108af6194e3a156493a2ab0003a03d51b5dfff9853cee055aa922/" +
        "openvino-2022.3.2-9279-cp310-cp310-manylinux2014_x86_64.whl";

    /// <summary>SHA-256 of <see cref="OpenVinoArchiveUrl"/>.</summary>
    private const string OpenVinoArchiveSha256 =
        "21968bc27dc15463004706fd7d4efa28445fd544e3e977e7580cb7ce1b3ba830";

    /// <summary>SHA-256 of <see cref="OpenVinoWheelUrl"/>.</summary>
    private const string OpenVinoWheelSha256 =
        "ab4a54f0841bdcfe5fd2c654a4a8c0257991aa2aeaf65fe9498cd6b7a20bd84d";

    /// <summary>The installed runtime, in load order.</summary>
    private static readonly (string Name, long Size, string Sha256)[] OpenVinoRuntimeFiles =
    {
        ("libopenvino_c.so.2232", 457176,
            "e6af0851acb20bee97c46cdbe81dfa24f746ec1b27798b85ff73bb594be43c98"),
        ("libopenvino.so.2232", 18474168,
            "57cbe0668c5fd0aaf214b768b32f051b258ef79d652e25d5dc7173fbde1101ea"),
        ("libopenvino_onnx_frontend.so.2232", 5192296,
            "e48e169f3c1877d41ca6e42bcf32f6e2d096e9f62324c1f9d761382a13444fd3"),
        ("libopenvino_intel_cpu_plugin.so", 41362344,
            "eecf898422c883393004300b1dc3c876c6083b8697b2739a5c832af9e0f398ed"),
        ("libtbb.so.2", 261488,
            "dc72de1a3de811d973cd72ebd068767a19a8f9e6a211e5d864da23d9004eb54e"),
        ("libtbbbind.so.2", 78008,
            "c36624ea9065d0e21eb669933bd91a528fefc1f2d61474d4fa5b861837bde927"),
        ("libhwloc.so.5", 254200,
            "278cc4cd04939fd7d4591872a88fffc5b8d9490e0345d5d0d42816e1e6b3ea0a"),
        ("libpugixml.so.1", 249128,
            "ba546fe4e42eb07a14421e4e79fc74ae50b61be30a86e7b3451a2c7eee6339c7"),
        ("plugins.xml", 753,
            "45bb98cee3bf8d8c51499c7e9a3ad789b181a8878e7919cca866e52175782cb2")
    };

    /// <summary>The OpenVINO runtime as a pinned set.</summary>
    private static readonly PinnedFileSet OpenVinoFiles = new("OpenVINO runtime", OpenVinoRuntimeFiles,
        VerificationFileName,
        OpenVinoArchiveSha256 + "  openvino-rhel8.tgz\n" +
        OpenVinoWheelSha256 + "  openvino-manylinux.whl\n");

    /// <summary>Serialises preparation across worlds.</summary>
    private static readonly object OpenVinoGate = new();

    /// <summary>Where the runtime was prepared; kept for the life of the game.</summary>
    private static string _openVinoDirectory;

    /// <summary>Directory containing the standalone OpenVINO C runtime, once prepared.</summary>
    public static string OpenVinoDirectory => _openVinoDirectory;

    /// <summary>
    /// Downloads the standalone OpenVINO C runtime the OpenVINO worker runs the decoder on. It is
    /// separate from ONNX Runtime and does not change <see cref="Provider"/>.
    /// </summary>
    /// <returns>The runtime's directory.</returns>
    /// <exception cref="PlatformNotSupportedException">Not 64-bit Linux.</exception>
    private static string PrepareOpenVino(ILogger logger, CancellationToken cancellation)
    {
        lock (OpenVinoGate)
        {
            if (_openVinoDirectory != null) return _openVinoDirectory;
            if (!HostPlatform.IsLinux || HostPlatform.Architecture != Architecture.X64)
                throw new PlatformNotSupportedException("The standalone OpenVINO runtime requires 64-bit Linux");

            string directory = Path.Combine(
                DiffusionPaths.RuntimeDirectory, "openvino", OpenVinoVersion, "linux-x64");
            EnsurePinned(OpenVinoFiles, directory, () =>
            {
                LoadingNotice.Post(logger, "Downloading the OpenVINO inference runtime. This happens once.");
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                OpenVinoFiles.Install(directory, staging =>
                {
                    foreach (NativeSource source in StandaloneOpenVinoSources())
                        NativeDownloads.Extract(client, source, staging, logger, cancellation);
                }, "The verified OpenVINO archives did not contain a complete runtime");
            });

            _openVinoDirectory = directory;
            logger.Notification("[{0}] OpenVINO {1} standalone CPU runtime prepared in {2}",
                DiffusionPaths.ModId, OpenVinoVersion, directory);
            return directory;
        }
    }

    /// <summary>
    /// The two pinned archives the runtime comes from, with the files each contributes renamed to the
    /// names the runtime loads.
    /// </summary>
    private static IReadOnlyList<NativeSource> StandaloneOpenVinoSources() => new NativeSource[]
    {
        new()
        {
            Name = $"OpenVINO {OpenVinoVersion} (Intel/RHEL 8)",
            Url = OpenVinoArchiveUrl,
            Kind = ArchiveKind.TarGz,
            Sha256 = OpenVinoArchiveSha256,
            FileNames = new[]
            {
                "LICENSE",
                "runtime-third-party-programs.txt",
                "onednn_third-party-programs.txt",
                "tbb_third-party-programs.txt",
                "libopenvino_c.so.2022.3.2",
                "libopenvino.so.2022.3.2",
                "libopenvino_onnx_frontend.so.2022.3.2",
                "libopenvino_intel_cpu_plugin.so",
                "plugins.xml"
            },
            TargetFileNames = new[]
            {
                "LICENSE.txt",
                "runtime-third-party-programs.txt",
                "onednn-third-party-programs.txt",
                "onetbb-third-party-programs.txt",
                "libopenvino_c.so.2232",
                "libopenvino.so.2232",
                "libopenvino_onnx_frontend.so.2232",
                "libopenvino_intel_cpu_plugin.so",
                "plugins.xml"
            }
        },
        new()
        {
            Name = $"OpenVINO {OpenVinoVersion} dependencies (Intel/PyPI)",
            Url = OpenVinoWheelUrl,
            Kind = ArchiveKind.Zip,
            Sha256 = OpenVinoWheelSha256,
            EntryPaths = new[]
            {
                "openvino/libs/libtbb.so.2",
                "openvino/libs/libtbbbind.so.2",
                "openvino/libs/libhwloc.so.5",
                "openvino/libs/libpugixml.so.1"
            }
        }
    };
}
