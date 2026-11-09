namespace VSTerrainDiffusion.Native;

/// <summary>Log lines for the game's log, sent back over the reply pipe.</summary>
internal static class WorkerLog
{
    /// <summary>Where lines go: stderr until the reply pipe is up, then the pipe.</summary>
    internal static Action<int, string> Sink { get; set; } = (_, message) => Console.Error.WriteLine(message);

    /// <summary>Logs a notification.</summary>
    /// <param name="message">The line.</param>
    internal static void Notification(string message) => Sink(InferenceWorkerProtocol.LogNotification, message);

    /// <summary>Logs a warning.</summary>
    /// <param name="message">The line.</param>
    internal static void Warning(string message) => Sink(InferenceWorkerProtocol.LogWarning, message);
}
