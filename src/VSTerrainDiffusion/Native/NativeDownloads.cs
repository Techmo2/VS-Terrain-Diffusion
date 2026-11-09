using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Fetching native files out of remote archives: range-read ZIP entries, streamed tarballs, and
/// whole archives checked against a pinned SHA-256 first; plus the atomic directory swap the
/// installers finish with.
/// </summary>
internal static class NativeDownloads
{
    /// <summary>Buffer for streaming downloads and file copies.</summary>
    private const int BufferBytes = 1 << 20;

    /// <summary>Extracts a source's files into <paramref name="directory"/>, by its archive kind.</summary>
    /// <param name="client">The HTTP client to download with.</param>
    /// <param name="source">The archive and the files to take from it.</param>
    /// <param name="directory">Where the files go.</param>
    /// <param name="logger">The server log, for progress.</param>
    /// <param name="cancellation">Stops the download.</param>
    public static void Extract(HttpClient client, NativeSource source, string directory, ILogger logger,
                               CancellationToken cancellation)
    {
        if (source.Kind == ArchiveKind.TarGz)
            ExtractFromTarGz(client, source, directory, logger, cancellation);
        else
            ExtractFromZip(client, source, directory, logger, cancellation);
    }

    /// <summary>
    /// Extracts ZIP entries: a pinned archive (<see cref="NativeSource.Sha256"/> set) is downloaded
    /// whole and verified, otherwise only the wanted entries are range-read. A target that already
    /// has the entry's size is kept.
    /// </summary>
    public static void ExtractFromZip(HttpClient client, NativeSource source, string directory,
                                      ILogger logger, CancellationToken cancellation)
    {
        if (source.TargetFileNames != null && source.TargetFileNames.Length != source.EntryPaths.Length)
        {
            throw new InvalidDataException($"{source.Name} has mismatched archive and destination file lists");
        }

        if (!string.IsNullOrEmpty(source.Sha256))
        {
            ExtractVerifiedZip(client, source, directory, logger, cancellation);
            return;
        }

        List<RemoteZipExtractor.Entry> entries = RemoteZipExtractor.ReadCentralDirectory(client, source.Url, cancellation);

        for (int i = 0; i < source.EntryPaths.Length; i++)
        {
            string wanted = source.EntryPaths[i];
            RemoteZipExtractor.Entry entry = entries.Find(e =>
                string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                throw new FileNotFoundException($"{source.Name} does not contain {wanted}");
            }

            string targetName = source.TargetFileNames == null
                ? Path.GetFileName(entry.Name)
                : source.TargetFileNames[i];
            string target = Path.Combine(directory, targetName);
            if (File.Exists(target) && new FileInfo(target).Length == entry.UncompressedSize) continue;

            logger.Notification("[{0}] Fetching {1} ({2}) from {3}",
                DiffusionPaths.ModId, Path.GetFileName(entry.Name),
                ModelAssetManager.HumanBytes(entry.UncompressedSize), source.Name);
            RemoteZipExtractor.ExtractEntry(client, source.Url, entry, target, cancellation);
        }
    }

    /// <summary>Streams a whole tarball, verifying it if pinned, and keeps the named files.</summary>
    /// <exception cref="FileNotFoundException">A wanted file was not in the archive.</exception>
    public static void ExtractFromTarGz(HttpClient client, NativeSource source, string directory,
                                        ILogger logger, CancellationToken cancellation)
    {
        logger.Notification("[{0}] Downloading {1}; this archive cannot be partially fetched, so the whole file is streamed.",
            DiffusionPaths.ModId, source.Name);

        List<string> written = RemoteTarGzExtractor.Extract(
            client, source.Url, source.Sha256, source.FileNames, source.TargetFileNames, directory, cancellation);
        IReadOnlyList<string> expected = source.TargetFileNames ?? source.FileNames;
        foreach (string name in expected)
        {
            if (!written.Contains(name) && !File.Exists(Path.Combine(directory, name)))
            {
                throw new FileNotFoundException($"{source.Name} does not contain {name}");
            }
        }
    }

    /// <summary>
    /// Downloads a pinned ZIP whole, checks its SHA-256, and only then extracts the wanted entries,
    /// each through a temporary file so a partial write never takes the target's name.
    /// </summary>
    public static void ExtractVerifiedZip(HttpClient client, NativeSource source, string directory,
                                          ILogger logger, CancellationToken cancellation)
    {
        logger.Notification("[{0}] Downloading and verifying {1}; the complete archive is required for SHA-256 validation.",
            DiffusionPaths.ModId, source.Name);

        string archivePath = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".download");
        try
        {
            DownloadVerifiedFile(client, source.Url, source.Sha256, archivePath, cancellation);

            using var archive = new ZipArchive(File.OpenRead(archivePath), ZipArchiveMode.Read);
            var selected = new ZipArchiveEntry[source.EntryPaths.Length];
            for (int i = 0; i < source.EntryPaths.Length; i++)
            {
                selected[i] = archive.GetEntry(source.EntryPaths[i]);
                if (selected[i] == null)
                    throw new FileNotFoundException($"{source.Name} does not contain {source.EntryPaths[i]}");
            }

            for (int i = 0; i < selected.Length; i++)
            {
                ZipArchiveEntry entry = selected[i];
                string targetName = source.TargetFileNames == null
                    ? Path.GetFileName(entry.FullName)
                    : source.TargetFileNames[i];
                string target = Path.Combine(directory, targetName);
                string temporary = target + ".tmp";
                try
                {
                    using (Stream input = entry.Open())
                    using (var output = new FileStream(
                               temporary, FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes))
                    {
                        input.CopyTo(output);
                        output.Flush(flushToDisk: true);
                    }
                    if (new FileInfo(temporary).Length != entry.Length)
                        throw new InvalidDataException($"{source.Name} produced an incomplete {entry.FullName}");
                    File.Move(temporary, target, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
        }
    }

    /// <summary>Downloads a file to a new path, hashing as it goes.</summary>
    /// <exception cref="InvalidDataException">The SHA-256 did not match; the file is left for the caller to delete.</exception>
    public static void DownloadVerifiedFile(HttpClient client, string url, string expectedSha256,
                                            string destination, CancellationToken cancellation)
    {
        using HttpResponseMessage response = client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation)
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using Stream input = response.Content.ReadAsStream(cancellation);
        using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes,
            FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes];
        while (true)
        {
            int read = input.ReadAsync(buffer.AsMemory(), cancellation).AsTask().GetAwaiter().GetResult();
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);

        string actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Downloaded archive SHA-256 is {actualSha256}, expected {expectedSha256}");
        }
    }

    /// <summary>
    /// Moves <paramref name="staging"/> to <paramref name="destination"/>, replacing what is there.
    /// The old directory is moved aside first and put back if the move fails.
    /// </summary>
    public static void ReplaceDirectory(string staging, string destination)
    {
        string backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
        bool backedUp = false;
        if (Directory.Exists(destination))
        {
            Directory.Move(destination, backup);
            backedUp = true;
        }

        try
        {
            Directory.Move(staging, destination);
        }
        catch
        {
            if (backedUp && !Directory.Exists(destination) && Directory.Exists(backup))
                Directory.Move(backup, destination);
            throw;
        }

        if (backedUp) TryDeleteDirectory(backup);
    }

    /// <summary>Deletes a directory tree if it exists, ignoring files that are in use or protected.</summary>
    public static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
