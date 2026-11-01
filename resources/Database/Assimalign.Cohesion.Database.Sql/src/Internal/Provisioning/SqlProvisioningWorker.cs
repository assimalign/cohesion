using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlProvisioningWorker {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlProvisioningWorker.DebugView))]
internal sealed class SqlProvisioningWorker : DatabaseEngineWorker
{
    private readonly object _pendingSync = new();
    private readonly LinkedList<Work> _pending = new();
    private readonly Dictionary<SqlTableRewrite, LinkedListNode<Work>> _queuedRewrites = new();
    private readonly ManualResetEventSlim _signal = new();
    private readonly ManualResetEventSlim _ready = new();
    private bool _closed;

    internal SqlProvisioningWorker(SqlDatabaseEngine engine)
        : base(engine.Name + "/provisioning", DatabaseEngineWorkerKind.Provisioning, TimeSpan.FromSeconds(1))
    {
    }

    internal void Enqueue(SqlDeferredMigration migration) => Enqueue(new Work(migration, null));

    internal void Enqueue(SqlTableRewrite rewrite) => Enqueue(new Work(null, rewrite));

    internal int PendingRewriteCount
    {
        get { lock (_pendingSync) { return _queuedRewrites.Count; } }
    }

    // Test-only producer failure injection, invoked outside the queue monitor.
    internal Action? BeforeEnqueue { get; set; }

    private void Enqueue(Work work)
    {
        BeforeEnqueue?.Invoke();
        // Admission may enqueue while holding the rewrite monitor. This queue monitor
        // protects links only: it never spans invocation or completion of any work.
        lock (_pendingSync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var node = _pending.AddLast(work);
            try
            {
                if (work.Rewrite is { } rewrite) { _queuedRewrites.Add(rewrite, node); }
                _signal.Set();
            }
            catch
            {
                _pending.Remove(node);
                if (work.Rewrite is { } rewrite) { _queuedRewrites.Remove(rewrite); }
                throw;
            }
        }
    }

    internal void Remove(SqlTableRewrite rewrite)
    {
        lock (_pendingSync)
        {
            if (_queuedRewrites.Remove(rewrite, out var node)) { _pending.Remove(node); }
        }
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
        while (!cancellationToken.IsCancellationRequested)
        {
            Work work;
            lock (_pendingSync)
            {
                if (_pending.First is not { } node) { break; }
                work = node.Value;
                _pending.RemoveFirst();
                if (work.Rewrite is { } rewrite) { _queuedRewrites.Remove(rewrite); }
            }
            // Per-step migrations retain their offline failure policy. Joint migrations
            // discard unpublished work and keep the old schema online. Neither retries
            // here; only an explicit later schema application can schedule joint work.
            if (work.Rewrite is { } replacement) { replacement.Run(cancellationToken); }
            else { work.Migration!.Run(cancellationToken); }
        }
    }

    protected override ValueTask DisposeAsyncCore()
    {
        lock (_pendingSync)
        {
            _closed = true;
            _pending.Clear();
            _queuedRewrites.Clear();
            _signal.Dispose();
            _ready.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private readonly record struct Work(SqlDeferredMigration? Migration, SqlTableRewrite? Rewrite);

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
