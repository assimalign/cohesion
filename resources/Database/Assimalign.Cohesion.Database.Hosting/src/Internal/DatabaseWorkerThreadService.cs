using System;
using System.Threading;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// Drives one claimed engine worker's blocking pump
/// (<see cref="IDatabaseEngineWorker.Run"/>) on a dedicated OS thread — the
/// <see cref="DedicatedThreadService"/> execution-menu member. Named subclasses
/// exist per worker kind so the thread (named after the service type) is
/// recognizable in dumps.
/// </summary>
internal class DatabaseWorkerThreadService : DedicatedThreadService
{
    private readonly IDatabaseEngineWorker _worker;

    internal DatabaseWorkerThreadService(IDatabaseEngineWorker worker)
    {
        _worker = worker;
    }

    /// <inheritdoc />
    protected override void Run(CancellationToken cancellationToken)
        => _worker.Run(cancellationToken);
}
