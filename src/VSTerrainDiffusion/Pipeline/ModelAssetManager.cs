using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
    private static readonly Asset[] CommonAssets =
    {
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

    private static readonly Asset Fp32CoarseAsset = new()
    {
        FileName = "coarse_model.onnx",
        SizeBytes = 22497125,
        Sha256 = "d6ca15b21b2e35d5e594a9ac7a4249a2376590c0ad2b5b49a1e6e2d033450008"
    };

    private static readonly Asset Fp32BaseAsset = new()
    {
        FileName = "base_model.onnx",
        SizeBytes = 2029994361,
        Sha256 = "543de788f73d0a4012685c908259f615601102aace4751aeccec64154ba145c0"
    };

    private static readonly Asset Fp32DecoderAsset = new()
    {
        FileName = "decoder_model.onnx",
        SizeBytes = 223854143,
        Sha256 = "6473ae47ca6ec4d743d30fe4f5d381fe4158899714eff09b762005bdbdef68c1"
    };

    /// <summary>An asset published by one of this repository's model releases.</summary>
    private static string ReleaseUrl(string tag, string fileName) =>
        $"https://github.com/Techmo2/VS-Terrain-Diffusion/releases/download/{tag}/{fileName}";

    /// <summary>
    /// Half-precision exports for GPU providers: float32 in and out, narrower maths inside. Each is
    /// a separate export, not a conversion of the file above, because a naive conversion of the base
    /// model overflows its conditioning normalisation. Built by the *-fp16-release workflows, whose
    /// pinned hashes and these are updated together.
    /// </summary>
    private static readonly Asset Fp16CoarseAsset = new()
    {
        FileName = "coarse_model.fp16.onnx",
        SizeBytes = 5686151,
        Sha256 = "f971de460284fe8d0e2a4467f2d5ec6673c16b6f4222b8a5f340e14c125fd44e",
        // No release yet: the coarse model is not worth shipping in fp16 until its
        // layernorms are re-exported, so this points at the tag it would use.
        UrlOverride = ReleaseUrl("coarse-fp16-v1", "coarse_model.fp16.onnx")
    };

    private static readonly Asset Fp16BaseAsset = new()
    {
        FileName = "base_model.fp16.onnx",
        SizeBytes = 507810847,
        Sha256 = "fcb4ddd9a9f4b6aebcfc9663d40173128c2c0f1a105bf10a286321cf4a521647",
        UrlOverride = ReleaseUrl("base-fp16-v1", "base_model.fp16.onnx")
    };

    /// <summary>
    /// H/W are fixed at export, so the file name and release tag carry the window. 256 is what the
    /// decoder stage asks for (<see cref="WorldPipeline.DecoderTileSize"/>).
    /// </summary>
    private static readonly Asset Fp16DecoderAsset = new()
    {
        FileName = "decoder_model.fp16.256.onnx",
        SizeBytes = 56264771,
        Sha256 = "2429cf246bf8905b17763bb91edd0adbe5a8dc09be7c2228a62e3ad968f0fb8e",
        UrlOverride = ReleaseUrl("decoder-fp16-256-v1", "decoder_model.fp16.256.onnx")
    };

    private static readonly Asset Int8DecoderAsset = new()
    {
        FileName = "decoder_model.int8.onnx",
        SizeBytes = 43497445,
        Sha256 = "0ae464c884593b3016a19365caf3ae43a7e26743c8ef1234814e10bbbd9b5b74",
        UrlOverride =
            "https://github.com/Techmo2/VS-Terrain-Diffusion/releases/download/decoder-int8-v1/decoder_model.int8.onnx"
    };

    private static readonly object Gate = new();

    /// <summary>
    /// The set of files the last successful preparation covered, or null. Keyed on the selection
    /// rather than a flag because precision can change between two worlds in one session - ConfigLib
    /// edits the config live - and the newly selected model then still has to be fetched.
    /// </summary>
    private static string _preparedSelection;

    private static Asset SelectedDecoderAsset => DiffusionConfig.Instance.DecoderPrecision switch
    {
        "int8" => Int8DecoderAsset,
        "fp16" => Fp16DecoderAsset,
        _ => Fp32DecoderAsset
    };

    private static Asset SelectedCoarseAsset =>
        DiffusionConfig.Instance.CoarsePrecision == "fp16" ? Fp16CoarseAsset : Fp32CoarseAsset;

    private static Asset SelectedBaseAsset =>
        DiffusionConfig.Instance.BasePrecision == "fp16" ? Fp16BaseAsset : Fp32BaseAsset;

    public static string OfflineHelpUrl => $"https://huggingface.co/{RepositorySlug}/tree/{Revision}";

    /// <summary>Whether this run actually fetched anything, rather than finding it all on disk.</summary>
    public static bool Downloaded { get; private set; }

    /// <summary>
    /// How many bytes still have to come down the wire, judged on file presence and size alone -
    /// the same test <see cref="EnsureSingleAsset"/> applies before it resorts to hashing, so the
    /// figure quoted to the player matches what actually gets fetched. Only a file that passes the
    /// size check and then fails its hash escapes the count.
    /// </summary>
    private static long PendingBytes()
    {
        long pending = 0;
        foreach (Asset asset in RequiredAssets())
        {
            string path = DiffusionPaths.ResolveAsset(asset.FileName);
            if (!File.Exists(path)) pending += asset.SizeBytes;
            else if (new FileInfo(path).Length != asset.SizeBytes) pending += asset.SizeBytes;
        }
        return pending;
    }

    private static IEnumerable<Asset> RequiredAssets()
    {
        foreach (Asset asset in CommonAssets) yield return asset;
        yield return SelectedCoarseAsset;
        yield return SelectedBaseAsset;
        yield return SelectedDecoderAsset;
    }

    /// <summary>
    /// Downloads and verifies anything missing. Blocking; call from a background thread.
    /// Throws <see cref="ModelAssetException"/> when the files cannot be made available.
    /// </summary>
    public static void EnsureAssetsReady(ILogger logger, CancellationToken cancellation = default)
    {
        string selection = CurrentSelection();
        if (_preparedSelection == selection) return;
        lock (Gate)
        {
            if (_preparedSelection == selection) return;

            Downloaded = false;
            Directory.CreateDirectory(DiffusionPaths.ModelDirectory);
            bool validate = DiffusionConfig.Instance.ValidateModelHashes;

            logger.Notification("[{0}] Preparing model assets in {1}", DiffusionPaths.ModId, DiffusionPaths.ModelDirectory);

            // The player is staring at a loading screen while this runs, so say what the wait is
            // for, how big it is, and that it is still moving.
            var progress = new DownloadProgress(logger, "model files", PendingBytes());
            if (progress.Active) Downloaded = true;
            progress.Announce();

            foreach (Asset asset in RequiredAssets())
            {
                cancellation.ThrowIfCancellationRequested();
                EnsureSingleAsset(asset, logger, validate, progress, cancellation);
            }

            progress.Complete();

            logger.Notification("[{0}] Model assets ready", DiffusionPaths.ModId);
            _preparedSelection = selection;
        }
    }

    /// <summary>The files the current configuration asks for, as a comparable key.</summary>
    private static string CurrentSelection()
    {
        var names = new List<string>();
        foreach (Asset asset in RequiredAssets()) names.Add(asset.FileName);
        return string.Join('|', names);
    }

    public static string ResolveAssetPath(string fileName) => DiffusionPaths.ResolveAsset(fileName);

    /// <summary>
    /// Returns the decoder selected by the machine configuration. Asset preparation is deliberately
    /// strict: silently changing precision after a download failure would change newly generated
    /// terrain on the next successful start.
    /// </summary>
    public static string ResolveDecoderPath(ILogger logger) =>
        ResolveSelectedModel(SelectedDecoderAsset, "Decoder", DiffusionConfig.Instance.DecoderPrecision, logger);

    /// <summary>The coarse model at the configured precision.</summary>
    public static string ResolveCoarsePath(ILogger logger) =>
        ResolveSelectedModel(SelectedCoarseAsset, "Coarse model", DiffusionConfig.Instance.CoarsePrecision, logger);

    /// <summary>The base (latent) model at the configured precision.</summary>
    public static string ResolveBasePath(ILogger logger) =>
        ResolveSelectedModel(SelectedBaseAsset, "Base model", DiffusionConfig.Instance.BasePrecision, logger);

    private static string ResolveSelectedModel(Asset asset, string label, string precision, ILogger logger)
    {
        if (_preparedSelection == null)
            throw new InvalidOperationException("Terrain Diffusion model assets have not been prepared");

        string path = ResolveAssetPath(asset.FileName);
        if (!File.Exists(path) || new FileInfo(path).Length != asset.SizeBytes)
            throw new ModelAssetException($"The selected {label.ToLowerInvariant()} '{asset.FileName}' is unavailable");

        logger.Notification("[{0}] {1} precision: {2} ({3})", DiffusionPaths.ModId,
            label, precision.ToUpperInvariant(), HumanBytes(asset.SizeBytes));
        return path;
    }

    private static void EnsureSingleAsset(Asset asset, ILogger logger, bool validate,
                                          DownloadProgress progress, CancellationToken cancellation)
    {
        string path = DiffusionPaths.ResolveAsset(asset.FileName);
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            bool validSize = info.Length == asset.SizeBytes;
            if (validSize && !validate)
            {
                logger.Notification("[{0}] Using existing '{1}' without hash validation", DiffusionPaths.ModId, asset.FileName);
                return;
            }
            if (validSize && (asset.Sha256 == null || Sha256Hex(path).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                logger.Notification("[{0}] Verified '{1}'", DiffusionPaths.ModId, asset.FileName);
                return;
            }

            // A file that looked complete but failed its hash was not in the pending total - only
            // hashing finds it - so its bytes join the total now rather than pushing past 100%.
            logger.Warning("[{0}] '{1}' failed verification, re-downloading", DiffusionPaths.ModId, asset.FileName);
            if (validSize)
            {
                Downloaded = true;
                progress.AddPending(asset.SizeBytes);
            }
            File.Delete(path);
        }

        DownloadAndVerify(asset, path, logger, progress, cancellation);
    }

    private static void DownloadAndVerify(Asset asset, string path, ILogger logger,
                                          DownloadProgress progress, CancellationToken cancellation)
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
                Copy(netStream, fileStream, progress, cancellation);
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

    private static void Copy(Stream source, Stream destination, DownloadProgress progress,
                             CancellationToken cancellation)
    {
        var buffer = new byte[1 << 20];

        while (true)
        {
            int read = source.ReadAsync(buffer.AsMemory(), cancellation)
                .AsTask().GetAwaiter().GetResult();
            if (read == 0) break;
            destination.Write(buffer, 0, read);
            progress?.Advance(read);
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
