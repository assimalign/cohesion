using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// Holds the process's thread pool busy until disposed, the way parallel test classes do when
/// their busy loops (writers, readers and write-back passes started with <c>Task.Run</c>) fill
/// every worker and leave a queue behind them: a work item queued meanwhile does not start.
/// </summary>
/// <remarks>
/// <para>
/// Far more blockers are queued to the pool's global queue than the pool grows workers during a
/// test (it adds about one a second while starved), each parked on one gate, and a sentinel is
/// queued behind them. The global queue is first in, first out, and a worker steals from another
/// worker's local queue (where <c>Task.Run</c> on a pool thread puts its work) only once the global
/// queue is empty, so anything queued after <see cref="Start"/> runs only after the sentinel:
/// <see cref="HasHeld"/> still being true proves nothing queued since has run.
/// </para>
/// <para>
/// A test that uses it must run in <see cref="ThreadPoolSaturationCollection"/>, alone, so no other
/// test's work waits behind the blockers.
/// </para>
/// </remarks>
internal sealed class ThreadPoolSaturation : IDisposable
{
    // Never disposed: a blocker the pool dequeues after Dispose still waits on the gate (and
    // returns at once), and neither event allocates a kernel handle.
    private readonly ManualResetEventSlim _release = new();
    private readonly ManualResetEventSlim _sentinelRan = new();

    private ThreadPoolSaturation()
    {
    }

    /// <summary>
    /// Gets whether nothing queued to the thread pool after <see cref="Start"/> has run yet.
    /// </summary>
    public bool HasHeld => !_sentinelRan.IsSet;

    /// <summary>
    /// Queues the blockers and their sentinel.
    /// </summary>
    public static ThreadPoolSaturation Start()
    {
        // The pool creates workers up to its minimum without delay, so the blockers must outnumber
        // a minimum raised by configuration (runtimeconfig, DOTNET_ThreadPool_ForceMinWorkerThreads).
        ThreadPool.GetMinThreads(out int minimumWorkers, out _);
        int blockerCount = Math.Max(1024, minimumWorkers * 4);

        var saturation = new ThreadPoolSaturation();
        for (int i = 0; i < blockerCount; i++)
        {
            // The bound only keeps a test that never disposes its saturation from parking the
            // process's workers for good.
            ThreadPool.UnsafeQueueUserWorkItem(static state => state._release.Wait(TimeSpan.FromMinutes(2)), saturation, preferLocal: false);
        }

        ThreadPool.UnsafeQueueUserWorkItem(static state => state._sentinelRan.Set(), saturation, preferLocal: false);
        return saturation;
    }

    /// <summary>
    /// Releases the blockers and waits for the pool to drain past the sentinel, so the next test
    /// starts on an empty queue.
    /// </summary>
    public void Dispose()
    {
        _release.Set();
        _sentinelRan.Wait(TimeSpan.FromSeconds(30));
    }
}
