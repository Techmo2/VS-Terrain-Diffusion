namespace VSTerrainDiffusion.Native;

/// <summary>
/// Which execution provider the ONNX sessions should be created with.
/// </summary>
public enum InferenceProvider
{
    Cpu,
    Cuda,
    DirectMl,
    CoreMl,
    OpenVino,

    /// <summary>
    /// NVIDIA's TensorRT for RTX, an ONNX Runtime plugin provider. Ampere and later; builds its
    /// engines on the machine, in seconds, and caches them.
    /// </summary>
    TensorRtRtx
}
