using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

#nullable enable

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Wire format between the game and the inference worker, compiled into both.
///
/// Every frame starts with its kind and a request id. The game numbers its requests and the worker
/// answers each one exactly once, with <see cref="Ok"/> or <see cref="Failed"/>, in whatever order
/// its models finish; <see cref="Log"/> frames carry id 0 and can arrive at any time. Frames travel
/// over two one-way named pipes rather than the worker's stdout, which native libraries are free to
/// print on.
/// </summary>
internal static class InferenceWorkerProtocol
{
    /// <summary>First thing the worker sends, followed by the nonce it was started with.</summary>
    internal const int HandshakeMagic = 0x54444957; // TDIW

    // Game to worker.
    /// <summary>
    /// Request: load the runtime described by a <see cref="RuntimePlan"/> (JSON). Answered with no payload.
    /// </summary>
    internal const int Initialise = 1;

    /// <summary>
    /// Request: create a session from a <see cref="ModelSpec"/> (JSON). Answered with a
    /// <see cref="ModelInfo"/> (JSON).
    /// </summary>
    internal const int LoadModel = 2;

    /// <summary>Request: run a loaded model by name on tensors. Answered with the output tensors.</summary>
    internal const int Run = 3;

    /// <summary>Request: release a loaded model's session by name. Answered with no payload.</summary>
    internal const int UnloadModel = 4;

    /// <summary>Request: release every model and exit. Not answered; the game waits for the process to end.</summary>
    internal const int Shutdown = 5;

    // Worker to game.
    /// <summary>Reply: the request succeeded; a payload kind and the payload follow.</summary>
    internal const int Ok = 100;

    /// <summary>Reply: the request failed; the error message follows.</summary>
    internal const int Failed = 101;

    /// <summary>A line for the game's log, unprompted: a level and the text follow.</summary>
    internal const int Log = 102;

    // What follows an Ok.
    /// <summary>Nothing follows the <see cref="Ok"/>.</summary>
    internal const int PayloadNone = 0;

    /// <summary>A JSON string follows.</summary>
    internal const int PayloadJson = 1;

    /// <summary>Tensors follow, as <see cref="WriteTensors"/> writes them.</summary>
    internal const int PayloadTensor = 2;

    /// <summary>Level of a <see cref="Log"/> frame: an ordinary notification.</summary>
    internal const int LogNotification = 0;

    /// <summary>Level of a <see cref="Log"/> frame: a warning.</summary>
    internal const int LogWarning = 1;

    /// <summary>Level of a <see cref="Log"/> frame: an error.</summary>
    internal const int LogError = 2;

    /// <summary>Most tensors one frame may carry; anything more is treated as corruption.</summary>
    internal const int MaxInputs = 16;

    /// <summary>Highest tensor rank a frame may carry.</summary>
    internal const int MaxRank = 8;

    /// <summary>Most elements one tensor may hold (1 GiB of floats).</summary>
    internal const int MaxElements = 256 * 1024 * 1024;

    /// <summary>Writes a value as a length-prefixed JSON string.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="writer">The frame being written.</param>
    /// <param name="value">The value.</param>
    internal static void WriteJson<T>(BinaryWriter writer, T value) =>
        writer.Write(JsonSerializer.Serialize(value));

    /// <summary>Reads a value written by <see cref="WriteJson{T}"/>.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="reader">The frame being read.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidDataException">The JSON was null.</exception>
    internal static T ReadJson<T>(BinaryReader reader) =>
        JsonSerializer.Deserialize<T>(reader.ReadString())
        ?? throw new InvalidDataException($"Empty {typeof(T).Name} from the inference worker");

    /// <summary>Writes tensors: a count, then for each its rank, dimensions and data.</summary>
    /// <param name="writer">The frame being written.</param>
    /// <param name="tensors">The tensors, data in row-major order.</param>
    internal static void WriteTensors(BinaryWriter writer, IReadOnlyList<(float[] Data, long[] Shape)> tensors)
    {
        writer.Write(tensors.Count);
        foreach ((float[] data, long[] shape) in tensors)
        {
            writer.Write(shape.Length);
            foreach (long dimension in shape) writer.Write(dimension);
            WriteFloats(writer, data);
        }
    }

    /// <summary>
    /// Reads tensors written by <see cref="WriteTensors"/>, refusing counts, ranks or sizes beyond the
    /// limits.
    /// </summary>
    /// <param name="reader">The frame being read.</param>
    /// <returns>The tensors.</returns>
    /// <exception cref="InvalidDataException">The frame is malformed.</exception>
    internal static List<(float[] Data, long[] Shape)> ReadTensors(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 1 || count > MaxInputs)
            throw new InvalidDataException($"Invalid tensor count: {count}");

        var tensors = new List<(float[], long[])>(count);
        for (int i = 0; i < count; i++)
        {
            int rank = reader.ReadInt32();
            if (rank < 1 || rank > MaxRank)
                throw new InvalidDataException($"Invalid tensor rank: {rank}");

            var shape = new long[rank];
            long elements = 1;
            for (int dimension = 0; dimension < rank; dimension++)
            {
                shape[dimension] = reader.ReadInt64();
                if (shape[dimension] < 1 || elements > MaxElements / shape[dimension])
                    throw new InvalidDataException("Invalid tensor shape");
                elements *= shape[dimension];
            }

            float[] data = ReadFloats(reader);
            if (data.Length != elements)
                throw new InvalidDataException("Tensor shape does not match its data length");
            tensors.Add((data, shape));
        }
        return tensors;
    }

    /// <summary>
    /// Writes an element count and then the floats as raw bytes, in the machine's byte order (both ends are
    /// the same machine).
    /// </summary>
    /// <param name="writer">The frame being written.</param>
    /// <param name="values">The floats.</param>
    internal static void WriteFloats(BinaryWriter writer, ReadOnlySpan<float> values)
    {
        writer.Write(values.Length);
        writer.Flush();
        writer.BaseStream.Write(MemoryMarshal.AsBytes(values));
    }

    /// <summary>Reads floats written by <see cref="WriteFloats"/>.</summary>
    /// <param name="reader">The frame being read.</param>
    /// <returns>The floats.</returns>
    /// <exception cref="InvalidDataException">The count is out of range.</exception>
    internal static float[] ReadFloats(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxElements)
            throw new InvalidDataException($"Invalid tensor element count: {count}");
        var values = new float[count];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }
}

/// <summary>
/// What the worker loads before creating any session. Worked out, and downloaded, by the game
/// (<c>OnnxRuntimeBootstrap</c>, which the worker does not compile); the worker only loads it.
/// </summary>
internal sealed class RuntimePlan
{
    /// <summary>The provider sessions are created with; OpenVINO means ONNX Runtime CPU here.</summary>
    public InferenceProvider Provider { get; set; }

    /// <summary>The ONNX Runtime build the worker's P/Invoke resolver is pointed at.</summary>
    public string OnnxRuntimeDirectory { get; set; } = "";

    /// <summary>
    /// Libraries loaded by absolute path, in order, before ONNX Runtime: the Visual C++ runtime and
    /// the CUDA libraries on Windows, and TensorRT RTX's dependencies.
    /// </summary>
    public string[] Preload { get; set; } = Array.Empty<string>();

    /// <summary>Plugin execution provider to register, such as TensorRT RTX, or null.</summary>
    public string? PluginName { get; set; }

    /// <summary>Absolute path of the plugin provider's library, or null.</summary>
    public string? PluginLibrary { get; set; }
}

/// <summary>One model for the worker to load.</summary>
internal sealed class ModelSpec
{
    /// <summary>The name the game runs and unloads the model by.</summary>
    public string Name { get; set; } = "";

    /// <summary>The graph as downloaded.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>
    /// Where the optimised graph is cached, or null to use <see cref="SourcePath"/> as it is
    /// (TensorRT RTX cannot import ONNX Runtime's fused operators).
    /// </summary>
    public string? OptimizedPath { get; set; }

    /// <summary>Open the graph from its file rather than reading it into memory first.</summary>
    public bool LoadFromFile { get; set; }

    /// <summary>Keep only one model on the accelerated provider at a time.</summary>
    public bool Offload { get; set; }

    /// <summary>Create a CPU session whatever the plan's provider.</summary>
    public bool CpuOnly { get; set; }

    /// <summary>Threads for a session on the CPU; 0 is ONNX Runtime's default (one per physical core).</summary>
    public int CpuThreads { get; set; }

    /// <summary>Where TensorRT RTX keeps the engines it builds, or null for no cache.</summary>
    public string? TensorRtRtxCacheDirectory { get; set; }

    /// <summary>Where MIGraphX keeps the programs it compiles.</summary>
    public string? RocmCacheDirectory { get; set; }

    /// <summary>TensorRT RTX shape profile, min and max, in its <c>name:dimxdim</c> notation.</summary>
    public string? TensorRtRtxProfileMin { get; set; }

    /// <summary>See <see cref="TensorRtRtxProfileMin"/>.</summary>
    public string? TensorRtRtxProfileMax { get; set; }
}

/// <summary>What the worker reports back once a model is loaded.</summary>
internal sealed class ModelInfo
{
    /// <summary>For the log and <c>/tdiff status</c>, e.g. "ONNX Runtime Cuda".</summary>
    public string Backend { get; set; } = "";

    /// <summary>The provider in use, which is the CPU if the planned one could not create a session.</summary>
    public InferenceProvider ActiveProvider { get; set; }
}
