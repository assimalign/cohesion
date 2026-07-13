namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The checkpoint slot on the execution menu: a pool-scheduled timer loop driving
/// the engine's claimed checkpointer
/// (<see cref="DatabaseEngineWorkerKind.Checkpoint"/>).
/// </summary>
/// <remarks>
/// Pool-scheduled per the execution-model mapping in docs/DESIGN.md: checkpointing
/// is periodic and not latency-critical — a timer loop on the pool is enough. The
/// work itself is engine-owned (requirement R10); an embedded consumer gets the
/// identical loop self-scheduled by the engine.
/// </remarks>
internal sealed class CheckpointService : DatabaseWorkerTimerService
{
    internal CheckpointService(IDatabaseEngineWorker worker)
        : base(worker) { }
}
