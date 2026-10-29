using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    internal static Action? SchemaDefinitionLeaseEntered { get; set; }
    private async Task<DatabaseResult> ExecuteCreateSchemaAsync(SqlCreateSchemaPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (SqlNamespaces.Find(_catalog, plan.Name) is not null)
        {
            if (plan.IfNotExists && statement.ScriptReceipt is null) { return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0); }
            throw new SqlPlanningException("CS03029", string.Format(CultureInfo.CurrentCulture, ErrorMessages.NamespaceExists, plan.Name));
        }
        // Declared scripts retain their compiled ownership identity. It does not select the
        // namespace of code-first tables, which continue to be provisioned exclusively in dbo.
        var schema = new SqlCatalogNamespace(plan.Name,
            statement.ProvisioningSchema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema,
            statement.ProvisioningSchema);
        await SelfCommitAsync(statement, statement.ScriptReceipt is { } publication
            ? _catalog.CreateSchemaAsync(schema, publication, cancellationToken)
            : _catalog.CreateSchemaAsync(schema, cancellationToken)).ConfigureAwait(false);
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    private async Task<DatabaseResult> ExecuteDropSchemaAsync(SqlDropSchemaPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (plan.Name.Equals(SqlNamespaces.DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlPlanningException("CS03031", ErrorMessages.DefaultNamespaceProtected);
        }
        var snapshot = _catalog.CaptureSnapshot();
        var schema = snapshot.Namespaces.FirstOrDefault(value => value.Name.Equals(plan.Name, StringComparison.OrdinalIgnoreCase));
        var contents = ObjectsInNamespace(snapshot, plan.Name);
        var sequences = snapshot.Sequences.Where(s => s.Schema.Equals(plan.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.ObjectId).ToArray();
        if (!plan.Cascade && contents.Views.Length != 0)
        { throw new SqlPlanningException("CS03302", string.Format(CultureInfo.CurrentCulture, ErrorMessages.EngineViewDropDependent, plan.Name, contents.Views[0].Schema, contents.Views[0].Name)); }
        if (!plan.Cascade && (contents.Tables.Length != 0 || sequences.Length != 0))
        {
            throw new SqlPlanningException("CS03030", string.Format(CultureInfo.CurrentCulture, ErrorMessages.EngineNamespaceDependentObjects, plan.Name));
        }
        // Definition DDL must never wait for transaction locks under its metadata lease:
        // a deferred migration owns table reservations and waits for that lease between steps.
        foreach (var sequence in sequences)
        {
            BeforeSequenceDdlLock?.Invoke();
            await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                LockResource.Object(sequence.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
            SqlSequenceExecution.CheckGrant(statement, statement.DdlLockOwner);
        }
        foreach (var table in contents.AffectedTables)
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
        }
        SqlSequenceExecution.CheckGrant(statement, statement.DdlLockOwner);
        using var metadata = statement.DdlInterlock is { } interlock
            ? statement.ProvisioningSchema is not null
                ? await interlock.EnterMetadataAsync(definition: true, cancellationToken).ConfigureAwait(false)
                : interlock.EnterMetadata(statement.DatabaseName, definition: true)
            : null;
        SchemaDefinitionLeaseEntered?.Invoke();
        var current = _catalog.CaptureSnapshot();
        var currentSchema = current.Namespaces.FirstOrDefault(value => value.Name.Equals(plan.Name, StringComparison.OrdinalIgnoreCase));
        var currentContents = ObjectsInNamespace(current, plan.Name);
        if (!sequences.SequenceEqual(current.Sequences.Where(s => s.Schema.Equals(plan.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.ObjectId)) || !ReferenceEquals(schema, currentSchema) || !contents.Tables.SequenceEqual(currentContents.Tables) ||
            !contents.AffectedTables.SequenceEqual(currentContents.AffectedTables) || !contents.Indexes.SequenceEqual(currentContents.Indexes) || !contents.Views.SequenceEqual(currentContents.Views))
        {
            // The statement's ordinary cleanup releases its complete lock set. Never publish
            // by name using locks and ownership checks for a previous namespace image.
            throw new SqlPlanningException("CS05013", string.Format(CultureInfo.CurrentCulture, ErrorMessages.NamespaceChanged, plan.Name));
        }
        if (schema is null)
        {
            if (!plan.IfExists) { SqlNamespaces.Require(_catalog, plan.Name); }
            if (statement.ScriptReceipt is { } absence)
            {
                await SelfCommitAsync(statement, _catalog.DropSchemaAsync(plan.Name, plan.Cascade, absence, cancellationToken)).ConfigureAwait(false);
            }
            return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
        }
        EnsureCanChange(schema.Owner, schema.Name, schema.OwningSchema, "DROP SCHEMA", statement);
        foreach (var table in contents.AffectedTables)
        {
            EnsureCanChange(table.Owner, table.Name, table.OwningSchema, "DROP SCHEMA", statement);
        }
        foreach (var view in contents.Views)
        { EnsureCanChange(view.Owner, view.Name, view.OwningSchema, "DROP SCHEMA", statement); }
        foreach (var index in contents.Indexes)
        {
            EnsureCanChange(index.Owner, index.Name, index.OwningSchema, "DROP SCHEMA", statement);
        }
        await SelfCommitAsync(statement, statement.ScriptReceipt is { } publication
            ? _catalog.DropSchemaAsync(schema.Name, plan.Cascade, publication, cancellationToken)
            : _catalog.DropSchemaAsync(schema.Name, plan.Cascade, cancellationToken)).ConfigureAwait(false);
        List<Exception>? failures = null;
        foreach (var child in contents.AffectedTables.Where(table => !table.Schema.Equals(schema.Name, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                _catalog.TryGetTable(child.Schema, child.Name, out var published);
                _definitions.Adopt(published, child, CancellationToken.None);
            }
            catch (Exception failure)
            {
                // Publication committed: finish every other binding and directory removal.
                (failures ??= []).Add(failure);
            }
        }
        await RemoveDroppedIndexesAsync(statement, contents.Indexes, failures, CancellationToken.None).ConfigureAwait(false);
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    // SQL's one namespace-contents enumeration drives RESTRICT, ordered locks, ownership
    // and the CASCADE drop set. Persisted views participate in this enumeration and its revalidation.
    // The catalog independently guards the same invariant inside its publication transaction.
    private static NamespaceContents ObjectsInNamespace(SqlCatalogSnapshot snapshot, string name)
    {
        var tables = new List<SqlCatalogTable>();
        var affected = new List<SqlCatalogTable>();
        var indexes = new List<SqlCatalogIndex>();
        var sequenceDependencies = snapshot.Sequences.Where(sequence => sequence.Schema.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(sequence => SqlSequenceDefault.Dependency(sequence.ObjectId)).ToHashSet(StringComparer.Ordinal);
        foreach (var table in snapshot.Tables)
        {
            if (table.Schema.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                tables.Add(table);
                affected.Add(table);
                indexes.AddRange(snapshot.GetIndexes(table.ObjectId));
            }
            else if (table.Constraints.Any(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference &&
                name.Equals(constraint.ReferencedSchema, StringComparison.OrdinalIgnoreCase)) ||
                table.Columns.Any(column => column.DefaultFunctionCalls.Any(sequenceDependencies.Contains)))
            {
                affected.Add(table);
            }
        }
        var views = DependentViews(snapshot, snapshot.Views.Where(view => view.Schema.Equals(name, StringComparison.OrdinalIgnoreCase)), tables.Select(table => table.ObjectId));
        return new(tables.ToArray(), affected.OrderBy(table => table.ObjectId).ToArray(), indexes.ToArray(), views);
    }

    private sealed record NamespaceContents(SqlCatalogTable[] Tables, SqlCatalogTable[] AffectedTables, SqlCatalogIndex[] Indexes, SqlCatalogView[] Views);
}
