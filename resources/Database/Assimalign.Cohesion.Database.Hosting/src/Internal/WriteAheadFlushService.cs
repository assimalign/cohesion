namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The write-ahead flush slot on the execution menu: a dedicated OS thread driving
/// the engine's claimed WAL group-commit flush worker
/// (<see cref="DatabaseEngineWorkerKind.WriteAheadFlush"/>).
/// </summary>
/// <remarks>
/// Dedicated thread per the execution-model mapping in docs/DESIGN.md: every grouped
/// commit waits on this loop, so it must be immune to thread-pool starvation. The
/// work itself is engine-owned (requirement R10) — this service merely schedules the
/// worker the host claimed; an embedded consumer gets the identical loop
/// self-scheduled by the engine.
/// </remarks>
internal sealed class WriteAheadFlushService : DatabaseWorkerThreadService
{
    internal WriteAheadFlushService(IDatabaseEngineWorker worker)
        : base(worker) { }
}
