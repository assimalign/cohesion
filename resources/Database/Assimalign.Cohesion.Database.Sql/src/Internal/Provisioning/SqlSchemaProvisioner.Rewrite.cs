using System;
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
        SqlTableRewrite rewrite;
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
                _database.Engine.ScheduleProvisioning(rewrite);
            }
            catch
            {
                admission?.Dispose();
                _rewrite = null;
                _applyGate.Release();
                throw;
            }
        }
        // Never register a synchronous cancellation callback under the admission monitor.
        // It may claim queued work and release admission immediately.
        rewrite.ObserveCancellation();
        return new ValueTask(rewrite.Completion);
    }

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

    internal async Task RunRewriteAsync(SqlCatalogTable table, CancellationToken cancellationToken)
    {
        TransactionContext? transaction = null;
        long epoch = _database.TableReclamation.EnterStatement();
        var metrics = new SqlStatementMetrics();
        try
        {
            Exception? operationFailure = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                transaction = await _database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
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
                    if (transaction is { State: TransactionState.Active } && !_database.IsOffline)
                    {
                        await _database.Coordinator.RollbackAsync(transaction, CancellationToken.None).ConfigureAwait(false);
                        RewriteRolledBack?.Invoke();
                    }
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
