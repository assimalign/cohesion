using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    private SqlDeferredMigration? _deferred;

    // Instance-local test hooks: the same real worker, storage and locks as production.
    internal Action<SqlMigrationScriptStep, CancellationToken>? BeforeDeferredStep { get; set; }
    internal Action<long, long?>? DeferredProgress { get; set; }

    internal SqlDeferredMigration? Deferred => Volatile.Read(ref _deferred);

    private SqlSchemaMigrationResult? TryGetDeferredResult(SqlCompiledSchema schema)
    {
        if (Deferred is not { } pending)
        {
            return null;
        }
        if (!string.Equals(schema.Hash, pending.Schema.Hash, StringComparison.Ordinal))
        {
            throw new SqlSchemaMigrationException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlSchemaProvisionerApplyAsyncFailure, SqlProvisioningCodes.PolicyRefused, Describe()));
        }
        return new SqlSchemaMigrationResult(null, schema.Hash, 0, WasAlreadyApplied: false);
    }

    private List<SqlMigrationScriptStep> FindDeferredSteps(SqlMigrationScript script)
    {
        var deferred = new List<SqlMigrationScriptStep>();
        foreach (var step in script.Steps)
        {
            var operation = step.Operation;
            // Columns are appended physically. Once an addition is deferred, later
            // additions to that table must retain declaration order behind it.
            if (operation.Kind == SqlSchemaMigrationOperationKind.AddColumn && deferred.Any(prior =>
                prior.Operation.Kind == SqlSchemaMigrationOperationKind.AddColumn &&
                string.Equals(prior.Operation.ParentName, operation.ParentName, StringComparison.OrdinalIgnoreCase)))
            {
                deferred.Add(step);
                continue;
            }
            bool scales = operation.Kind is SqlSchemaMigrationOperationKind.AddIndex or SqlSchemaMigrationOperationKind.AddConstraint
                || operation.Kind == SqlSchemaMigrationOperationKind.AlterColumn && RequiresValueValidation(operation)
                || operation.Kind == SqlSchemaMigrationOperationKind.AddColumn &&
                    (operation.Column is { IsNullable: false } || operation.Column?.CustomType is { } domain &&
                        _database.Engine.Types.Find(domain)?.Check is not null);
            if (scales && operation.ParentName is { } tableName &&
                _catalog.TryGetTable(SqlPlanner.DefaultSchema, tableName, out var table))
            {
                // A scoped iterator snapshots every owned page before its first MoveNext.
                // Probe allocation metadata instead: retained versions still mean work,
                // and the final physical slot's deletion releases its page at commit.
                if (_database.DataStorage.HasOwnerPages(table.ObjectId))
                {
                    deferred.Add(step);
                }
            }

            // An empty child's FK cannot bind before its parent's new unique index exists.
            if (!deferred.Contains(step) && operation.Constraint?.ReferencedObject is { } parent &&
                deferred.Any(prior => string.Equals(prior.Operation.ParentName, parent, StringComparison.OrdinalIgnoreCase)))
            {
                deferred.Add(step);
            }
        }
        return deferred;
    }

    private bool RequiresValueValidation(SqlSchemaMigrationOperation operation)
    {
        if (operation.Column is not { } column || operation.PreviousColumn is not { } previous)
        {
            return false;
        }
        var type = ResolveColumnType(column, _database.Engine.Types, out _);
        var previousType = ResolveColumnType(previous, _database.Engine.Types, out _);
        return previous.IsNullable && !column.IsNullable ||
            type.Type == DatabaseType.Character && type.MaxLength != previousType.MaxLength ||
            type.MaxLength is { } length && (previousType.MaxLength is null || length < previousType.MaxLength) ||
            type.Precision is { } precision && (previousType.Precision is null || precision < previousType.Precision);
    }

    private SqlCatalogPendingMigration CreateDeferredIntent(SqlCompiledSchema schema, List<SqlMigrationScriptStep> steps,
        int stepCount)
    {
        var previous = _catalog.PendingMigration;
        bool resuming = previous is not null && string.Equals(previous.ContentHash, schema.Hash, StringComparison.Ordinal);
        var positions = new List<SqlCatalogMigrationStep>(steps.Count);
        int next = stepCount - steps.Count + 1;
        foreach (var step in steps)
        {
            var operation = step.Operation;
            var recorded = resuming ? previous!.Steps.FirstOrDefault(value =>
                value.Kind == operation.Kind.ToString() &&
                string.Equals(value.Table, operation.ParentName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(value.ObjectName, operation.ObjectName, StringComparison.OrdinalIgnoreCase)) : null;
            positions.Add(recorded ?? new SqlCatalogMigrationStep(operation.Kind.ToString(), operation.ParentName!, operation.ObjectName, next));
            next++;
        }
        var ordered = steps.Zip(positions).OrderBy(pair => pair.Second.Position).ToArray();
        return new SqlCatalogPendingMigration(schema.Hash, schema.CanonicalDocument,
            ordered.Select(pair => pair.Second).ToArray(), resuming ? previous!.StepCount : stepCount);
    }

    private async ValueTask PrepareDeferredAsync(SqlCompiledSchema schema, List<SqlMigrationScriptStep> steps,
        int stepCount, CancellationToken cancellationToken)
    {
        var intent = CreateDeferredIntent(schema, steps, stepCount);
        steps = intent.Steps.Select(position => steps.Single(step =>
            step.Operation.Kind.ToString() == position.Kind &&
            string.Equals(step.Operation.ParentName, position.Table, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(step.Operation.ObjectName, position.ObjectName, StringComparison.OrdinalIgnoreCase))).ToList();

        TransactionContext locks = await _database.Coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken).ConfigureAwait(false);
        try
        {
            var tables = new SortedSet<ulong>();
            foreach (var step in steps)
            {
                if (_catalog.TryGetTable(SqlPlanner.DefaultSchema, step.Operation.ParentName!, out var table))
                {
                    tables.Add(table.ObjectId);
                }
                if (step.Operation.Constraint?.ReferencedObject is { } parent &&
                    _catalog.TryGetTable(SqlPlanner.DefaultSchema, parent, out var referenced))
                {
                    tables.Add(referenced.ObjectId);
                }
            }
            foreach (ulong table in tables)
            {
                await _database.Coordinator.LockManager.AcquireAsync(locks.Sequence, LockResource.Object(table),
                    LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
            }
            var migration = new SqlDeferredMigration(this, schema, steps, intent, locks, tables);
            PublishProgress(intent.Steps[0], intent.StepCount, running: false);
            Volatile.Write(ref _deferred, migration);
        }
        catch
        {
            if (!_database.IsOffline)
            {
                await _database.Coordinator.RollbackAsync(locks, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    internal async Task RunDeferredAsync(SqlDeferredMigration migration, CancellationToken cancellationToken)
    {
        int index = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var session = _database.CreateSchemaSession(migration.Schema.Name, cancellationToken, migration.Locks.Sequence);
            for (; index < migration.Steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SqlCatalogMigrationStep position = migration.Intent.Steps[index];
                BeginDeferredTrace(migration, index);
                PublishProgress(position, migration.Intent.StepCount, running: true, 0);
                session.ProvisioningProgress = (processed, total) =>
                {
                    PublishProgress(position, migration.Intent.StepCount, running: true, processed, total);
                    TraceDeferredProgress(migration, position, processed, total);
                    DeferredProgress?.Invoke(processed, total);
                };
                var step = migration.Steps[index];
                BeforeDeferredStep?.Invoke(step, cancellationToken);
                await session.ExecuteAsync(step.Request, cancellationToken).ConfigureAwait(false);
                StepApplied?.Invoke(step);
                EndDeferredTrace(migration, index, "Applied");
            }

            // The table locks protect this migration. Unrelated DDL may advance the
            // catalog generation while we compare; retry its optimistic generation check.
            // Do not wait for the DDL interlock while owning table locks.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong generation = _catalog.DdlGeneration;
                if (FirstCatalogDifference(migration.Schema) is { } difference)
                {
                    throw new SqlSchemaMigrationException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProvisionerRunDeferredAsyncFailure, difference));
                }
                try
                {
                    await _catalog.SaveSchemaStateAsync(new SqlCatalogSchemaState(migration.Schema.Hash,
                        migration.Schema.CanonicalDocument, generation), cancellationToken).ConfigureAwait(false);
                    _formatCheckedStateDocument = migration.Schema.CanonicalDocument;
                    break;
                }
                catch (SqlCatalogException) when (!_database.IsOffline && _catalog.DdlGeneration != generation)
                {
                    // A completed unrelated DDL operation invalidated the comparison.
                }
            }
            _database.ReportProvisioning(null);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            if (!cancellationToken.IsCancellationRequested && !_database.IsClosed)
            {
                FailDeferredTrace(migration, index, failure);
                EndDeferredTrace(migration, index, "Error");
                var position = migration.Intent.Steps[Math.Min(index, migration.Steps.Count - 1)];
                var coded = new SqlSchemaMigrationException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProvisionerRunDeferredAsyncFailure2, SqlProvisioningCodes.StepFailed, Describe(), position.Position, migration.Intent.StepCount, position.Kind, position.Table, position.ObjectName), failure);
                var progress = _database.Provisioning;
                _database.ReportProvisioning(new DatabaseProvisioningProgress(position.Kind, position.Table,
                    position.Position, migration.Intent.StepCount, false, progress?.RowsProcessed, progress?.TotalRows,
                    SqlProvisioningCodes.StepFailed));
                if (!_database.IsOffline)
                {
                    _database.DataStorage.TakeOffline(StorageOfflineCause.ProvisioningFailure, coded.Message, coded);
                }
            }
        }
        finally
        {
            try
            {
                if (migration.Locks.State == TransactionState.Active && !_database.IsOffline)
                {
                    await _database.Coordinator.RollbackAsync(migration.Locks, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                EndDeferredTrace(migration, index, "Cancelled");
                Volatile.Write(ref _deferred, null);
                _applyGate.Release();
            }
        }
    }

    private void PublishProgress(SqlCatalogMigrationStep step, int total, bool running,
        long? processed = null, long? rows = null)
    {
        _database.ReportProvisioning(new DatabaseProvisioningProgress(step.Kind, step.Table,
            step.Position, total, running, processed, rows));
    }

    internal async ValueTask StopDeferredAsync()
    {
        await StopRewriteAsync().ConfigureAwait(false);
        if (Deferred is { } pending)
        {
            await pending.StopAsync().ConfigureAwait(false);
        }
    }
}
