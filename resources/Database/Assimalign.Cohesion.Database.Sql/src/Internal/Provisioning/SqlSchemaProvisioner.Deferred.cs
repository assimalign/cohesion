using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    private SqlDeferredMigration? _deferred;

    // Instance-local test hooks: the same real worker, storage and locks as production.
    internal Action<SqlMigrationScriptStep, CancellationToken>? BeforeDeferredStep { get; set; }
    internal Action<long, long?>? DeferredProgress { get; set; }
    internal Action<Exception>? DeferredFailed { get; set; }

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

    internal async Task RunDeferredAsync(SqlDeferredMigration migration, CancellationToken cancellationToken, bool propagateJointFailure = false)
    {
        try { await RunJointMigrationAsync(migration, cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (!propagateJointFailure)
        {
            // The joint boundary records every failure, including OOM and cancellation.
            // The engine pump must continue serving the other databases.
            DeferredFailed?.Invoke(failure);
        }
        finally
        {
            try { await RollbackRewriteAsync(migration.Locks).ConfigureAwait(false); }
            finally
            {
                migration.JointMetadata?.Dispose();
                Volatile.Write(ref _deferred, null);
                // Inline execution retains admission through preparation and trace
                // cleanup in ApplyAsync. Only a handed-off worker releases it here.
                if (migration.ReleaseApplyGate) { _applyGate.Release(); }
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
        await StopJointPreparationAsync().ConfigureAwait(false);
        if (Deferred is { } pending)
        {
            await pending.StopAsync().ConfigureAwait(false);
        }
    }
}
