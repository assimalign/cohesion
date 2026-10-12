using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed class SqlProvisioningWorker : DatabaseEngineWorker
{
    private readonly ConcurrentQueue<SqlDeferredMigration> _pending = new();
    private readonly ManualResetEventSlim _signal = new();
    private readonly ManualResetEventSlim _ready = new();

    internal SqlProvisioningWorker(SqlDatabaseEngine engine)
        : base(engine.Name + "/provisioning", DatabaseEngineWorkerKind.Provisioning, TimeSpan.FromSeconds(1))
    {
    }

    internal void Enqueue(SqlDeferredMigration migration)
    {
        _pending.Enqueue(migration);
        _signal.Set();
    }

    internal void CompleteBuild() => _ready.Set();

    protected override void WaitForTrigger(CancellationToken cancellationToken)
    {
        _ready.Wait(cancellationToken);
        _signal.Wait(cancellationToken);
    }

    protected override void RunIterationCore(CancellationToken cancellationToken)
    {
        _signal.Reset();
        while (!cancellationToken.IsCancellationRequested && _pending.TryDequeue(out var migration))
        {
            // The migration reports a permanent step failure by taking its database
            // offline. It does not retry the step; a later reopen may schedule a new attempt.
            migration.Run(cancellationToken);
        }
    }

    protected override ValueTask DisposeAsyncCore()
    {
        _signal.Dispose();
        _ready.Dispose();
        return ValueTask.CompletedTask;
    }
}
