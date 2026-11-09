namespace VSTerrainDiffusion.Native;

/// <summary>
/// Which execution provider the ONNX sessions should be created with.
/// </summary>
public enum InferenceProvider
{
    /// <summary>
    /// ONNX Runtime's default CPU provider. Works everywhere; the fallback for every other provider.
    /// </summary>
    Cpu,
    /// <summary>NVIDIA GPUs through ONNX Runtime's CUDA provider.</summary>
    Cuda,
    /// <summary>Any DirectX 12 GPU on Windows through DirectML.</summary>
    DirectMl,
    /// <summary>Apple's Core ML on macOS (the GPU and Neural Engine).</summary>
    CoreMl,
    /// <summary>
    /// Intel CPUs and GPUs through OpenVINO, run in its own worker rather than ONNX Runtime.
    /// </summary>
    OpenVino,

    /// <summary>
    /// NVIDIA's TensorRT for RTX, an ONNX Runtime plugin provider. Ampere and later; builds its
    /// engines on the machine, in seconds, and caches them.
    /// </summary>
    TensorRtRtx,

    /// <summary>
    /// AMD GPUs on Linux through ROCm: ONNX Runtime's MIGraphX provider, from AMD's own ONNX Runtime
    /// build, running on the system's ROCm install. Compiles each graph for the GPU on first use and
    /// caches the result.
    /// </summary>
    Rocm
}
