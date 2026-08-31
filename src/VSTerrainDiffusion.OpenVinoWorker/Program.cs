using VSTerrainDiffusion.Native;

return WorkerProgram.Run(args);

internal static class WorkerProgram
{
    internal static int Run(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Expected model, cache, native runtime, and thread count arguments");
            return 2;
        }

        try
        {
            OpenVinoRuntime.ConfigureNativeDirectory(args[2]);
            using var runtime = new OpenVinoRuntime(
                args[0], args[1], int.Parse(args[3]), Console.Error.WriteLine);
            using var reader = new BinaryReader(Console.OpenStandardInput());
            using var writer = new BinaryWriter(Console.OpenStandardOutput());
            writer.Write(OpenVinoWorkerProtocol.ReadyMagic);
            writer.Flush();

            while (true)
            {
                int command;
                try { command = reader.ReadInt32(); }
                catch (EndOfStreamException) { return 0; }
                if (command == OpenVinoWorkerProtocol.ShutdownCommand) return 0;
                if (command != OpenVinoWorkerProtocol.RunCommand)
                    throw new InvalidDataException($"Unknown OpenVINO worker command: {command}");

                try
                {
                    IReadOnlyList<(float[] Data, long[] Shape)> inputs = ReadInputs(reader);
                    float[] output = runtime.Run(inputs);
                    writer.Write(OpenVinoWorkerProtocol.Success);
                    writer.Write(output.Length);
                    OpenVinoWorkerProtocol.WriteFloats(writer.BaseStream, output);
                    writer.Flush();
                }
                catch (Exception exception)
                {
                    writer.Write(OpenVinoWorkerProtocol.Failure);
                    writer.Write(exception.ToString());
                    writer.Flush();
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static IReadOnlyList<(float[] Data, long[] Shape)> ReadInputs(BinaryReader reader)
    {
        int inputCount = reader.ReadInt32();
        if (inputCount < 1 || inputCount > OpenVinoWorkerProtocol.MaxInputs)
            throw new InvalidDataException($"Invalid OpenVINO input count: {inputCount}");
        var inputs = new List<(float[], long[])>(inputCount);
        for (int i = 0; i < inputCount; i++)
        {
            int rank = reader.ReadInt32();
            if (rank < 1 || rank > OpenVinoWorkerProtocol.MaxRank)
                throw new InvalidDataException($"Invalid OpenVINO tensor rank: {rank}");
            var shape = new long[rank];
            long shapeElements = 1;
            for (int dimension = 0; dimension < rank; dimension++)
            {
                shape[dimension] = reader.ReadInt64();
                if (shape[dimension] < 1 || shapeElements > OpenVinoWorkerProtocol.MaxElements / shape[dimension])
                    throw new InvalidDataException("Invalid OpenVINO tensor shape");
                shapeElements *= shape[dimension];
            }
            int count = reader.ReadInt32();
            if (shapeElements != count)
                throw new InvalidDataException("OpenVINO tensor shape does not match its data length");
            float[] data = OpenVinoWorkerProtocol.ReadFloats(reader.BaseStream, count);
            inputs.Add((data, shape));
        }
        return inputs;
    }
}
