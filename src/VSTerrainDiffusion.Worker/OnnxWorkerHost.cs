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
    /// <summary>The loaded models' threads, by the id the game gave each model.</summary>
    private static readonly ConcurrentDictionary<int, ModelThread> Models = new();

    /// <summary>Writes replies to the reply pipe, under <see cref="WriteGate"/>.</summary>
    private static BinaryWriter _writer = null!;

    /// <summary>Keeps each frame in one piece when several model threads reply at once.</summary>
    private static readonly object WriteGate = new();

    /// <summary>
    /// Connects to the game's pipes, answers the handshake and serves requests until the game asks
    /// for a shutdown or closes the request pipe.
    /// </summary>
    /// <param name="requestPipe">Name of the pipe the game writes requests to.</param>
    /// <param name="replyPipe">Name of the pipe replies are written to.</param>
    /// <param name="nonce">The secret the game started the worker with, echoed in the handshake.</param>
    /// <returns>The exit code, 0.</returns>
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

    /// <summary>Runs a model on its own thread and replies with the output, or the failure.</summary>
    /// <param name="id">The request being answered.</param>
    /// <param name="thread">The model's thread, on which this runs.</param>
    /// <param name="inputs">Tensors in the graph's input order.</param>
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

        Send(InferenceWorkerProtocol.Ok, id, writer =>
        {
            writer.Write(InferenceWorkerProtocol.PayloadTensor);
            writer.Write(elapsed);
            InferenceWorkerProtocol.WriteFloats(writer, output);
        });
    }

    /// <summary>Does a piece of work and replies with an empty <c>Ok</c>, or the failure.</summary>
    /// <param name="id">The request being answered.</param>
    /// <param name="work">The work.</param>
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

        Send(InferenceWorkerProtocol.Ok, id, writer => writer.Write(InferenceWorkerProtocol.PayloadNone));
    }

    /// <summary>Loads a model and replies with what it loaded on, or the failure.</summary>
    /// <param name="id">The request being answered.</param>
    /// <param name="work">The load.</param>
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

        Send(InferenceWorkerProtocol.Ok, id, writer =>
        {
            writer.Write(InferenceWorkerProtocol.PayloadJson);
            InferenceWorkerProtocol.WriteJson(writer, info);
        });
    }

    /// <summary>Replies that a request failed.</summary>
    /// <param name="id">The request being answered.</param>
    /// <param name="message">Why, usually the exception's full text.</param>
    private static void Fail(int id, string message) =>
        Send(InferenceWorkerProtocol.Failed, id, writer => writer.Write(message));

    /// <summary>Sends a line for the game's log; the sink of <see cref="WorkerLog"/>.</summary>
    /// <param name="level">One of the <c>InferenceWorkerProtocol.Log*</c> levels.</param>
    /// <param name="message">The line.</param>
    private static void SendLog(int level, string message) =>
        Send(InferenceWorkerProtocol.Log, 0, writer =>
        {
            writer.Write(level);
            writer.Write(message);
        });

    /// <summary>
    /// Writes one whole frame - kind, request id, then the body - and flushes it. Frames from
    /// different model threads never interleave.
    /// </summary>
    /// <param name="kind">The frame kind, from <see cref="InferenceWorkerProtocol"/>.</param>
    /// <param name="id">The request being answered, or 0 for a log line.</param>
    /// <param name="body">Writes what follows the id.</param>
    private static void Send(int kind, int id, Action<BinaryWriter> body)
    {
        lock (WriteGate)
        {
            _writer.Write(kind);
            _writer.Write(id);
            body(_writer);
            _writer.Flush();
        }
    }

    /// <summary>One model and the thread that runs everything asked of it, in order.</summary>
    private sealed class ModelThread
    {
        /// <summary>Work for the model, run in the order it was posted.</summary>
        private readonly BlockingCollection<Action> _work = new();

        /// <summary>The thread that runs <see cref="_work"/>.</summary>
        private readonly Thread _thread;

        /// <summary>The model's session, once loaded on this thread.</summary>
        internal OnnxSession Session { get; private set; } = null!;

        /// <summary>Starts the model's thread.</summary>
        /// <param name="name">The model's name, for the thread name.</param>
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

        /// <summary>Loads the model; call on the model's thread.</summary>
        /// <param name="spec">What to load.</param>
        /// <returns>What it loaded on.</returns>
        internal ModelInfo Load(ModelSpec spec)
        {
            Session = new OnnxSession(spec);
            return Session.Info;
        }

        /// <summary>Queues work for the model's thread.</summary>
        /// <param name="action">The work.</param>
        internal void Post(Action action) => _work.Add(action);

        /// <summary>Finishes what is queued, disposes the session and waits for the thread.</summary>
        internal void Stop()
        {
            _work.CompleteAdding();
            _thread.Join();
        }
    }
}
