using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async Task<DatabaseResult> ExecuteAlterColumnAsync(SqlAlterColumnPlan plan, SqlStatementContext statement,
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
            throw new DatabaseException($"ALTER COLUMN '{column.Name}' requiring a storage-type, domain, collation, scale or default rewrite is not supported. Use an explicit table-copy migration; in-place rewrites are tracked by #1447.");
        }
        if (column.IsNullable && plan.Table.PrimaryKeyColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
        {
            throw new DatabaseException("A primary-key column cannot become nullable.");
        }
        if (column.DefaultLiteral is not null)
        {
            BindDefaultExpression(column, $"DEFAULT of column '{column.Name}' on '{plan.Table.Name}'", _definitions.Functions, _catalog.DefaultCollation, cancellationToken, declaring: true);
        }

        bool validatesValues = previous.IsNullable && !column.IsNullable ||
            column.Type.Type == DatabaseType.Character && column.Type.MaxLength != previous.Type.MaxLength ||
            column.Type.MaxLength is { } length && (previous.Type.MaxLength is null || length < previous.Type.MaxLength) ||
            column.Type.Precision is { } precision && (previous.Type.Precision is null || precision < previous.Type.Precision);
        if (validatesValues)
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
                // This is physical equality: even SQL-equal CHAR values need a rewrite if
                // their padding differs. New wrappers around the same padded string do not.
                if (!Equals(SqlCharacter.Output(value), SqlCharacter.Output(converted)))
                {
                    throw new DatabaseException($"ALTER COLUMN '{column.Name}' would rewrite existing values; only validation-only changes are supported. Use an explicit table-copy migration; in-place rewrites are tracked by #1447.");
                }
                statement.ProvisioningProgress?.Invoke(++processed, total);
            }
        }

        var columns = plan.Table.Columns.ToArray();
        columns[ordinal] = column;
        var replacement = new SqlCatalogTable(plan.Table.ObjectId, plan.Table.Schema, plan.Table.Name, columns,
            plan.Table.PrimaryKeyColumns, plan.Table.Owner, plan.Table.OwningSchema, plan.Table.Constraints, plan.Table.DroppedColumnOrdinals);
        _definitions.Get(replacement, cancellationToken);
        await SelfCommitAsync(statement, (statement.ScriptReceipt is null ? _catalog.PublishTableAsync(replacement, [], PublishedRegistrations([]),
            replaceExisting: true, cancellationToken) : _catalog.PublishTableAsync(replacement, [], PublishedRegistrations([]),
            replaceExisting: true, statement.ScriptReceipt, cancellationToken))).ConfigureAwait(false);
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }
}
