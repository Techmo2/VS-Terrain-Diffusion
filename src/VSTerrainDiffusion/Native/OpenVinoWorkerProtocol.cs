using System;
using System.IO;
using System.Runtime.InteropServices;

#nullable enable

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Wire format between the game and the OpenVINO worker over its stdin and stdout, compiled into both.
/// </summary>
internal static class OpenVinoWorkerProtocol
{
    /// <summary>First thing the worker writes once its model is compiled.</summary>
    internal const int ReadyMagic = 0x4f565744; // OVWD

    /// <summary>Request: run the model on the tensors that follow.</summary>
    internal const int RunCommand = 1;

    /// <summary>Request: exit.</summary>
    internal const int ShutdownCommand = 2;

    /// <summary>Reply: the outputs follow.</summary>
    internal const int Success = 0;

    /// <summary>Reply: an error message follows.</summary>
    internal const int Failure = 1;

    /// <summary>Most tensors one request may carry.</summary>
    internal const int MaxInputs = 16;

    /// <summary>Highest tensor rank a request may carry.</summary>
    internal const int MaxRank = 8;

    /// <summary>Most elements one tensor may hold (1 GiB of floats).</summary>
    internal const int MaxElements = 256 * 1024 * 1024;

    /// <summary>
    /// Writes floats as raw bytes in the machine's byte order; the count travels separately.
    /// </summary>
    /// <param name="stream">The pipe.</param>
    /// <param name="values">The floats.</param>
    internal static void WriteFloats(Stream stream, float[] values)
    {
        stream.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    /// <summary>Reads <paramref name="count"/> floats written by <see cref="WriteFloats"/>.</summary>
    /// <param name="stream">The pipe.</param>
    /// <param name="count">How many.</param>
    /// <returns>The floats.</returns>
    /// <exception cref="InvalidDataException">The count is out of range.</exception>
    internal static float[] ReadFloats(Stream stream, int count)
    {
        if (count < 0 || count > MaxElements)
            throw new InvalidDataException($"Invalid OpenVINO tensor element count: {count}");
        var values = new float[count];
        stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }
}
