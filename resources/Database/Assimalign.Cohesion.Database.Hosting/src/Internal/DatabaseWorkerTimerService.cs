using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// Drives one claimed engine worker as a pool-scheduled timer loop: waits out the
/// worker's <see cref="IDatabaseEngineWorker.Interval"/>, then ticks one bounded
/// pass (<see cref="IDatabaseEngineWorker.RunIteration"/>) — the
/// <see cref="BackgroundService"/> execution-menu member for periodic,
/// non-latency-critical workers. A pass is bounded blocking work (a checkpoint's
/// flush, a purge batch), which the pool tolerates at these cadences.
/// </summary>
internal class DatabaseWorkerTimerService : BackgroundService
{
    private readonly IDatabaseEngineWorker _worker;

    internal DatabaseWorkerTimerService(IDatabaseEngineWorker worker)
    {
        _worker = worker;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_worker.Interval, cancellationToken).ConfigureAwait(false);
                _worker.RunIteration(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The host is stopping; exit the loop cleanly.
                break;
            }
        }
    }
}
