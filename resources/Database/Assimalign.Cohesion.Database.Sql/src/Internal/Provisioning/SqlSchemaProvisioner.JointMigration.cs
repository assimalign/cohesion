using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Internal;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    internal Action<string>? JointStage { get; set; }
    internal Action? JointSpaceCounting { get; set; }
    internal long JointSpaceNeed { get; private set; }
    internal long JointSpaceIndexPages { get; private set; }
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
        SqlJointMigrationPlan plan;
        try { JointStage?.Invoke("Planning"); plan = executor.PlanJointMigration(schema, script, cancellationToken); }
        catch (Exception refusal)
        {
            // Storage, corruption and lifecycle identities take precedence (#1364).
            // Only the planner's data-independent validation vocabulary is permanent policy.
            var translated = _database.TranslateFailure(refusal);
            if (!ReferenceEquals(translated, refusal)) { throw translated; }
            if (DatabaseFailure.IsCorruption(refusal)) { throw; }
            if (refusal is DatabaseException coded && coded.Diagnostics.Any(diagnostic => diagnostic.Code is { } code &&
                (code.StartsWith("CS06", StringComparison.Ordinal) || code.StartsWith("CK06", StringComparison.Ordinal))))
            { throw; }
            if (refusal is SqlPlanningException or SqlUnsupportedQueryException or SqlEvaluationException or SqlExecutionException or
                SqlCatalogException or DatabaseTypeException or DatabaseObjectLockedException or ArgumentException)
            { throw PlanningRefusal(schema, script.Steps.Count, refusal); }
            throw;
        }
        var intent = new SqlCatalogPendingMigration(schema.Hash, schema.CanonicalDocument,
            script.Steps.Select((step, index) => new SqlCatalogMigrationStep("Joint/" + step.Kind,
                step.Operation.ParentName ?? step.Operation.ObjectName, step.Operation.ObjectName, index + 1)).ToArray(), script.Steps.Count);
        TransactionContext? locks = null;
        IDisposable? metadata = null;
        bool saved = false;
        bool spaceRefused = false;
        var failed = new DatabaseProvisioningProgress("JointMigration", schema.Name, 1, intent.StepCount, false,
            failureCode: SqlProvisioningCodes.JointFailed);
        var failures = new System.Collections.Generic.List<Exception>(3);
        try
        {
            // EnterApply excludes all session DDL here. Transfer this compatible
            // metadata lease with the reservation; the worker must never reacquire it.
            metadata = _database.DdlInterlock.EnterMetadata(_database.Name, definition: false);
            locks = await _database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            // Lock/latch order: fail-fast apply admission -> owned sequence X (ascending)
            // -> every old/touched table X (ascending) BEFORE queueing -> prepared owners X
            // -> coordinator apply gate -> tree/page latches. No queue monitor spans work.
            // Non-definition metadata admission is retained from Build; publication takes snapshot gate
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
            // Build checks only the metadata floor, independent of owner size.
            // The worker counts pages before any prepared write, including after resume.
            try { CheckJointSpace(JointSpaceFloor(intent)); }
            catch { spaceRefused = true; throw; }
            await _catalog.SavePendingMigrationAsync(intent, cancellationToken).ConfigureAwait(false);
            saved = true;
            JointStage?.Invoke("IntentDurable");
            JointStage?.Invoke("LocksReserved");
            return new SqlDeferredMigration(this, schema, script.Steps, intent, locks,
                plan.Touched.Select(table => table.ObjectId).ToHashSet(), cancellationToken)
                { JointPlan = plan, JointFailureProgress = failed, JointMetadata = metadata };
        }
        catch (Exception failure)
        {
            _database.ReportProvisioning(failed);
            failures.Add(failure);
            try
            {
                if (!spaceRefused && (saved || _catalog.PendingMigration is not null))
                { await _catalog.DiscardJointIntentAsync(CancellationToken.None).ConfigureAwait(false); }
            }
            catch (Exception cleanup) { failures.Add(cleanup); }
            try { await RollbackRewriteAsync(locks).ConfigureAwait(false); }
            catch (Exception cleanup) { failures.Add(cleanup); }
            finally { metadata?.Dispose(); }
            Exception cause = failures.Count == 1 ? failure : new AggregateException(failures);
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
            BeginDeferredTrace(migration, 0);
            PublishProgress(migration.Intent.Steps[0], migration.Intent.StepCount, running: true, 0);
            var statement = new SqlStatementContext(migration.Locks, _database.Coordinator, migration.Schema.Name,
                _database.Name, provisioningProgress: (processed, total) =>
                {
                    PublishProgress(migration.Intent.Steps[0], migration.Intent.StepCount, true, processed, total);
                    TraceDeferredProgress(migration, migration.Intent.Steps[0], processed, total);
                    DeferredProgress?.Invoke(processed, total);
                }, checkpointCopy: true);
            JointMetrics = statement.Metrics;
            BeforeDeferredStep?.Invoke(migration.Steps[0], cancellationToken);
            AdmitJointSpace(migration.JointPlan!, migration.Intent, cancellationToken);
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
            if (IsJointUnconfirmed(failure))
            {
                // Publication may already be durable. Leave trees and intent to recovery;
                // neither discard nor reclamation may decide the commit's outcome here.
                _database.ReportProvisioning(new DatabaseProvisioningProgress("JointMigration", migration.Schema.Name,
                    1, migration.Intent.StepCount, false, failureCode: SqlProvisioningCodes.JointUnconfirmed));
                var unconfirmed = CodedMigrationFailure(SqlProvisioningCodes.JointUnconfirmed,
                    ErrorMessages.JointPublicationUnconfirmed, failure);
                FailDeferredTrace(migration, 0, unconfirmed);
                EndDeferredTrace(migration, 0, "Unconfirmed");
                throw unconfirmed;
            }
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
        // Preserve space admission's byte counts in the caller-visible report.
        if (failure is SqlSchemaMigrationException migration && migration.Diagnostics.Any(diagnostic => diagnostic.Code == SqlProvisioningCodes.JointFailed))
        { return migration; }
        return CodedMigrationFailure(SqlProvisioningCodes.JointFailed, ErrorMessages.JointMigrationFailed, failure);
    }

    private SqlSchemaMigrationException PlanningRefusal(SqlCompiledSchema schema, int steps, Exception refusal, string? message = null)
    {
        message ??= string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.JointPlanningRefused, refusal.Message);
        _database.ReportProvisioning(new DatabaseProvisioningProgress("Planning", schema.Name, 1, Math.Max(1, steps),
            false, failureCode: SqlProvisioningCodes.PolicyRefused));
        TraceStage("Planning", SqlProvisioningCodes.PolicyRefused, stepCount: steps);
        return CodedMigrationFailure(SqlProvisioningCodes.PolicyRefused, message, refusal);
    }

    private static SqlSchemaMigrationException CodedMigrationFailure(string code, string message, Exception? cause = null)
        => new(message, new DatabaseException(message,
            [new Diagnostic { Code = code, Message = message, Severity = DiagnosticSeverity.Error }], cause));

    internal static bool IsJointUnconfirmed(Exception failure)
        => failure is DatabaseException coded && coded.Diagnostics.Any(diagnostic => diagnostic.Code == SqlProvisioningCodes.JointUnconfirmed) ||
            failure is AggregateException aggregate && aggregate.InnerExceptions.Any(IsJointUnconfirmed) ||
            failure.InnerException is { } inner && IsJointUnconfirmed(inner);

    internal static bool IsCommitUnconfirmed(Exception failure)
        => failure is StorageOfflineException { CommitRecordWritten: true } or TransactionCommitUnconfirmedException or DatabaseTransactionCommitUnconfirmedException ||
            failure is AggregateException aggregate && aggregate.InnerExceptions.Any(IsCommitUnconfirmed) ||
            failure.InnerException is { } inner && IsCommitUnconfirmed(inner);

}
