using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async Task<QueryResult> ExecuteAlterColumnAsync(SqlAlterColumnPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        await AcquireObjectLockAsync(statement, plan.Table.Schema, plan.Table.Name, "ALTER TABLE ALTER COLUMN", cancellationToken).ConfigureAwait(false);
        EnsureCurrentDefinition(plan.Table);
        int ordinal = FindColumnOrdinal(plan.Table, plan.Column.Name);
        SqlCatalogColumn previous = plan.Table.Columns[ordinal];
        var column = plan.Column;
        if (previous.Type.Type != column.Type.Type || previous.Type.Scale != column.Type.Scale ||
            !string.Equals(previous.DomainName, column.DomainName, StringComparison.OrdinalIgnoreCase) ||
            (previous.Collation ?? _catalog.DefaultCollation).Id != (column.Collation ?? _catalog.DefaultCollation).Id ||
            !string.Equals(SqlSchemaProvisioner.CanonicalDefault(previous.DefaultLiteral),
                SqlSchemaProvisioner.CanonicalDefault(column.DefaultLiteral), StringComparison.Ordinal))
        {
            throw new DatabaseException("ALTER COLUMN requiring a storage-type, domain, collation, scale or default rewrite is not supported.");
        }
        if (column.IsNullable && plan.Table.PrimaryKeyColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
        {
            throw new DatabaseException("A primary-key column cannot become nullable.");
        }
        if (column.DefaultLiteral is not null)
        {
            BindDefaultExpression(column, $"DEFAULT of column '{column.Name}' on '{plan.Table.Name}'", _definitions.Functions, _catalog.DefaultCollation, declaring: true);
        }

        bool tightens = previous.IsNullable && !column.IsNullable ||
            column.Type.MaxLength is { } length && (previous.Type.MaxLength is null || length < previous.Type.MaxLength) ||
            column.Type.Precision is { } precision && (previous.Type.Precision is null || precision < previous.Type.Precision);
        if (tightens)
        {
            var snapshot = ConstraintCurrentSnapshot(statement);
            long total = statement.ProvisioningProgress is null ? 0 : Scan(plan.Table, statement, cancellationToken, snapshot).LongCount();
            statement.ProvisioningProgress?.Invoke(0, total);
            long processed = 0;
            foreach (var row in Scan(plan.Table, statement, cancellationToken, snapshot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? value = row.Values[ordinal];
                object? converted = CoerceForColumn(value, column);
                if (!Equals(value, converted))
                {
                    throw new DatabaseException($"ALTER COLUMN '{column.Name}' would rewrite existing values; only validation-only changes are supported.");
                }
                statement.ProvisioningProgress?.Invoke(++processed, total);
            }
        }

        var columns = plan.Table.Columns.ToArray();
        columns[ordinal] = column;
        var replacement = new SqlCatalogTable(plan.Table.ObjectId, plan.Table.Schema, plan.Table.Name, columns,
            plan.Table.PrimaryKeyColumns, plan.Table.Owner, plan.Table.OwningSchema, plan.Table.Constraints, plan.Table.DroppedColumnOrdinals);
        _definitions.Get(replacement);
        await SelfCommitAsync(statement, _catalog.PublishTableAsync(replacement, [], PublishedRegistrations([]),
            replaceExisting: true, cancellationToken, scriptReceipt: statement.ScriptReceipt)).ConfigureAwait(false);
        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }
}
