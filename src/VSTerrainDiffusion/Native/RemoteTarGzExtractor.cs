using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
    /// <paramref name="destinationDirectory"/>, saving it under the wanted name.
    ///
    /// Matching ignores directories and tolerates version suffixes, because release tarballs ship
    /// the real library as <c>libonnxruntime.so.1.24.4</c> with <c>libonnxruntime.so</c> as a
    /// symlink that tar records separately.
    /// </summary>
    /// <returns>The wanted names that were actually written.</returns>
    public static List<string> Extract(HttpClient client, string url, IReadOnlyCollection<string> wantedFileNames,
                                       string destinationDirectory, CancellationToken cancellation)
    {
        Directory.CreateDirectory(destinationDirectory);
        var written = new List<string>();

        using HttpResponseMessage response = client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation)
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using Stream network = response.Content.ReadAsStream(cancellation);
        using var gzip = new GZipStream(network, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        TarEntry entry;
        while ((entry = tar.GetNextEntry()) != null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile) continue;

            string fileName = Path.GetFileName(entry.Name);
            string wanted = Match(wantedFileNames, fileName);
            if (wanted == null || written.Contains(wanted)) continue;

            string destination = Path.Combine(destinationDirectory, wanted);
            string temporary = destination + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                entry.DataStream?.CopyTo(file);
            }
            File.Move(temporary, destination, overwrite: true);
            written.Add(wanted);
        }

        return written;
    }

    private static string Match(IReadOnlyCollection<string> wantedFileNames, string fileName)
    {
        foreach (string wanted in wantedFileNames)
        {
            if (fileName.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return wanted;
            if (fileName.StartsWith(wanted + ".", StringComparison.OrdinalIgnoreCase)) return wanted;
        }
        return null;
    }
}
