using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// A set of native files pinned by name, size and SHA-256, installed together into one directory
/// with a marker file naming the sources they came from.
/// </summary>
/// <remarks>
/// An install is trusted only when the marker matches and every file matches the manifest, so a
/// stale, interrupted or tampered install is replaced rather than loaded. Files present without a
/// marker are an administrator's own install, which callers may accept when downloads are off.
/// </remarks>
internal sealed class PinnedFileSet
{
    /// <summary>Creates a pinned set.</summary>
    /// <param name="name">What the files are, for messages, e.g. "TensorRT RTX runtime".</param>
    /// <param name="files">Every file of the set with its size in bytes and lower-case SHA-256, in load order.</param>
    /// <param name="markerFileName">Marker file written beside the files once they are verified.</param>
    /// <param name="markerContents">Exact marker contents: the sources' identities, so a new pin refetches.</param>
    public PinnedFileSet(string name, (string Name, long Size, string Sha256)[] files,
                         string markerFileName, string markerContents)
    {
        Name = name;
        Files = files;
        MarkerFileName = markerFileName;
        MarkerContents = markerContents;
    }

    /// <summary>What the files are, for messages.</summary>
    public string Name { get; }

    /// <summary>Every file of the set, in load order.</summary>
    public IReadOnlyList<(string Name, long Size, string Sha256)> Files { get; }

    /// <summary>Marker file written beside the files once they are verified.</summary>
    public string MarkerFileName { get; }

    /// <summary>Exact contents the marker must have.</summary>
    public string MarkerContents { get; }

    /// <summary>Whether every file of the set exists in <paramref name="directory"/>, verified or not.</summary>
    public bool IsPresent(string directory)
    {
        foreach ((string name, _, _) in Files)
        {
            if (!File.Exists(Path.Combine(directory, name))) return false;
        }
        return true;
    }

    /// <summary>Whether <paramref name="directory"/> has a marker, i.e. was installed by this mod.</summary>
    public bool HasMarker(string directory) => File.Exists(Path.Combine(directory, MarkerFileName));

    /// <summary>
    /// Whether <paramref name="directory"/> holds exactly this set: the marker matches and every file
    /// has its pinned size and hash. An unreadable file counts as a mismatch.
    /// </summary>
    public bool IsVerified(string directory)
    {
        string marker = Path.Combine(directory, MarkerFileName);
        try
        {
            if (!File.Exists(marker) ||
                !string.Equals(File.ReadAllText(marker), MarkerContents, StringComparison.Ordinal))
                return false;

            foreach ((string name, long size, string expectedSha256) in Files)
            {
                string path = Path.Combine(directory, name);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != size) return false;
                using FileStream input = File.OpenRead(path);
                string actualSha256 = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal)) return false;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Absolute paths of every file of the set in <paramref name="directory"/>, in load order.</summary>
    public string[] PathsIn(string directory)
    {
        var paths = new string[Files.Count];
        for (int i = 0; i < paths.Length; i++) paths[i] = Path.Combine(directory, Files[i].Name);
        return paths;
    }

    /// <summary>
    /// Installs the set into <paramref name="directory"/>: extracts into a fresh staging directory,
    /// checks it is complete, writes the marker, verifies it, then swaps it into place. The old
    /// install, if any, is only removed once the new one is in place.
    /// </summary>
    /// <param name="directory">Final directory of the set.</param>
    /// <param name="extract">Puts the files into the staging directory it is given.</param>
    /// <param name="incompleteMessage">Exception message when the extracted files are not all there.</param>
    /// <exception cref="FileNotFoundException">The sources did not hold every file.</exception>
    /// <exception cref="InvalidDataException">A file did not match the manifest.</exception>
    public void Install(string directory, Action<string> extract, string incompleteMessage)
    {
        string parent = Path.GetDirectoryName(directory)
                        ?? throw new InvalidOperationException(Name + " directory has no parent");
        Directory.CreateDirectory(parent);

        string staging = directory + ".install-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            extract(staging);

            if (!IsPresent(staging)) throw new FileNotFoundException(incompleteMessage);

            File.WriteAllText(Path.Combine(staging, MarkerFileName), MarkerContents);
            if (!IsVerified(staging))
                throw new InvalidDataException($"The extracted {Name} did not match the pinned file manifest");
            NativeDownloads.ReplaceDirectory(staging, directory);
        }
        finally
        {
            NativeDownloads.TryDeleteDirectory(staging);
        }
    }
}
