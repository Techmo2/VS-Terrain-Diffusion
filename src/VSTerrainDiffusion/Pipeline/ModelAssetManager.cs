using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Ensures the Terrain Diffusion model files exist locally and match the expected SHA-256 hashes.
/// Files come from a pinned Hugging Face commit and land in
/// <c>&lt;VintagestoryData&gt;/TerrainDiffusionModels</c>.
/// </summary>
public static class ModelAssetManager
{
    private const string RepositorySlug = "xandergos/terrain-diffusion-30m-onnx";
    private const string Revision = "ad2df557eca5645f588766101cf3bc3682455c3e";

    private sealed class Asset
    {
        public string FileName;
        public string Sha256;
        public long SizeBytes;
        public string UrlOverride;

        public string Url => UrlOverride ??
            $"https://huggingface.co/{RepositorySlug}/resolve/{Revision}/{FileName}?download=true";
    }

    /// <summary>
    /// Pinned manifest for the commit above. Sizes and hashes come from the Hugging Face
    /// <c>paths-info</c> API; regenerate them with tools/refresh-manifest.sh when bumping the revision.
    /// </summary>
    private static readonly Asset[] Assets =
    {
        new()
        {
            FileName = "coarse_model.onnx",
            SizeBytes = 22497125,
            Sha256 = "d6ca15b21b2e35d5e594a9ac7a4249a2376590c0ad2b5b49a1e6e2d033450008"
        },
        new()
        {
            FileName = "base_model.onnx",
            SizeBytes = 2029994361,
            Sha256 = "543de788f73d0a4012685c908259f615601102aace4751aeccec64154ba145c0"
        },
        new()
        {
            FileName = "decoder_model.onnx",
            SizeBytes = 223854143,
            Sha256 = "6473ae47ca6ec4d743d30fe4f5d381fe4158899714eff09b762005bdbdef68c1"
        },
        new()
        {
            FileName = "pipeline_data.json",
            SizeBytes = 12226,
            Sha256 = "e3132c3ef0c65d8613615f9278ffe23bbd9363ddcd87f1cc6f18456bcc9efe5c"
        },
        new()
        {
            FileName = "world_pipeline_config.json",
            SizeBytes = 774,
            Sha256 = "c60f0b74d89317e64cfc623fbfdd828f1b5b2e50aa75020ac4001103381853bd"
        }
    };

    private static readonly Asset Int8DecoderAsset = new()
    {
        FileName = "decoder_model.int8.onnx",
        SizeBytes = 43496635,
        Sha256 = "0ce6eb771a072a8622448c30488f0505c009e43bebccd65246a4dd58fe8e2da6",
        UrlOverride =
            "https://github.com/Techmo2/VS-Terrain-Diffusion/releases/download/decoder-int8-v1/decoder_model.int8.onnx"
    };

    private static readonly object Gate = new();
    private static bool _ready;

    public static string OfflineHelpUrl => $"https://huggingface.co/{RepositorySlug}/tree/{Revision}";

    /// <summary>Whether this run actually fetched anything, rather than finding it all on disk.</summary>
    public static bool Downloaded { get; private set; }

    /// <summary>
    /// How many bytes still have to come down the wire, judged on file presence and size alone -
    /// the same test <see cref="EnsureSingleAsset"/> applies before it resorts to hashing, so the
    /// figure quoted to the player matches what actually gets fetched. Only a file that passes the
    /// size check and then fails its hash escapes the count.
    /// </summary>
    private static long PendingBytes(bool validate)
    {
        long pending = 0;
        foreach (Asset asset in Assets)
        {
            string path = DiffusionPaths.ResolveAsset(asset.FileName);
            if (!File.Exists(path)) pending += asset.SizeBytes;
            else if (validate && new FileInfo(path).Length != asset.SizeBytes) pending += asset.SizeBytes;
        }
        return pending;
    }

    /// <summary>
    /// Downloads and verifies anything missing. Blocking; call from a background thread.
    /// Throws <see cref="ModelAssetException"/> when the files cannot be made available.
    /// </summary>
    public static void EnsureAssetsReady(ILogger logger, CancellationToken cancellation = default)
    {
        if (_ready) return;
        lock (Gate)
        {
            if (_ready) return;

            Directory.CreateDirectory(DiffusionPaths.ModelDirectory);
            bool validate = DiffusionConfig.Instance.ValidateModelHashes;

            logger.Notification("[{0}] Preparing model assets in {1}", DiffusionPaths.ModId, DiffusionPaths.ModelDirectory);

            // The player is staring at a loading screen while this runs, so say what the wait is
            // for and how big it is. Once only, at the start: the log file has the detail.
            long pending = PendingBytes(validate);
            if (pending > 0)
            {
                Downloaded = true;
                LoadingNotice.Post(logger,
                    "Downloading the world generation models ({0}). This happens once, and the world will " +
                    "finish loading when it completes.", HumanBytes(pending));
            }

            foreach (Asset asset in Assets)
            {
                cancellation.ThrowIfCancellationRequested();
                EnsureSingleAsset(asset, logger, validate, cancellation);
            }

            logger.Notification("[{0}] Model assets ready", DiffusionPaths.ModId);
            _ready = true;
        }
    }

    public static string ResolveAssetPath(string fileName) => DiffusionPaths.ResolveAsset(fileName);

    /// <summary>
    /// Fetches the mixed-precision decoder when the selected provider needs it. Failure is not
    /// fatal because the verified FP32 decoder is always available as a fallback.
    /// </summary>
    public static void EnsureOptionalDecoderReady(ILogger logger,
                                                   CancellationToken cancellation = default)
    {
        if (!ShouldUseInt8Decoder()) return;

        lock (Gate)
        {
            bool validate = DiffusionConfig.Instance.ValidateModelHashes;
            string path = DiffusionPaths.ResolveAsset(Int8DecoderAsset.FileName);
            bool needsDownload = !File.Exists(path) ||
                                 new FileInfo(path).Length != Int8DecoderAsset.SizeBytes;
            if (needsDownload)
            {
                Downloaded = true;
                LoadingNotice.Post(logger,
                    "Downloading the optional INT8 decoder ({0}). This happens once.",
                    HumanBytes(Int8DecoderAsset.SizeBytes));
            }

            try
            {
                EnsureSingleAsset(Int8DecoderAsset, logger, validate, cancellation);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                logger.Warning(
                    "[{0}] Could not prepare the INT8 decoder ({1}); using the FP32 decoder",
                    DiffusionPaths.ModId, e.Message);
            }
        }
    }

    /// <summary>
    /// Selects the optional mixed-precision decoder. The original decoder remains the safe
    /// fallback, and is still downloaded and verified with the rest of the model set.
    /// </summary>
    public static string ResolveDecoderPath(ILogger logger)
    {
        string originalPath = ResolveAssetPath("decoder_model.onnx");
        if (!ShouldUseInt8Decoder()) return originalPath;

        string int8Path = ResolveAssetPath(Int8DecoderAsset.FileName);
        if (!File.Exists(int8Path))
        {
            logger.Notification("[{0}] The INT8 decoder is not installed; using the FP32 decoder",
                DiffusionPaths.ModId);
            return originalPath;
        }

        var info = new FileInfo(int8Path);
        bool invalidSize = info.Length != Int8DecoderAsset.SizeBytes;
        bool invalidHash = DiffusionConfig.Instance.ValidateModelHashes && !invalidSize &&
                           !Sha256Hex(int8Path).Equals(Int8DecoderAsset.Sha256, StringComparison.OrdinalIgnoreCase);
        if (invalidSize || invalidHash)
        {
            logger.Warning("[{0}] '{1}' failed verification; using the FP32 decoder",
                DiffusionPaths.ModId, Int8DecoderAsset.FileName);
            return originalPath;
        }

        logger.Notification("[{0}] Using the mixed-precision INT8 decoder ({1})",
            DiffusionPaths.ModId, HumanBytes(info.Length));
        return int8Path;
    }

    private static bool ShouldUseInt8Decoder()
    {
        string precision = DiffusionConfig.Instance.DecoderPrecision;
        return precision == "int8" ||
               precision == "auto" &&
               DiffusionConfig.Instance.InferenceDevice == "openvino" &&
               OperatingSystem.IsLinux() &&
               RuntimeInformation.OSArchitecture == Architecture.X64;
    }

    private static void EnsureSingleAsset(Asset asset, ILogger logger, bool validate,
                                          CancellationToken cancellation)
    {
        string path = DiffusionPaths.ResolveAsset(asset.FileName);
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            if (!validate)
            {
                logger.Notification("[{0}] Using existing '{1}' without hash validation", DiffusionPaths.ModId, asset.FileName);
                return;
            }
            if (info.Length == asset.SizeBytes && (asset.Sha256 == null || Sha256Hex(path).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                logger.Notification("[{0}] Verified '{1}'", DiffusionPaths.ModId, asset.FileName);
                return;
            }

            // A file that looked complete but failed its hash was not in the pending total, so the
            // player was told nothing was being fetched. Rare, and worth its own line.
            logger.Warning("[{0}] '{1}' failed verification, re-downloading", DiffusionPaths.ModId, asset.FileName);
            if (info.Length == asset.SizeBytes && !Downloaded)
            {
                Downloaded = true;
                LoadingNotice.Post(logger, "Re-downloading a world generation model that did not verify.");
            }
            File.Delete(path);
        }

        DownloadAndVerify(asset, path, logger, cancellation);
    }

    private static void DownloadAndVerify(Asset asset, string path, ILogger logger,
                                          CancellationToken cancellation)
    {
        string tempPath = path + ".tmp";
        try
        {
            logger.Notification("[{0}] Downloading '{1}' ({2})",
                DiffusionPaths.ModId, asset.FileName, HumanBytes(asset.SizeBytes));

            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using HttpResponseMessage response = client
                .GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                throw new ModelAssetException(
                    $"Failed to download {asset.FileName} (HTTP {(int)response.StatusCode}). " +
                    $"Direct download: {asset.Url}");
            }

            using (Stream netStream = response.Content.ReadAsStreamAsync(cancellation).GetAwaiter().GetResult())
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                Copy(netStream, fileStream, cancellation);
            }

            var info = new FileInfo(tempPath);
            if (info.Length != asset.SizeBytes)
            {
                throw new ModelAssetException(
                    $"{asset.FileName} downloaded with unexpected size {info.Length} (expected {asset.SizeBytes}).");
            }
            if (asset.Sha256 != null)
            {
                string actual = Sha256Hex(tempPath);
                if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ModelAssetException(
                        $"SHA-256 mismatch for {asset.FileName}. Expected {asset.Sha256} but got {actual}.");
                }
            }

            File.Move(tempPath, path, overwrite: true);
            logger.Notification("[{0}] Downloaded and verified '{1}'", DiffusionPaths.ModId, asset.FileName);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception e)
        {
            TryDelete(tempPath);
            if (e is ModelAssetException) throw;
            if (IsOfflineError(e))
            {
                throw new ModelAssetException(
                    "The Terrain Diffusion models are missing and must be downloaded while online. " +
                    "Connect to the internet and restart the server, or place the files manually in " +
                    DiffusionPaths.ModelDirectory + ". Direct download: " + asset.Url, e);
            }
            throw new ModelAssetException("Failed downloading " + asset.FileName + ": " + e.Message, e);
        }
    }

    private static void Copy(Stream source, Stream destination, CancellationToken cancellation)
    {
        var buffer = new byte[1 << 20];

        while (true)
        {
            int read = source.ReadAsync(buffer.AsMemory(), cancellation)
                .AsTask().GetAwaiter().GetResult();
            if (read == 0) break;
            destination.Write(buffer, 0, read);
        }
    }

    private static bool IsOfflineError(Exception e)
    {
        for (Exception current = e; current != null; current = current.InnerException)
        {
            if (current is System.Net.Sockets.SocketException) return true;
            if (current is HttpRequestException) return true;
            if (current is TaskCanceledException) return true;
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    internal static string Sha256Hex(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    internal static string HumanBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}

/// <summary>Raised when the model files cannot be made available locally.</summary>
public class ModelAssetException : Exception
{
    public ModelAssetException(string message) : base(message) { }
    public ModelAssetException(string message, Exception inner) : base(message, inner) { }
}
