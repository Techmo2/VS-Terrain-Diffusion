using System;
using System.IO;
using System.Runtime.InteropServices;

#nullable enable

namespace VSTerrainDiffusion.Native;

internal static class OpenVinoWorkerProtocol
{
    internal const int ReadyMagic = 0x4f565744; // OVWD
    internal const int RunCommand = 1;
    internal const int ShutdownCommand = 2;
    internal const int Success = 0;
    internal const int Failure = 1;
    internal const int MaxInputs = 16;
    internal const int MaxRank = 8;
    internal const int MaxElements = 256 * 1024 * 1024;

    internal static void WriteFloats(Stream stream, float[] values)
    {
        stream.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    internal static float[] ReadFloats(Stream stream, int count)
    {
        if (count < 0 || count > MaxElements)
            throw new InvalidDataException($"Invalid OpenVINO tensor element count: {count}");
        var values = new float[count];
        stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }
}
