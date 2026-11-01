using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    internal Action<string>? JointStage { get; set; }
    internal SqlStatementMetrics? JointMetrics { get; private set; }
    private (CancellationTokenSource Stop, TaskCompletionSource Finished)? _jointPreparing;

    private (CancellationTokenSource Stop, TaskCompletionSource Finished) BeginJointPreparation(CancellationToken token)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            lock (_rewriteSync)
            {
                ObjectDisposedException.ThrowIf(_database.IsClosed, _database);
                _jointPreparing = (stop, finished);
            }
            return (stop, finished);
        }
        catch { stop.Dispose(); throw; }
    }

    private void EndJointPreparation((CancellationTokenSource Stop, TaskCompletionSource Finished) preparation)
    {
        lock (_rewriteSync)
        {
            _jointPreparing = null;
            preparation.Stop.Dispose();
        }
        preparation.Finished.TrySetResult();
    }

    private async ValueTask StopJointPreparationAsync()
    {
        Task? finished = null;
        lock (_rewriteSync)
        {
            if (_jointPreparing is { } preparation)
            {
                preparation.Stop.Cancel();
                finished = preparation.Finished.Task;
            }
        }
        // This is a lifecycle join, outside all queue/catalog/apply latches. Every
        // preparation lock wait is still registered under its one lock-manager owner.
        if (finished is not null) { await finished.ConfigureAwait(false); }
    }

    private bool RequiresJointMigration(SqlSchemaMigrationPlan plan)
    {
        foreach (var operation in plan.Operations.Where(operation => operation.Kind == SqlSchemaMigrationOperationKind.AlterColumn))
        {
            if (!_catalog.TryGetTable(SqlPlanner.DefaultSchema, operation.ParentName!, out var table) ||
                table.FindColumn(operation.ObjectName) is not { } previous) { continue; }
            var type = ResolveColumnType(operation.Column!, _database.Engine.Types, out string? domain);
            if (SqlMigrationScriptGenerator.RequiresTypeRewrite(previous.Type, previous.DomainName, type, domain))
            { return true; }
        }
        return false;
    }

    private async ValueTask<SqlDeferredMigration> PrepareJointMigrationAsync(SqlCompiledSchema schema,
        SqlMigrationScript script, CancellationToken cancellationToken)
    {
        var executor = new SqlPlanExecutor(_database.DataStorage, _catalog, _database.IndexManager, _database.Definitions);
        var plan = executor.PlanJointMigration(schema, script, cancellationToken);
        var intent = new SqlCatalogPendingMigration(schema.Hash, schema.CanonicalDocument,
            script.Steps.Select((step, index) => new SqlCatalogMigrationStep("Joint/" + step.Kind,
                step.Operation.ParentName ?? step.Operation.ObjectName, step.Operation.ObjectName, index + 1)).ToArray(), script.Steps.Count);
        TransactionContext? locks = null;
        var failed = new DatabaseProvisioningProgress("JointMigration", schema.Name, 1, intent.StepCount, false,
            failureCode: SqlProvisioningCodes.JointFailed);
        var failures = new System.Collections.Generic.List<Exception>(3);
        try
        {
            await _catalog.SavePendingMigrationAsync(intent, cancellationToken).ConfigureAwait(false);
            JointStage?.Invoke("IntentDurable");
            locks = await _database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            // Lock/latch order: fail-fast apply admission -> owned sequence X (ascending)
            // -> every old/touched table X (ascending) BEFORE queueing -> prepared owners X
            // -> coordinator apply gate -> tree/page latches. No queue monitor spans work.
            // Metadata admission on the worker is fail-fast; publication takes snapshot gate
            // -> coordinator snapshot pin -> catalog monitor -> catalog storage bracket.
            // No data apply gate or page/tree latch spans publication. All waiting uses this
            // one lock owner, including user admission with the user's live transaction owner.
            using var timeout = _database.Engine.EngineOptions.LockTimeout == TimeSpan.Zero ? null
                : _database.Coordinator.LockManager.EnterLockTimeout(_database.Engine.EngineOptions.LockTimeout);
            var owned = _catalog.Sequences.Where(sequence => plan.Touched.Any(table => table.ObjectId == sequence.OwnerTableId))
                .OrderBy(sequence => sequence.ObjectId).ToArray();
            foreach (var sequence in owned)
            { await _database.Coordinator.LockManager.AcquireAsync(locks.Sequence, LockResource.Object(sequence.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false); }
            foreach (var table in plan.Touched)
            {
                await _database.Coordinator.LockManager.AcquireAsync(locks.Sequence, LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
                if (!_catalog.TryGetTable(table.Schema, table.Name, out var current) || !ReferenceEquals(table, current))
                { throw new SqlCatalogException("CS04064", ErrorMessages.CatalogSaveSchemaDocumentSqlCatalog); }
            }
            if (locks.State != TransactionState.Active) { throw new TransactionAbortedException(ErrorMessages.SequenceTransactionEnded); }
            JointStage?.Invoke("LocksReserved");
            return new SqlDeferredMigration(this, schema, script.Steps, intent, locks,
                plan.Touched.Select(table => table.ObjectId).ToHashSet(), cancellationToken)
                { JointPlan = plan, JointFailureProgress = failed };
        }
        catch (Exception failure)
        {
            _database.ReportProvisioning(failed);
            failures.Add(failure);
            try { await _catalog.DiscardJointIntentAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { failures.Add(cleanup); }
            try { await RollbackRewriteAsync(locks).ConfigureAwait(false); }
            catch (Exception cleanup) { failures.Add(cleanup); }
            Exception cause = failures.Count == 1 ? failure : new AggregateException(failures);
            if (ReferenceEquals(cause, failure) && failure is TransactionLockTimeoutException) { throw SqlTimeoutException.Lock(failure, null); }
            if (ReferenceEquals(cause, failure) && failure is TransactionDeadlockException deadlock) { throw SqlExecutionException.Deadlock(deadlock); }
            var coded = JointFailure(cause);
            EnsureFailureTrace(schema);
            TraceStage("JointMigration", SqlProvisioningCodes.JointFailed, stepCount: intent.StepCount);
            TraceFailure(coded);
            if (ReferenceEquals(cause, failure) && (failure is OperationCanceledException or OutOfMemoryException)) { throw; }
            throw coded;
        }
    }

    private async Task RunJointMigrationAsync(SqlDeferredMigration migration, CancellationToken cancellationToken)
    {
        long? epoch = null;
        var failed = migration.JointFailureProgress!;
        JointMetrics = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            epoch = _database.TableReclamation.EnterStatement();
            using var metadata = _database.DdlInterlock.EnterMetadata(_database.Name, definition: false);
            BeginDeferredTrace(migration, 0);
            PublishProgress(migration.Intent.Steps[0], migration.Intent.StepCount, running: true, 0);
            var statement = new SqlStatementContext(migration.Locks, _database.Coordinator, migration.Schema.Name,
                _database.Name, provisioningProgress: (processed, total) =>
                {
                    PublishProgress(migration.Intent.Steps[0], migration.Intent.StepCount, true, processed, total);
                    TraceDeferredProgress(migration, migration.Intent.Steps[0], processed, total);
                    DeferredProgress?.Invoke(processed, total);
                });
            JointMetrics = statement.Metrics;
            BeforeDeferredStep?.Invoke(migration.Steps[0], cancellationToken);
            var executor = new SqlPlanExecutor(_database.DataStorage, _catalog, _database.IndexManager, _database.Definitions);
            await executor.ExecuteJointMigrationAsync(migration.JointPlan!, migration.Schema, statement,
                phase => JointStage?.Invoke(phase), cancellationToken).ConfigureAwait(false);
            _formatCheckedStateDocument = migration.Schema.CanonicalDocument;
            _database.ReportProvisioning(null);
            EndDeferredTrace(migration, 0, "Applied");
        }
        catch (Exception) when (JointMetrics?.SelfCommits > 0)
        {
            // The complete new schema is already durable. Deliver that success even
            // if late cancellation or an observer throws after publication.
            _database.ReportProvisioning(null);
            EndDeferredTrace(migration, 0, "Applied");
        }
        catch (Exception failure)
        {
            // No compensation: published objects and committed rows were never changed.
            // Remove the intent; retry/next Build replans from the old applied marker.
            _database.ReportProvisioning(failed);
            Exception cause = failure;
            try { await _catalog.DiscardJointIntentAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { cause = new AggregateException(failure, cleanup); }
            var coded = JointFailure(cause);
            FailDeferredTrace(migration, 0, coded);
            EndDeferredTrace(migration, 0, "Error");
            if (ReferenceEquals(cause, failure) && (failure is OperationCanceledException or OutOfMemoryException)) { throw; }
            throw coded;
        }
        finally
        {
            if (epoch is { } entered) { _database.TableReclamation.ExitStatement(entered); }
            _database.TableReclamation.Request();
        }
    }

    private static SqlSchemaMigrationException JointFailure(Exception failure)
    {
        string message = ErrorMessages.JointMigrationFailed;
        return new SqlSchemaMigrationException(message, new DatabaseException(message,
            [new Diagnostic { Code = SqlProvisioningCodes.JointFailed, Message = message, Severity = DiagnosticSeverity.Error }], failure));
    }
}
