using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    private readonly object _rewriteSync = new();
    private SqlTableRewrite? _rewrite;
    // Instance-local test observation at the final transaction cleanup boundary.
    internal Action? RewriteRolledBack { get; set; }
    internal bool RewriteCancellationRequested
    {
        get { lock (_rewriteSync) { return _rewrite?.IsCancellationRequested == true; } }
    }

    internal ValueTask RewriteAsync(SqlCatalogTable table, CancellationToken cancellationToken)
    {
        SqlTableRewrite? rewrite = null;
        lock (_rewriteSync)
        {
            ObjectDisposedException.ThrowIf(_database.IsClosed, _database);
            if (!_applyGate.Wait(0, cancellationToken))
            {
                throw new DatabaseObjectLockedException(_database.Name, _database.Name, "REWRITE TABLE");
            }
            IDisposable? admission = null;
            try
            {
                admission = _database.DdlInterlock.EnterApply(_database.Name);
                rewrite = new SqlTableRewrite(this, table, admission, cancellationToken);
                _rewrite = rewrite;
            }
            catch
            {
                // Preparation has not started; discard any constructor-owned resources.
                rewrite?.DiscardUnqueued();
                admission?.Dispose();
                _rewrite = null;
                _applyGate.Release();
                throw;
            }
        }
        // Never begin async lock preparation or register cancellation under admission.
        rewrite.StartPreparation();
        return new ValueTask(rewrite.Completion);
    }

    internal void QueueRewrite(SqlTableRewrite rewrite) => _database.Engine.ScheduleProvisioning(rewrite);

    internal void UnqueueRewrite(SqlTableRewrite rewrite) => _database.Engine.UnscheduleProvisioning(rewrite);

    internal Exception TranslateRewriteFailure(Exception failure) => _database.TranslateFailure(failure);

    internal void ReleaseRewrite()
    {
        lock (_rewriteSync)
        {
            _rewrite = null;
            _applyGate.Release();
        }
    }

    private ValueTask StopRewriteAsync()
    {
        SqlTableRewrite? rewrite;
        lock (_rewriteSync) { rewrite = _rewrite; }
        return rewrite?.StopAsync() ?? ValueTask.CompletedTask;
    }

    internal async Task<TransactionContext> AcquireRewriteLockAsync(SqlCatalogTable table, CancellationToken cancellationToken)
    {
        TransactionContext transaction = await _database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        try
        {
            // Never put an external lock wait on the engine's single provisioning pump.
            // A configured engine lock timeout also bounds a caller holding the blocker.
            var timeout = _database.Engine.EngineOptions.LockTimeout;
            using var scope = timeout == TimeSpan.Zero ? null : _database.Coordinator.LockManager.EnterLockTimeout(timeout);
            // Sequence grants precede the old table X grant; the worker reacquires only
            // grants already held by this same owner. Catalog/storage latches are innermost.
            var owned = _database.Catalog.Sequences.Where(sequence => sequence.OwnerTableId == table.ObjectId).OrderBy(sequence => sequence.ObjectId).ToArray();
            foreach (var sequence in owned)
            {
                await _database.Coordinator.LockManager.AcquireAsync(transaction.Sequence,
                    LockResource.Object(sequence.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
            }
            await _database.Coordinator.LockManager.AcquireAsync(transaction.Sequence,
                LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
            if (transaction.State != TransactionState.Active)
            { _database.Coordinator.LockManager.ReleaseAll(transaction.Sequence); throw new TransactionAbortedException(ErrorMessages.SequenceTransactionEnded); }
            if (_database.Catalog.Sequences.Any(sequence => sequence.OwnerTableId == table.ObjectId && !owned.Any(previous => previous.ObjectId == sequence.ObjectId)))
            { throw new SqlCatalogException("CS04067", ErrorMessages.SequenceChanged); }
            return transaction;
        }
        catch (Exception failure)
        {
            try { await RollbackRewriteAsync(transaction).ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            if (failure is TransactionLockTimeoutException) { throw SqlTimeoutException.Lock(failure, null); }
            throw;
        }
    }

    internal async Task RollbackRewriteAsync(TransactionContext? transaction)
    {
        if (transaction is { State: TransactionState.Active } && !_database.IsOffline)
        {
            await _database.Coordinator.RollbackAsync(transaction, CancellationToken.None).ConfigureAwait(false);
            RewriteRolledBack?.Invoke();
        }
    }

    internal async Task RunRewriteAsync(SqlCatalogTable table, TransactionContext transaction, CancellationToken cancellationToken)
    {
        long epoch = _database.TableReclamation.EnterStatement();
        var metrics = new SqlStatementMetrics();
        try
        {
            Exception? operationFailure = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var metadata = _database.DdlInterlock.EnterMetadata(_database.Name, definition: false);
                var statement = new SqlStatementContext(transaction, _database.Coordinator, databaseName: _database.Name,
                    provisioningProgress: (processed, total) => _database.ReportProvisioning(
                        new DatabaseProvisioningProgress("RewriteTable", table.Name, 1, 1, true, processed, total)));
                metrics = statement.Metrics;
                var executor = new SqlPlanExecutor(_database.DataStorage, _catalog, _database.IndexManager, _database.Definitions);
                await executor.RewriteTableAsync(table, statement, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                operationFailure = failure;
                throw;
            }
            finally
            {
                try
                {
                    await RollbackRewriteAsync(transaction).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure) when (operationFailure is not null)
                {
                    throw new AggregateException(operationFailure, cleanupFailure);
                }
                finally { _database.ReportProvisioning(null); }
            }
        }
        catch (Exception failure) when (_database.TranslateFailure(failure, metrics.SelfCommits != 0) is var translated && !ReferenceEquals(failure, translated))
        {
            throw translated;
        }
        finally
        {
            _database.TableReclamation.ExitStatement(epoch);
            _database.TableReclamation.Request();
        }
    }
}
