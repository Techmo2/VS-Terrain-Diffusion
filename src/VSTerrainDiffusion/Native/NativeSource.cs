namespace VSTerrainDiffusion.Native;

/// <summary>How a <see cref="NativeSource"/> archive is read.</summary>
internal enum ArchiveKind
{
    /// <summary>A ZIP (NuGet package, wheel or GitHub .zip asset) read with range requests.</summary>
    Zip,

    /// <summary>A GitHub .tar.gz asset, which has to be streamed in full.</summary>
    TarGz
}

/// <summary>One archive to take native files from, and which files to take.</summary>
internal sealed class NativeSource
{
    /// <summary>Human-readable name for the log, e.g. a package id.</summary>
    public string Name;

    /// <summary>Where the archive is downloaded from.</summary>
    public string Url;

    /// <summary>How the archive is read.</summary>
    public ArchiveKind Kind;

    /// <summary>
    /// Expected SHA-256 of the whole archive, lower-case hex. When set the archive is downloaded in
    /// full and checked before anything is extracted; otherwise a ZIP is range-read entry by entry.
    /// </summary>
    public string Sha256;

    /// <summary>Exact archive paths for <see cref="ArchiveKind.Zip"/>; ignored for tarballs.</summary>
    public string[] EntryPaths;

    /// <summary>
    /// Optional destination names corresponding to <see cref="EntryPaths"/> (or <see cref="FileNames"/>
    /// for a tarball). Used when an archive only contains a versioned library name but the native
    /// binding loads its stable name.
    /// </summary>
    public string[] TargetFileNames;

    /// <summary>File names to pull out of a tarball.</summary>
    public string[] FileNames;
}
