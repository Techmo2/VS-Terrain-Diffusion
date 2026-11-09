using System;
using System.Collections.Generic;
using System.Threading;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Decides which terrain tile gets the device next, in place of a plain lock.
///
/// The pipeline is not thread safe, so exactly one tile is generated at a time. With a plain
/// semaphore the order was whatever order the worldgen threads happened to arrive in, which after a
/// translocator hop means the player waits for every tile queued for where they used to be before
/// anything near where they now are is even started.
///
/// Work is ordered by how far it is from the nearest player, and work already running is abandoned
/// when something much closer turns up. Abandoning is safe - a window is only cached once it is
/// complete - but it is never free, so it takes a wide margin to trigger and a tile that has been
/// pushed aside a few times is left alone to finish.
/// </summary>
public sealed class InferenceScheduler : IDisposable
{
    /// <summary>
    /// How much closer, in blocks, a waiting tile has to be before the running one is dropped.
    /// Wide enough that ordinary jitter in a player's position never triggers it.
    /// </summary>
    private const long PreemptionMarginBlocks = 2048;

    /// <summary>
    /// After this many interruptions a tile runs to the end whatever else arrives, so a player
    /// moving steadily through the world cannot starve one for ever.
    /// </summary>
    private const int MaxPreemptions = 3;

    /// <summary>
    /// Least time between one tile being dropped and the next. Every preemption throws away
    /// whatever that tile had computed, so without a floor a player moving steadily can have each
    /// arrival interrupt the last and the device spends its time redoing work rather than
    /// finishing any of it.
    /// </summary>
    private const long PreemptionCooldownMs = 2000;

    /// <summary>One caller waiting for, or holding, the device.</summary>
    private sealed class Waiter
    {
        /// <summary>Distance in blocks to the nearest player; smaller runs sooner.</summary>
        public long Priority;

        /// <summary>Times this work has been dropped part way through.</summary>
        public int Preemptions;

        /// <summary>Set when it is this waiter's turn.</summary>
        public readonly ManualResetEventSlim Ready = new(false);

        /// <summary>The current attempt's token; replaced after each preemption.</summary>
        public PreemptionToken Token = new();
    }

    /// <summary>Guards the queue, <see cref="_running"/> and <see cref="_lastPreemptionMs"/>.</summary>
    private readonly object _lock = new();

    /// <summary>
    /// <see cref="Environment.TickCount64"/> at the last preemption. Starts at zero, not
    /// <see cref="long.MinValue"/>: the tick count minus that overflows and the cooldown never expires.
    /// </summary>
    private long _lastPreemptionMs;

    /// <summary>Waiters not yet running, unordered; <see cref="Finish"/> picks the best.</summary>
    private readonly List<Waiter> _queue = new();

    /// <summary>The waiter holding the device, or null.</summary>
    private Waiter _running;

    /// <summary>Backing field of <see cref="PreemptionCount"/>.</summary>
    private long _preemptionCount;

    /// <summary>This thread's <see cref="Enter"/> waiter, released by <see cref="Exit"/>.</summary>
    [ThreadStatic] private static Waiter _entered;

    /// <summary>
    /// How deep this thread is inside the scheduler. Guards against a nested acquire, which would
    /// wait for a turn this same thread is already holding.
    /// </summary>
    [ThreadStatic] private static int _held;

    /// <summary>How many times a tile has been dropped part way through, for diagnostics.</summary>
    public long PreemptionCount => Interlocked.Read(ref _preemptionCount);

    /// <summary>
    /// Runs one tile's generation, waiting its turn and starting again if it is pushed aside.
    /// <paramref name="priority"/> is a distance in blocks; smaller runs sooner.
    /// </summary>
    public T Run<T>(long priority, Func<T> work)
    {
        // Already holding the device on this thread. Nothing in the provider nests today, but a
        // second acquire here would deadlock the chunk thread, so it runs inline instead.
        if (_held > 0) return work();

        var waiter = new Waiter { Priority = priority };

        while (true)
        {
            Enqueue(waiter);
            waiter.Ready.Wait();

            try
            {
                _held++;
                using (InferencePreemption.Begin(waiter.Token))
                {
                    return work();
                }
            }
            catch (InferencePreemptedException)
            {
                Interlocked.Increment(ref _preemptionCount);

                // Come back with a fresh token, and higher standing than last time so this cannot
                // go round for ever.
                waiter.Preemptions++;
                waiter.Token = new PreemptionToken();
                waiter.Ready.Reset();
            }
            finally
            {
                _held--;
                Finish(waiter);
            }
        }
    }

    /// <summary>
    /// Takes the device for work that cannot simply be run again - a typed command, or the survey
    /// done while a world is being made. It goes ahead of queued tiles and is never interrupted.
    /// </summary>
    public void Enter()
    {
        if (_held++ > 0) return;          // already ours on this thread

        var waiter = new Waiter { Priority = 0, Preemptions = MaxPreemptions };
        _entered = waiter;
        Enqueue(waiter);
        waiter.Ready.Wait();
    }

    /// <summary>Gives back the device taken by <see cref="Enter"/>, once the outermost call exits.</summary>
    public void Exit()
    {
        if (--_held > 0) return;

        Waiter waiter = _entered;
        _entered = null;
        if (waiter != null) Finish(waiter);
    }

    /// <summary>
    /// Starts the waiter at once if the device is free; otherwise queues it, preempting the running work if
    /// this is far enough closer and the cooldown allows.
    /// </summary>
    private void Enqueue(Waiter waiter)
    {
        lock (_lock)
        {
            if (_running == null)
            {
                _running = waiter;
                waiter.Ready.Set();
                return;
            }

            _queue.Add(waiter);

            // Is the tile holding the device now far enough behind this one to be worth dropping?
            long now = Environment.TickCount64;
            if (_running.Preemptions < MaxPreemptions &&
                _running.Priority - waiter.Priority > PreemptionMarginBlocks &&
                now - _lastPreemptionMs >= PreemptionCooldownMs)
            {
                _lastPreemptionMs = now;
                _running.Token.Preempt();
            }
        }
    }

    /// <summary>
    /// Releases the device if <paramref name="waiter"/> holds it, and hands it to the best-ranked queued
    /// waiter.
    /// </summary>
    private void Finish(Waiter waiter)
    {
        lock (_lock)
        {
            if (_running != waiter) return;
            _running = null;

            Waiter next = null;
            int index = -1;
            for (int i = 0; i < _queue.Count; i++)
            {
                // A tile that has already been interrupted goes first among equals, so the queue
                // drains rather than churning.
                if (next != null && Ranking(_queue[i]) >= Ranking(next)) continue;
                next = _queue[i];
                index = i;
            }

            if (next == null) return;
            _queue.RemoveAt(index);
            _running = next;
            next.Ready.Set();
        }
    }

    /// <summary>
    /// Queue order: distance, less a margin for each time the work was already dropped. Lower runs first.
    /// </summary>
    private static long Ranking(Waiter waiter) =>
        waiter.Priority - (long)waiter.Preemptions * PreemptionMarginBlocks;

    /// <summary>Releases every queued waiter, so no thread waits on a world that is going away.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (Waiter waiter in _queue) waiter.Ready.Set();
            _queue.Clear();
        }
    }
}
