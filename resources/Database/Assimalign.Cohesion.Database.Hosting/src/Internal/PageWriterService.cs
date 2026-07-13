namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The page-writer slot on the execution menu: a dedicated OS thread driving the
/// engine's claimed dirty-page write-back worker
/// (<see cref="DatabaseEngineWorkerKind.PageWriteBack"/>).
/// </summary>
/// <remarks>
/// Dedicated thread per the execution-model mapping in docs/DESIGN.md: a paced
/// synchronous write-back loop owns its thread for its whole life rather than
/// occupying the pool. The work itself is engine-owned (requirement R10) — this
/// service merely schedules the worker the host claimed; an embedded consumer gets
/// the identical loop self-scheduled by the engine. Write-back coordinates with the
/// buffer pool's write-ahead gate (journal durable past a page's LSN before the page
/// reaches the data file).
/// </remarks>
internal sealed class PageWriterService : DatabaseWorkerThreadService
{
    internal PageWriterService(IDatabaseEngineWorker worker)
        : base(worker) { }
}
