using System.Globalization;
using VSTerrainDiffusion.Native;

// One executable for both of the mod's worker processes:
//   onnx <request pipe> <reply pipe> <nonce>                      - ONNX Runtime for one game
//   openvino <model> <cache> <native runtime> <thread count>      - the OpenVINO decoder
return args.Length > 0 && args[0] == "onnx" && args.Length == 4
    ? OnnxWorker.Run(args[1], args[2], args[3])
    : args.Length > 0 && args[0] == "openvino"
        ? OpenVinoWorker.Run(args[1..])
        : Usage();

// Prints the two command lines this executable accepts; exit code 2.
static int Usage()
{
    Console.Error.WriteLine("Expected 'onnx <request pipe> <reply pipe> <nonce>' or " +
                            "'openvino <model> <cache> <native runtime> <thread count>'");
    return 2;
}

/// <summary>The ONNX Runtime worker's entry point: one game's inference, over two named pipes.</summary>
internal static class OnnxWorker
{
    /// <summary>Serves the game until it asks the worker to shut down or its pipes close.</summary>
    /// <param name="requestPipe">Name of the pipe the game writes requests to.</param>
    /// <param name="replyPipe">Name of the pipe the worker writes replies to.</param>
    /// <param name="nonce">The secret the game started the worker with, echoed in the handshake.</param>
    /// <returns>The exit code: 0 after a shutdown, 1 after an unhandled failure (printed to stderr).</returns>
    internal static int Run(string requestPipe, string replyPipe, string nonce)
    {
        try
        {
            return OnnxWorkerHost.Run(requestPipe, replyPipe, nonce);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
