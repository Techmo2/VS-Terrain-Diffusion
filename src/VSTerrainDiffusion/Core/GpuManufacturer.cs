namespace VSTerrainDiffusion.Core;

/// <summary>Who made a GPU, as far as choosing an inference runtime is concerned.</summary>
public enum GpuManufacturer
{
    /// <summary>AMD (PCI vendor 0x1002 or 0x1022).</summary>
    Amd,

    /// <summary>NVIDIA (0x10DE).</summary>
    Nvidia,

    /// <summary>Intel (0x8086).</summary>
    Intel,

    /// <summary>Apple silicon's own GPU (0x106B).</summary>
    Apple,

    /// <summary>No GPU found, or one from another vendor.</summary>
    Unknown
}
