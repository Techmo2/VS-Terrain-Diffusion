using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Ensures the Terrain Diffusion model files exist locally and match the expected SHA-256 hashes.
/// Files come from a pinned Hugging Face commit and land in
/// <c>&lt;VintagestoryData&gt;/TerrainDiffusionModels</c>.
/// </summary>
public static partial class ModelAssetManager
{
    /// <summary>Serialises preparation.</summary>
    private static readonly object Gate = new();

    /// <summary>
    /// The set of files the last successful preparation covered, or null. Keyed on the selection
    /// rather than a flag because precision can change between two worlds in one session - ConfigLib
    /// edits the config live - and the newly selected model then still has to be fetched.
    /// </summary>
    private static string _preparedSelection;

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
            bool validate = InferenceSettings.Current.ValidateModelHashes;

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

    /// <summary>The full path of a model file, wherever the mod finds it.</summary>
    /// <param name="fileName">The file's name.</param>
    /// <returns>The path.</returns>
    public static string ResolveAssetPath(string fileName) => DiffusionPaths.ResolveAsset(fileName);

    /// <summary>
    /// Returns the decoder selected by the machine configuration. Asset preparation is deliberately
    /// strict: silently changing precision after a download failure would change newly generated
    /// terrain on the next successful start.
    /// </summary>
    public static string ResolveDecoderPath(ILogger logger) =>
        ResolveSelectedModel(SelectedDecoderAsset, "Decoder", InferenceSettings.Current.DecoderPrecision, logger);

    /// <summary>The coarse model, which only comes in FP32.</summary>
    public static string ResolveCoarsePath(ILogger logger) =>
        ResolveSelectedModel(SelectedCoarseAsset, "Coarse model", "fp32", logger);

    /// <summary>The base (latent) model at the configured precision.</summary>
    public static string ResolveBasePath(ILogger logger) =>
        ResolveSelectedModel(SelectedBaseAsset, "Base model", InferenceSettings.Current.BasePrecision, logger);

    /// <summary>
    /// The path of a selected model, which preparation must already have fetched; logs the precision in use.
    /// </summary>
    /// <param name="asset">The model.</param>
    /// <param name="label">What it is, for the log.</param>
    /// <param name="precision">Its precision, for the log.</param>
    /// <param name="logger">The server log.</param>
    /// <returns>The path.</returns>
    /// <exception cref="InvalidOperationException">Nothing was prepared.</exception>
    /// <exception cref="ModelAssetException">The file is missing or the wrong size.</exception>
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

    /// <summary>
    /// Makes one file present and, if asked, verified: a file of the right size is kept (and hashed when
    /// validating), anything else is downloaded again.
    /// </summary>
    /// <param name="asset">The file.</param>
    /// <param name="logger">The server log.</param>
    /// <param name="validate">Whether to hash files already present.</param>
    /// <param name="progress">The download progress to report to.</param>
    /// <param name="cancellation">Stops the download.</param>
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

    /// <summary>
    /// Downloads a file beside its destination, checks its size and hash, and moves it into place.
    /// </summary>
    /// <param name="asset">The file.</param>
    /// <param name="path">Its destination.</param>
    /// <param name="logger">The server log.</param>
    /// <param name="progress">The download progress to report to.</param>
    /// <param name="cancellation">Stops the download.</param>
    /// <exception cref="ModelAssetException">The download failed, was offline, or did not verify.</exception>
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

    /// <summary>Copies a download to disk in 1 MiB pieces, reporting progress.</summary>
    /// <param name="source">The response body.</param>
    /// <param name="destination">The file.</param>
    /// <param name="progress">The download progress, or null.</param>
    /// <param name="cancellation">Stops the copy.</param>
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

    /// <summary>
    /// Whether a failure looks like no network: a socket error, an HTTP request error or a timeout anywhere
    /// in its chain.
    /// </summary>
    /// <param name="e">The failure.</param>
    /// <returns>True if offline.</returns>
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

    /// <summary>Deletes a file if it exists, ignoring failure.</summary>
    /// <param name="path">The file.</param>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    /// <summary>A file's SHA-256.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Lower-case hex.</returns>
    internal static string Sha256Hex(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>A size in B, KB, MB or GB, for messages.</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>e.g. "1.89 GB".</returns>
    internal static string HumanBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
