using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlProvisioningWorker {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlProvisioningWorker.DebugView))]
internal sealed class SqlProvisioningWorker : DatabaseEngineWorker
{
    private readonly ConcurrentQueue<Action<CancellationToken>> _pending = new();
    private readonly ManualResetEventSlim _signal = new();
    private readonly ManualResetEventSlim _ready = new();

    internal SqlProvisioningWorker(SqlDatabaseEngine engine)
        : base(engine.Name + "/provisioning", DatabaseEngineWorkerKind.Provisioning, TimeSpan.FromSeconds(1))
    {
    }

    internal void Enqueue(SqlDeferredMigration migration)
    {
        _pending.Enqueue(migration.Run);
        _signal.Set();
    }

    internal void Enqueue(SqlTableRewrite rewrite)
    {
        _pending.Enqueue(rewrite.Run);
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
            migration(cancellationToken);
        }
    }

    protected override ValueTask DisposeAsyncCore()
    {
        _signal.Dispose();
        _ready.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlProvisioningWorker _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlProvisioningWorker value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public global::Assimalign.Cohesion.Database.DatabaseEngineWorkerKind Kind => _target.Kind;
        public global::System.TimeSpan Interval => _target.Interval;
    }
}
