namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>
/// The execution-menu member a claimed engine worker is mapped onto (see the
/// execution-model mapping in docs/DESIGN.md).
/// </summary>
public enum DatabaseWorkerExecution : byte
{
    /// <summary>
    /// A dedicated OS thread runs the worker's blocking pump
    /// (<see cref="IDatabaseEngineWorker.Run"/>) for the host's whole life — the
    /// right member for latency-critical or synchronous-I/O loops that must be
    /// immune to thread-pool starvation.
    /// </summary>
    DedicatedThread = 0,

    /// <summary>
    /// A pool-scheduled timer loop ticks one bounded pass
    /// (<see cref="IDatabaseEngineWorker.RunIteration"/>) per worker interval — the
    /// right member for periodic, non-latency-critical work.
    /// </summary>
    PooledTimer,
}
