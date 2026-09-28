using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.ML.OnnxRuntime;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Hosts ONNX Runtime for one game. The game starts this process as a world loads and ends it when
/// the world closes, so the native runtime - and whichever provider, CUDA or TensorRT RTX build
/// came with it - is gone from memory with the process rather than pinned for the life of the game.
///
/// Requests arrive on one pipe and replies leave on another. Each model gets a thread of its own,
/// so two models run at the same time the way they did in the game's process, while one model's
/// runs stay in order.
/// </summary>
internal static class OnnxWorkerHost
{
    private static readonly ConcurrentDictionary<int, ModelThread> Models = new();
    private static BinaryWriter _writer = null!;
    private static readonly object WriteGate = new();

    internal static int Run(string requestPipe, string replyPipe, string nonce)
    {
        using var requests = new NamedPipeClientStream(".", requestPipe, PipeDirection.In);
        using var replies = new NamedPipeClientStream(".", replyPipe, PipeDirection.Out);
        requests.Connect(30_000);
        replies.Connect(30_000);

        using var reader = new BinaryReader(new BufferedStream(requests, 64 * 1024), Encoding.UTF8);
        _writer = new BinaryWriter(new BufferedStream(replies, 64 * 1024), Encoding.UTF8);
        WorkerLog.Sink = SendLog;

        lock (WriteGate)
        {
            _writer.Write(InferenceWorkerProtocol.HandshakeMagic);
            _writer.Write(nonce);
            _writer.Flush();
        }

        while (true)
        {
            int kind, id;
            try
            {
                kind = reader.ReadInt32();
                id = reader.ReadInt32();
            }
            catch (EndOfStreamException)
            {
                // The game closed the pipe, or is gone. Either way nothing will ask for more.
                return 0;
            }

            switch (kind)
            {
                case InferenceWorkerProtocol.Initialise:
                    Reply(id, () => Initialise(InferenceWorkerProtocol.ReadJson<RuntimePlan>(reader)));
                    break;

                case InferenceWorkerProtocol.LoadModel:
                {
                    int model = reader.ReadInt32();
                    ModelSpec spec = InferenceWorkerProtocol.ReadJson<ModelSpec>(reader);
                    var thread = new ModelThread(spec.Name);
                    Models[model] = thread;
                    thread.Post(() => Reply(id, () => thread.Load(spec)));
                    break;
                }

                case InferenceWorkerProtocol.Run:
                {
                    int model = reader.ReadInt32();
                    List<(float[] Data, long[] Shape)> inputs = InferenceWorkerProtocol.ReadTensors(reader);
                    if (!Models.TryGetValue(model, out ModelThread? thread))
                    {
                        Fail(id, $"No model {model} is loaded");
                        break;
                    }
                    thread.Post(() => RunModel(id, thread, inputs));
                    break;
                }

                case InferenceWorkerProtocol.UnloadModel:
                {
                    int model = reader.ReadInt32();
                    if (Models.TryRemove(model, out ModelThread? thread)) thread.Stop();
                    Reply(id, () => { });
                    break;
                }

                case InferenceWorkerProtocol.Shutdown:
                    foreach (ModelThread thread in Models.Values) thread.Stop();
                    Models.Clear();
                    return 0;

                default:
                    throw new InvalidDataException($"Unknown request {kind}");
            }
        }
    }

    /// <summary>Loads everything the plan names, then points ONNX Runtime at its build.</summary>
    private static void Initialise(RuntimePlan plan)
    {
        // By absolute path, before anything imports them, so every library loaded afterwards binds
        // to these copies by name: the Visual C++ runtime, the CUDA libraries cuDNN opens by bare
        // name, and TensorRT RTX's dependencies.
        foreach (string library in plan.Preload) NativeLibrary.Load(library);

        NativeLibraryResolver.UseOnnxRuntime(plan.OnnxRuntimeDirectory);
        OnnxSession.Provider = plan.Provider;

        if (plan.PluginName != null)
        {
            OrtEnv.Instance().RegisterExecutionProviderLibrary(plan.PluginName, plan.PluginLibrary!);
            OnnxSession.TensorRtRtxEpName = plan.PluginName;
        }
    }

    private static void RunModel(int id, ModelThread thread, IReadOnlyList<(float[] Data, long[] Shape)> inputs)
    {
        float[] output;
        long elapsed;
        try
        {
            output = thread.Session.Run(inputs, out elapsed);
        }
        catch (Exception e)
        {
            Fail(id, e.ToString());
            return;
        }

        lock (WriteGate)
        {
            _writer.Write(InferenceWorkerProtocol.Ok);
            _writer.Write(id);
            _writer.Write(InferenceWorkerProtocol.PayloadTensor);
            _writer.Write(elapsed);
            InferenceWorkerProtocol.WriteFloats(_writer, output);
            _writer.Flush();
        }
    }

    private static void Reply(int id, Action work)
    {
        try
        {
            work();
        }
        catch (Exception e)
        {
            Fail(id, e.ToString());
            return;
        }

        lock (WriteGate)
        {
            _writer.Write(InferenceWorkerProtocol.Ok);
            _writer.Write(id);
            _writer.Write(InferenceWorkerProtocol.PayloadNone);
            _writer.Flush();
        }
    }

    private static void Reply(int id, Func<ModelInfo> work)
    {
        ModelInfo info;
        try
        {
            info = work();
        }
        catch (Exception e)
        {
            Fail(id, e.ToString());
            return;
        }

        lock (WriteGate)
        {
            _writer.Write(InferenceWorkerProtocol.Ok);
            _writer.Write(id);
            _writer.Write(InferenceWorkerProtocol.PayloadJson);
            InferenceWorkerProtocol.WriteJson(_writer, info);
            _writer.Flush();
        }
    }

    private static void Fail(int id, string message)
    {
        lock (WriteGate)
        {
            _writer.Write(InferenceWorkerProtocol.Failed);
            _writer.Write(id);
            _writer.Write(message);
            _writer.Flush();
        }
    }

    private static void SendLog(int level, string message)
    {
        lock (WriteGate)
        {
            _writer.Write(InferenceWorkerProtocol.Log);
            _writer.Write(0);
            _writer.Write(level);
            _writer.Write(message);
            _writer.Flush();
        }
    }

    /// <summary>One model and the thread that runs everything asked of it, in order.</summary>
    private sealed class ModelThread
    {
        private readonly BlockingCollection<Action> _work = new();
        private readonly Thread _thread;

        internal OnnxSession Session { get; private set; } = null!;

        internal ModelThread(string name)
        {
            _thread = new Thread(() =>
            {
                foreach (Action action in _work.GetConsumingEnumerable()) action();
                Session?.Dispose();
            })
            {
                Name = "model-" + name,
                IsBackground = true
            };
            _thread.Start();
        }

        internal ModelInfo Load(ModelSpec spec)
        {
            Session = new OnnxSession(spec);
            return Session.Info;
        }

        internal void Post(Action action) => _work.Add(action);

        /// <summary>Finishes what is queued, disposes the session and waits for the thread.</summary>
        internal void Stop()
        {
            _work.CompleteAdding();
            _thread.Join();
        }
    }
}

/// <summary>Log lines for the game's log, sent back over the reply pipe.</summary>
internal static class WorkerLog
{
    internal static Action<int, string> Sink { get; set; } = (_, message) => Console.Error.WriteLine(message);

    internal static void Notification(string message) => Sink(InferenceWorkerProtocol.LogNotification, message);

    internal static void Warning(string message) => Sink(InferenceWorkerProtocol.LogWarning, message);
}
