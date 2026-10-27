using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[System.Diagnostics.DebuggerDisplay("SqlTableRewrite")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlTableRewrite
{
    private readonly SqlSchemaProvisioner _owner;
    private readonly SqlCatalogTable _table;
    private readonly IDisposable _admission;
    private readonly CancellationTokenSource _stopping;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TransactionContext? _transaction;
    private CancellationTokenRegistration _cancellation;
    private int _claimed;
    private bool _finished;

    internal SqlTableRewrite(SqlSchemaProvisioner owner, SqlCatalogTable table, IDisposable admission,
        CancellationToken cancellationToken)
    {
        _owner = owner;
        _table = table;
        _admission = admission;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    internal Task Completion => _completion.Task;
    internal bool IsCancellationRequested => _stopping.IsCancellationRequested;

    internal void DiscardUnqueued()
    {
        lock (_stopping)
        {
            _finished = true;
            _stopping.Dispose();
        }
    }

    internal void StartPreparation() => _ = PrepareAsync();

    private async Task PrepareAsync()
    {
        try
        {
            _transaction = await _owner.AcquireRewriteLockAsync(_table, _stopping.Token).ConfigureAwait(false);
            _stopping.Token.ThrowIfCancellationRequested();
            _owner.QueueRewrite(this);
            ObserveCancellation();
        }
        catch (Exception failure)
        {
            // Preparation owns the item until enqueue succeeds. Enqueue rollback is
            // atomic; no worker can claim a failed enqueue or a pending lock waiter.
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) == 0)
            {
                try { await _owner.RollbackRewriteAsync(_transaction).ConfigureAwait(false); }
                catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
                Finish(_owner.TranslateRewriteFailure(failure));
            }
        }
        finally { _prepared.TrySetResult(); }
    }

    private void ObserveCancellation()
    {
        lock (_stopping)
        {
            if (_finished) { return; }
            // A canceled queued operation can unwind without waiting behind another
            // database's provisioning work. A claimed operation observes its linked token.
            _cancellation = _stopping.Token.UnsafeRegister(static state =>
                ((SqlTableRewrite)state!).Run(new CancellationToken(canceled: true)), this);
            if (_finished) { _cancellation.Dispose(); }
        }
    }

    internal void Run(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) { return; }
        Exception? error = null;
        try
        {
            // Cancellation can win while the pump is blocked on another database.
            // Unlink before cleanup so completed items retain no queue ownership.
            _owner.UnqueueRewrite(this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            _owner.RunRewriteAsync(_table, _transaction!, linked.Token).GetAwaiter().GetResult();
        }
        catch (Exception failure)
        {
            // The caller owns the failure. Keep the shared provisioning pump available for
            // other databases even when a maintenance operation fails.
            error = failure;
        }
        Finish(error);
    }

    private void Finish(Exception? error)
    {
        _admission.Dispose();
        _owner.ReleaseRewrite();
        lock (_stopping)
        {
            _finished = true;
            _cancellation.Dispose();
            _stopping.Dispose();
        }
        _joined.TrySetResult();
        // Completion also promises admission was released: a caller can immediately
        // retry or apply a schema after awaiting it, on every success and failure path.
        if (error is OperationCanceledException canceled) { _completion.TrySetCanceled(canceled.CancellationToken); }
        else if (error is not null) { _completion.TrySetException(error); }
        else { _completion.TrySetResult(); }
    }

    internal async ValueTask StopAsync()
    {
        CancellationToken stopped;
        lock (_stopping)
        {
            if (_finished) { return; }
            stopped = _stopping.Token;
            _stopping.Cancel();
        }
        await _prepared.Task.ConfigureAwait(false);
        // A queued item must unwind admission even after the pump has stopped.
        Run(stopped);
        await _joined.Task.ConfigureAwait(false);
    }

    private sealed class DebugView
    {
        public DebugView(SqlTableRewrite value) { _ = value; }
    }
}
