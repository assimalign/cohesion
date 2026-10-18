using System;
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
    private async Task<DatabaseResult> ExecuteCreateSchemaAsync(SqlCreateSchemaPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (SqlNamespaces.Find(_catalog, plan.Name) is not null)
        {
            if (plan.IfNotExists && statement.ScriptReceipt is null) { return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0); }
            throw new SqlPlanningException("COHSQLE029", string.Format(CultureInfo.CurrentCulture, ErrorMessages.NamespaceExists, plan.Name));
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
            throw new SqlPlanningException("COHSQLE031", ErrorMessages.DefaultNamespaceProtected);
        }
        var schema = SqlNamespaces.Find(_catalog, plan.Name);
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
        var snapshot = _catalog.CaptureSnapshot();
        var contained = snapshot.Tables.Where(table => table.Schema.Equals(schema.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!plan.Cascade && contained.Length != 0)
        {
            throw new SqlPlanningException("COHSQLE030", string.Format(CultureInfo.CurrentCulture, ErrorMessages.NamespaceDependentObjects, schema.Name));
        }
        var identities = contained.Select(table => table.ObjectId).ToHashSet();
        bool ReferencesContained(SqlCatalogConstraint constraint) => constraint.Kind == SqlCatalogConstraintKind.Reference &&
            contained.Any(table => table.Schema.Equals(constraint.ReferencedSchema, StringComparison.OrdinalIgnoreCase) &&
                table.Name.Equals(constraint.ReferencedTable, StringComparison.OrdinalIgnoreCase));
        // Lock both the retired tables and external children whose FK metadata changes. Schema
        // DDL holds the definition interlock, so the complete set stays stable while locks wait.
        var affected = snapshot.Tables.Where(table => identities.Contains(table.ObjectId) || table.Constraints.Any(ReferencesContained))
            .OrderBy(table => table.ObjectId).ToArray();
        foreach (var table in affected)
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
            var current = ReadCurrentTable(table);
            EnsureCanChange(current.Owner, current.Name, current.OwningSchema, "DROP SCHEMA", statement);
            EnsureSameIdentity(table, current);
            if (identities.Contains(table.ObjectId))
            {
                foreach (var index in snapshot.GetIndexes(table.ObjectId))
                {
                    EnsureCanChange(index.Owner, index.Name, index.OwningSchema, "DROP SCHEMA", statement);
                }
            }
        }
        await SelfCommitAsync(statement, statement.ScriptReceipt is { } publication
            ? _catalog.DropSchemaAsync(schema.Name, plan.Cascade, publication, cancellationToken)
            : _catalog.DropSchemaAsync(schema.Name, plan.Cascade, cancellationToken)).ConfigureAwait(false);
        foreach (var table in contained)
        {
            foreach (var index in snapshot.GetIndexes(table.ObjectId))
            {
                if (_indexManager.TryGetIndex(table.ObjectId, index.Name, out _))
                {
                    await _indexManager.DropIndexAsync(statement.Transaction, table.ObjectId, index.Name, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }
}
