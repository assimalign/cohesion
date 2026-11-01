using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlDeferredMigration")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDeferredMigration.DebugView))]
internal sealed class SqlDeferredMigration
{
    private readonly SqlSchemaProvisioner _owner;
    private readonly CancellationTokenSource _stopping;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _claimed;
    private bool _finished;

    internal SqlDeferredMigration(SqlSchemaProvisioner owner, SqlCompiledSchema schema,
        IReadOnlyList<SqlMigrationScriptStep> steps, SqlCatalogPendingMigration intent,
        TransactionContext locks, IReadOnlySet<ulong> tables, CancellationToken preparationCancellation = default)
    {
        _owner = owner;
        Schema = schema;
        Steps = steps;
        Intent = intent;
        Locks = locks;
        Tables = tables;
        _stopping = preparationCancellation.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(preparationCancellation) : new();
    }

    internal SqlCompiledSchema Schema { get; }
    internal IReadOnlyList<SqlMigrationScriptStep> Steps { get; }
    internal SqlCatalogPendingMigration Intent { get; }
    internal TransactionContext Locks { get; }
    internal IReadOnlySet<ulong> Tables { get; }
    internal SqlJointMigrationPlan? JointPlan { get; init; }
    internal DatabaseProvisioningProgress? JointFailureProgress { get; init; }
    internal IDisposable? JointMetadata { get; init; }

    internal async Task RunInlineAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
        { throw new OperationCanceledException(cancellationToken); }
        bool entered = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            entered = true;
            await _owner.RunDeferredAsync(this, linked.Token, propagateJointFailure: true).ConfigureAwait(false);
        }
        catch (Exception) when (!entered && JointPlan is not null)
        {
            // Even a failed linked-token allocation must discard the intent and release
            // the retained owner/gate. Cancellation enters the joint cleanup boundary.
            await _owner.RunDeferredAsync(this, new CancellationToken(true)).ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_stopping)
            {
                _finished = true;
                _stopping.Dispose();
            }
            _completion.TrySetResult();
        }
    }

    internal void Run(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
        {
            return;
        }
        bool entered = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            entered = true;
            _owner.RunDeferredAsync(this, linked.Token).GetAwaiter().GetResult();
        }
        catch (Exception) when (!entered && JointPlan is not null)
        {
            _owner.RunDeferredAsync(this, new CancellationToken(true)).GetAwaiter().GetResult();
            throw;
        }
        finally
        {
            lock (_stopping)
            {
                _finished = true;
                _stopping.Dispose();
            }
            _completion.TrySetResult();
        }
    }

    internal async ValueTask StopAsync()
    {
        CancellationToken stopped;
        lock (_stopping)
        {
            if (_finished)
            {
                return;
            }
            _stopping.Cancel();
            stopped = _stopping.Token;
        }
        // A queued migration must release its reservation even if the engine's pump
        // has already stopped. A claimed one is joined before storage can close.
        Run(stopped);
        await _completion.Task.ConfigureAwait(false);
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDeferredMigration value)
        {
            _ = value;
        }
    }
}
