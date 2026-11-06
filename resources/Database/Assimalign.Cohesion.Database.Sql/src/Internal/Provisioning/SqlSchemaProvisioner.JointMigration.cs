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
    // Test-only differential oracle: force the same target through physical replacement.
    internal bool ForceJointRewrite { get; set; }
    // Test-only observation after execution cleanup, before the lifecycle lease ends.
    internal Action? JointPreparationFinishing { get; set; }
    internal Action? JointSpaceCounting { get; set; }
    internal long JointSpaceNeed { get; private set; }
    internal long JointSpaceIndexPages { get; private set; }
    internal long JointSpaceReplacementBytes { get; private set; }
    // Full engine reserve E, retained through both discard and publication.
    internal long JointSpaceDiscardBytes { get; private set; }
    internal long JointSpacePublicationBytes { get; private set; }
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
        try { JointPreparationFinishing?.Invoke(); }
        finally
        {
            lock (_rewriteSync)
            {
                _jointPreparing = null;
                preparation.Stop.Dispose();
            }
            preparation.Finished.TrySetResult();
        }
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
        SqlMigrationScript script, CancellationToken cancellationToken, bool deferredBuild, bool deferredSteps)
    {
        var executor = new SqlPlanExecutor(_database.DataStorage, _catalog, _database.IndexManager, _database.Definitions);
        SqlJointMigrationPlan plan;
        try
        {
            JointStage?.Invoke("Planning");
            plan = executor.PlanJointMigration(schema, script, cancellationToken, ForceJointRewrite);
            if (_catalog.PendingMigration is { } recorded)
            {
                var choices = recorded.Steps.Where(step => step.Kind is "Joint/KeepOwner" or "Joint/RewriteOwner").ToArray();
                var existing = plan.Tables.Where(table => table.Previous is not null).ToArray();
                if (choices.Length != existing.Length || existing.Any(table => choices.Count(step =>
                    step.Table.Equals(table.Shape.Name, StringComparison.OrdinalIgnoreCase) &&
                    step.ObjectName.Equals(table.Shape.Name, StringComparison.OrdinalIgnoreCase)) != 1))
                { throw new SqlCatalogException("CS06059", ErrorMessages.JointClassificationInvalid); }
                if (existing.Any(table => choices.Single(step => step.Table.Equals(table.Shape.Name, StringComparison.OrdinalIgnoreCase)).Kind !=
                    (table.KeepsOwner ? "Joint/KeepOwner" : "Joint/RewriteOwner")))
                {
                    // Registered domains are outside the schema hash. The old schema
                    // is still online: discard unpublished work and plan with today's types.
                    await DiscardMismatchedIntentAsync(schema, script.Steps.Count, cancellationToken).ConfigureAwait(false);
                    plan = executor.PlanJointMigration(schema, script, cancellationToken, ForceJointRewrite);
                }
            }
        }
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
        var steps = script.Steps.Select((step, index) => new SqlCatalogMigrationStep("Joint/" + step.Kind,
            step.Operation.ParentName ?? step.Operation.ObjectName, step.Operation.ObjectName, index + 1)).ToList();
        foreach (var table in plan.Tables.Where(table => table.Previous is not null))
        {
            steps.Add(new SqlCatalogMigrationStep(table.KeepsOwner ? "Joint/KeepOwner" : "Joint/RewriteOwner",
                table.Shape.Name, table.Shape.Name, steps.Count + 1));
        }
        if (steps.Count == 0)
        { steps.Add(new SqlCatalogMigrationStep("Joint/Schema", schema.Name, schema.Name, 1)); }
        var intent = new SqlCatalogPendingMigration(schema.Hash, schema.CanonicalDocument, steps.ToArray(), steps.Count);
        TransactionContext? locks = null;
        IDisposable? metadata = null;
        bool saved = false;
        bool spaceRefused = false;
        var failed = new DatabaseProvisioningProgress("JointMigration", schema.Name, 1, Math.Max(1, script.Steps.Count), false,
            failureCode: SqlProvisioningCodes.JointFailed);
        var failures = new System.Collections.Generic.List<Exception>(3);
        try
        {
            // EnterApply excludes all session DDL here. Transfer this compatible
            // metadata lease with the reservation; the worker must never reacquire it.
            metadata = _database.DdlInterlock.EnterMetadata(_database.Name, definition: false);
            locks = await _database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            // Lock/latch order: fail-fast apply admission -> object zero X when no
            // old table exists -> owned sequence X (ascending)
            // -> every old/touched table X (ascending) BEFORE queueing -> prepared owners X
            // -> coordinator apply gate -> tree/page latches. No queue monitor spans work.
            // Every deferred job uses this order, including its cheap-step tables and required FK neighbours.
            // Non-definition metadata admission is retained from Build; publication takes snapshot gate
            // -> coordinator snapshot pin -> catalog monitor -> catalog storage bracket.
            // No data apply gate or page/tree latch spans publication. All waiting uses this
            // one lock owner, including user admission with the user's live transaction owner.
            using var timeout = _database.Engine.EngineOptions.LockTimeout == TimeSpan.Zero ? null
                : _database.Coordinator.LockManager.EnterLockTimeout(_database.Engine.EngineOptions.LockTimeout);
            // New-table-only jobs have no old heap to rendezvous on. Object zero
            // is not a catalog identity; reserve it before any positive identities.
            // Admission uses this same lock-manager resource, including deadlock,
            // cancellation and disposal handling, rather than a private task wait.
            if (plan.Touched.Length == 0)
            { await _database.Coordinator.LockManager.AcquireAsync(locks.Sequence, LockResource.Object(0), LockMode.Exclusive, cancellationToken).ConfigureAwait(false); }
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
            // Cheap inline jobs admit before writing their intent. Populated deferred
            // work still counts on the worker. Keep its pre-intent budget boundary,
            // then check the remaining phase budget against live space on the worker.
            bool deferred = deferredSteps || deferredBuild && plan.Tables.Any(table =>
                !table.KeepsOwner && table.Previous is { } old && _database.DataStorage.HasOwnerPages(old.ObjectId));
            long availableBeforeIntent = _database.Engine.GetAvailableMigrationBytes(_database.Name);
            try
            {
                if (deferred) { CheckJointSpace(JointSpaceFloor(intent)); }
                else { AdmitJointSpace(plan, intent, cancellationToken); }
            }
            catch { spaceRefused = true; throw; }
            await _catalog.SavePendingMigrationAsync(intent, cancellationToken).ConfigureAwait(false);
            saved = true;
            JointStage?.Invoke("IntentDurable");
            JointStage?.Invoke("LocksReserved");
            return new SqlDeferredMigration(this, schema, script.Steps, intent, locks,
                plan.Touched.Select(table => table.ObjectId).DefaultIfEmpty(0UL).ToHashSet(), cancellationToken)
                { JointPlan = plan, JointFailureProgress = failed, JointMetadata = metadata,
                    SpaceAdmitted = !deferred, SpaceAvailableBeforeIntent = availableBeforeIntent };
        }
        catch (Exception failure)
        {
            _database.ReportProvisioning(!deferredBuild && IsImperativeInterruption(failure) ? null : failed);
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
            var coded = JointFailure(cause, schema, script.Steps);
            EnsureFailureTrace(schema);
            TraceStage("JointMigration", SqlProvisioningCodes.JointFailed, stepCount: script.Steps.Count);
            TraceFailure(coded);
            if (ReferenceEquals(cause, failure) && (failure is OperationCanceledException or OutOfMemoryException)) { throw; }
            throw coded;
        }
    }

    private async ValueTask DiscardMismatchedIntentAsync(SqlCompiledSchema schema, int stepCount, CancellationToken cancellationToken)
    {
        await _catalog.DiscardJointIntentAsync(cancellationToken).ConfigureAwait(false);
        TraceStage("JointMigration", SqlProvisioningCodes.JointFailed, stepCount: stepCount);
        TraceFailure(CodedMigrationFailure(SqlProvisioningCodes.JointFailed, ErrorMessages.JointIntentDiscarded));
        TraceOutcome("Restarted", stepCount);
        EndTrace();
        BeginTrace(schema, "Replan");
        TraceStage("JointMigration", SqlProvisioningCodes.JointFailed, stepCount: stepCount);
    }

    // Open runs before Build's apply, without a live migration reservation. Reuse the
    // same discard commit and trace vocabulary for an old/different/no-target Build.
    internal async ValueTask DiscardUnpublishedIntentAsync(SqlCompiledSchema? target, CancellationToken cancellationToken)
    {
        if (_catalog.PendingMigration is null) { return; }
        // A discarded target is untrusted and need not be parsed to keep the stored
        // schema available. A declared target already passed Build's schema validation.
        if (target is not null) { BeginTrace(target, "Resume"); }
        try
        {
            if (target is null) { await _catalog.DiscardJointIntentAsync(cancellationToken).ConfigureAwait(false); }
            else { await DiscardMismatchedIntentAsync(target, 0, cancellationToken).ConfigureAwait(false); }
            TraceOutcome("Discarded", 0);
            if (target is not null)
            { _database.ReportProvisioning(new DatabaseProvisioningProgress("JointMigration", _database.Name.ToString(), 1, 1,
                false, failureCode: SqlProvisioningCodes.JointFailed)); }
        }
        finally { EndTrace(); }
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
            PublishProgress(migration.Intent.Steps[0], migration.ProvisioningStepCount, running: true, 0);
            var statement = new SqlStatementContext(migration.Locks, _database.Coordinator, migration.Schema.Name,
                _database.Name, provisioningProgress: (processed, total) =>
                {
                    PublishProgress(migration.Intent.Steps[0], migration.ProvisioningStepCount, true, processed, total);
                    TraceDeferredProgress(migration, migration.Intent.Steps[0], processed, total);
                    DeferredProgress?.Invoke(processed, total);
                }, checkpointCopy: true);
            JointMetrics = statement.Metrics;
            if (migration.Steps.Count > 0) { BeforeDeferredStep?.Invoke(migration.Steps[0], cancellationToken); }
            if (!migration.SpaceAdmitted)
            { AdmitJointSpace(migration.JointPlan, migration.Intent, cancellationToken, migration.SpaceAvailableBeforeIntent); }
            bool sparseGrowth = _database.Engine.SparseDataGrowth(_database.Name);
            bool markerOnly = migration.JointPlan.Tables.Length == 0 && migration.JointPlan.Dropped.Length == 0;
            statement.ConfigureCopySpace((touches, allocations, unflushed, prepared) =>
            {
                if (markerOnly) { CheckJointSpace(JointSpaceDiscardBytes); return; }
                JointSpaceDiscardBytes = JointEngineReserve(prepared);
                long reused = Math.Min(allocations, _database.DataStorage.PageManager.FreePageCount);
                // Eviction and engine checkpoints may have flushed earlier copy
                // allocations. The unwritten backlog cannot exceed the data pool.
                long pending = sparseGrowth ? Math.Min(checked(unflushed * Page.Size), _database.Engine.EngineOptions.BufferPoolCapacity) : 0;
                CheckJointSpace(checked(JointBracketBytes(touches, allocations - reused) + pending + JointSpaceDiscardBytes));
            }, unflushed => CheckJointSpace(markerOnly ? JointSpaceDiscardBytes : checked(JointSpacePublicationBytes + JointSpaceDiscardBytes +
                (sparseGrowth ? Math.Min(unflushed, _database.Engine.EngineOptions.BufferPoolCapacity) : 0))), _database.TableReclamation.CaptureAllocationCount,
                JointCopyCheckpointThreshold);
            var executor = new SqlPlanExecutor(_database.DataStorage, _catalog, _database.IndexManager, _database.Definitions);
            await executor.ExecuteJointMigrationAsync(migration.JointPlan, migration.Schema, statement,
                phase =>
                {
                    if (phase == "Publishing")
                    { foreach (var step in migration.Steps) { BeforeJointPublication?.Invoke(step); } }
                    JointStage?.Invoke(phase);
                }, cancellationToken).ConfigureAwait(false);
            _formatCheckedStateDocument = migration.Schema.CanonicalDocument;
            EndDeferredTrace(migration, 0, "Applied");
            _database.ReportProvisioning(null);
        }
        catch (Exception) when (JointMetrics?.SelfCommits > 0)
        {
            // The complete new schema is already durable. Deliver that success even
            // if late cancellation or an observer throws after publication.
            EndDeferredTrace(migration, 0, "Applied");
            _database.ReportProvisioning(null);
        }
        catch (Exception failure)
        {
            if (IsJointUnconfirmed(failure))
            {
                // Publication may already be durable. Leave trees and intent to recovery;
                // neither discard nor reclamation may decide the commit's outcome here.
                _database.ReportProvisioning(new DatabaseProvisioningProgress("JointMigration", migration.Schema.Name,
                    1, migration.ProvisioningStepCount, false, failureCode: SqlProvisioningCodes.JointUnconfirmed));
                var unconfirmed = CodedMigrationFailure(SqlProvisioningCodes.JointUnconfirmed,
                    ErrorMessages.JointPublicationUnconfirmed, failure);
                if (!migration.ReleaseApplyGate)
                { EnsureFailureTrace(migration.Schema); TraceStage("JointMigration", SqlProvisioningCodes.JointUnconfirmed, stepCount: migration.ProvisioningStepCount); }
                FailDeferredTrace(migration, 0, unconfirmed);
                EndDeferredTrace(migration, 0, "Unconfirmed");
                throw unconfirmed;
            }
            // No compensation: published objects and committed rows were never changed.
            // Remove the intent; retry/next Build replans from the old applied marker.
            _database.ReportProvisioning(!migration.ReleaseApplyGate && IsImperativeInterruption(failure) ? null : failed);
            Exception cause = failure;
            try { await _catalog.DiscardJointIntentAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { cause = new AggregateException(failure, cleanup); }
            var coded = JointFailure(cause, migration.Schema, migration.Steps);
            if (!migration.ReleaseApplyGate)
            { EnsureFailureTrace(migration.Schema); TraceStage("JointMigration", SqlProvisioningCodes.JointFailed, stepCount: migration.ProvisioningStepCount); }
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

    private static bool IsImperativeInterruption(Exception failure)
        => failure is OperationCanceledException or TransactionLockTimeoutException;

    private SqlSchemaMigrationException JointFailure(Exception failure, SqlCompiledSchema schema,
        System.Collections.Generic.IReadOnlyList<SqlMigrationScriptStep> steps)
    {
        string work = steps.Count == 0 ? "Joint/Schema" : string.Join("; ", steps.Select(step => Describe(step.Operation)));
        string message = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.JointMigrationFailed,
            Describe(), schema.Name, work, failure.Message);
        return CodedMigrationFailure(SqlProvisioningCodes.JointFailed, message, failure);
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
