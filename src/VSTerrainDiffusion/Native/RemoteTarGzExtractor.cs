using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Streams a remote .tar.gz and writes out the entries whose file name is wanted. Unlike ZIP,
/// gzip cannot be range-read, so the whole archive is streamed through once — but nothing is
/// written to disk except the files that were asked for.
/// </summary>
internal static class RemoteTarGzExtractor
{
    /// <summary>
    /// Extracts every entry matching one of <paramref name="wantedFileNames"/> into
    /// <paramref name="destinationDirectory"/>, using <paramref name="targetFileNames"/> when
    /// supplied and the archive name otherwise.
    ///
    /// Matching ignores directories and tolerates version suffixes, because release tarballs ship
    /// the real library as <c>libonnxruntime.so.1.24.4</c> with <c>libonnxruntime.so</c> as a
    /// symlink that tar records separately.
    /// </summary>
    /// <returns>The wanted names that were actually written.</returns>
    public static List<string> Extract(HttpClient client, string url, string expectedSha256,
                                       IReadOnlyList<string> wantedFileNames,
                                       IReadOnlyList<string> targetFileNames, string destinationDirectory,
                                       CancellationToken cancellation)
    {
        if (targetFileNames != null && targetFileNames.Count != wantedFileNames.Count)
            throw new InvalidDataException("Archive and destination file lists have different lengths");

        Directory.CreateDirectory(destinationDirectory);
        var written = new List<string>();
        string stagingDirectory = Path.Combine(
            destinationDirectory, ".extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            using HttpResponseMessage response = client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            using Stream network = response.Content.ReadAsStream(cancellation);
            byte[] actualHash;
            using (SHA256 sha256 = SHA256.Create())
            {
                using (var hashing = new CryptoStream(network, sha256, CryptoStreamMode.Read, leaveOpen: true))
                {
                    using (var gzip = new GZipStream(hashing, CompressionMode.Decompress, leaveOpen: true))
                    using (var tar = new TarReader(gzip, leaveOpen: true))
                    {
                        TarEntry entry;
                        while ((entry = tar.GetNextEntry()) != null)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (entry.EntryType != TarEntryType.RegularFile &&
                                entry.EntryType != TarEntryType.V7RegularFile) continue;

                            string fileName = Path.GetFileName(entry.Name);
                            int index = Match(wantedFileNames, fileName);
                            if (index < 0) continue;
                            string wanted = targetFileNames == null ? wantedFileNames[index] : targetFileNames[index];
                            if (written.Contains(wanted)) continue;

                            string destination = Path.Combine(stagingDirectory, wanted);
                            using (var file = new FileStream(
                                       destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                            {
                                entry.DataStream?.CopyToAsync(file, 1 << 20, cancellation)
                                    .GetAwaiter().GetResult();
                                file.Flush(flushToDisk: true);
                            }
                            written.Add(wanted);
                        }
                    }

                    // GZipStream normally consumes the complete response, including its footer.
                    // Drain any trailing bytes so the digest always covers the exact HTTP payload.
                    var drain = new byte[81920];
                    while (hashing.ReadAsync(drain.AsMemory(), cancellation).AsTask()
                               .GetAwaiter().GetResult() > 0) { }
                }
                actualHash = sha256.Hash
                             ?? throw new InvalidDataException("Could not calculate archive SHA-256");
            }

            if (!string.IsNullOrEmpty(expectedSha256))
            {
                byte[] expectedHash = Convert.FromHexString(expectedSha256);
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                {
                    throw new InvalidDataException(
                        $"Downloaded archive SHA-256 is {Convert.ToHexString(actualHash).ToLowerInvariant()}, " +
                        $"expected {expectedSha256}");
                }
            }

            IReadOnlyList<string> expectedFiles = targetFileNames ?? wantedFileNames;
            foreach (string name in expectedFiles)
            {
                if (!written.Contains(name))
                    throw new FileNotFoundException($"Archive does not contain {name}");
            }

            foreach (string name in expectedFiles)
            {
                File.Move(
                    Path.Combine(stagingDirectory, name),
                    Path.Combine(destinationDirectory, name),
                    overwrite: true);
            }
            return written;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static int Match(IReadOnlyList<string> wantedFileNames, string fileName)
    {
        for (int i = 0; i < wantedFileNames.Count; i++)
        {
            string wanted = wantedFileNames[i];
            if (fileName.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return i;
            if (fileName.StartsWith(wanted + ".", StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}
