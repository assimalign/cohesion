using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async Task<DatabaseResult> ExecuteAlterColumnTypeAsync(SqlAlterColumnPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var column = plan.Column;
        int ordinal = FindColumnOrdinal(plan.Table, column.Name);
        EnsureNoViewReads(plan.Table, column.Name, rename: true);
        // PostgreSQL parse_utilcmd.c:3700-3727 alters the identity sequence's type;
        // sequence.c:1382-1389 excludes domains as well as non-integer targets.
        if (column.Identity is not null && (column.DomainName is not null || column.Type.Type is not
            (DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64)))
        {
            throw SqlPlanningException.IdentityViolation(column.Name, ErrorMessages.IdentityTypeChangeForbidden);
        }
        if (column.Identity is { } identity)
        {
            var (minimum, maximum) = SqlPlanner.IdentityTypeBounds(column.Type.Type, identity.Increment);
            if (identity.Start < minimum || identity.Start > maximum)
            { throw SqlPlanningException.IdentityViolation(column.Name, ErrorMessages.IdentityStartOutOfRange); }
        }
        if (column.IsNullable && plan.Table.PrimaryKeyColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
        {
            throw new SqlExecutionException(SqlExecutionException.InvalidTableCode, ErrorMessages.PrimaryKeyNullable);
        }
        foreach (var index in _catalog.GetIndexes(plan.Table.ObjectId))
        {
            if (index.PredicateColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
            { throw new SqlPlanningException("CS03083", string.Format(CultureInfo.CurrentCulture, ErrorMessages.PartialPredicateTypeChange, column.Name, index.Name)); }
            if (index.ColumnNames.Contains(column.Name, StringComparer.OrdinalIgnoreCase) &&
                column.Type.Type is DatabaseType.String or DatabaseType.Character &&
                !(column.Collation ?? _catalog.DefaultCollation).IsIndexBacked)
            {
                throw new SqlExecutionException(SqlExecutionException.UnsupportedCode,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.ConstraintCollationUnsupported, column.Name));
            }
        }

        // Lock/latch order for inline TYPE: session schema/metadata admission -> source
        // table X -> adjacent FK tables X (ascending id) -> reserved owner X -> apply
        // gate -> tree/page latches. The lock manager detects cycles with concurrent
        // FK DDL; no page latch or apply gate spans catalog publication. Session cleanup
        // releases every table grant on success, failure, cancellation and disposal.
        var adjacent = new SortedDictionary<ulong, SqlCatalogTable>();
        foreach (var constraint in plan.Table.Constraints.Where(c => c.Kind == SqlCatalogConstraintKind.Reference &&
            c.Columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase)))
        {
            if (_catalog.TryGetTable(constraint.ReferencedSchema!, constraint.ReferencedTable!, out var parent))
            { adjacent.TryAdd(parent.ObjectId, parent); }
        }
        foreach (var (child, constraint) in IncomingReferences(plan.Table))
        {
            if (constraint.ReferencedColumns!.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
            { adjacent.TryAdd(child.ObjectId, child); }
        }
        adjacent.Remove(plan.Table.ObjectId);
        foreach (var table in adjacent.Values)
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
            EnsureCurrentDefinition(table);
        }

        var columns = plan.Table.Columns.ToArray();
        // Every copied row materializes its missing tails; the dense replacement needs
        // no frozen fast-default literals (the retired description retains those).
        for (int i = 0; i < columns.Length; i++)
        {
            var value = i == ordinal ? column : columns[i];
            columns[i] = new SqlCatalogColumn(value.Name, value.Type, value.IsNullable, value.DefaultLiteral,
                value.Collation, value.DomainName, value.DefaultFunctionCalls, value.Identity) { RecastDefault = value.RecastDefault };
        }
        var definition = new SqlCatalogTable(plan.Table.ObjectId, plan.Table.Schema, plan.Table.Name, columns,
            plan.Table.PrimaryKeyColumns, plan.Table.Owner, plan.Table.OwningSchema, plan.Table.Constraints);
        // Rebind CHECKs before entering the cache's persisted-definition error wrapper:
        // a newly incompatible type is a DDL failure, not catalog corruption.
        foreach (var check in _definitions.Get(plan.Table, cancellationToken).Checks.Where(c => c.ColumnOrdinals.Contains(ordinal)))
        {
            var constraint = check.Constraint;
            BindPersistedCheck(SqlPersistedExpression.Load(constraint.CheckExpression!, constraint.Name), definition,
                _catalog.DefaultCollation, _definitions.Functions, constraint.CheckFunctionCalls, cancellationToken, retyping: true, previous: plan.Table);
        }
        var defaults = _definitions.Get(plan.Table, cancellationToken).DefaultValues.ToArray();
        defaults[ordinal] = column.DefaultLiteral is null ? null : BindDefaultExpression(column,
            "ALTER COLUMN DEFAULT", _definitions.Functions, _catalog.DefaultCollation, cancellationToken, declaring: true);
        var bound = _definitions.GetDeclared(definition, defaults, cancellationToken);
        var evaluator = ExecutionEvaluator(null, cancellationToken, transactionTimestamp: statement.TransactionStart);
        if (defaults[ordinal] is SqlBoundConstant constant && bound.DomainAt(ordinal) is { } defaultDomain)
        {
            var defaultRow = new object?[columns.Length];
            defaultRow[ordinal] = constant.Value;
            CheckDomain(definition, defaultDomain, defaultRow, evaluator);
        }
        ValidateRewriteReferenceTypes(definition, adjacent.Values, column.Name);

        await RewriteTableAsync(plan.Table, statement, cancellationToken,
            (_, id) => new SqlCatalogTable(id, definition.Schema, definition.Name, columns,
                definition.PrimaryKeyColumns, definition.Owner, definition.OwningSchema, definition.Constraints),
            values =>
            {
                var converted = (object?[])values.Clone();
                converted[ordinal] = CoerceForColumn(evaluator.Evaluate(plan.Conversion!, values), column);
                CheckDomains(bound, converted, evaluator);
                ValidateRows(definition, [converted], statement, cancellationToken, checksOnly: true);
                return converted;
            }, (replacement, indexes) => ValidateRewriteReferences(replacement, indexes, adjacent.Values, column.Name, statement, cancellationToken),
            definitionChange: true).ConfigureAwait(false);
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    private void ValidateRewriteReferenceTypes(SqlCatalogTable replacement, IEnumerable<SqlCatalogTable> adjacent, string column)
    {
        var tables = adjacent.Append(replacement).ToArray();
        foreach (var child in tables)
        {
            foreach (var constraint in child.Constraints.Where(c => c.Kind == SqlCatalogConstraintKind.Reference))
            {
                var parent = tables.FirstOrDefault(table => string.Equals(table.Schema, constraint.ReferencedSchema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(table.Name, constraint.ReferencedTable, StringComparison.OrdinalIgnoreCase));
                if (parent is null || !ReferenceUsesAlteredColumn(child, constraint, parent, replacement, column)) { continue; }
                for (int i = 0; i < constraint.Columns.Count; i++)
                {
                    var source = child.Columns[FindColumnOrdinal(child, constraint.Columns[i])];
                    var target = parent.Columns[FindColumnOrdinal(parent, constraint.ReferencedColumns![i])];
                    // Match the engine's FK declaration compatibility contract.
                    if (source.Type.Type != target.Type.Type)
                    {
                        throw new SqlExecutionException(SqlExecutionException.DatatypeMismatchCode,
                            string.Format(CultureInfo.CurrentCulture, ErrorMessages.ForeignKeyColumnTypes, constraint.Name));
                    }
                    if (source.Type.Type is DatabaseType.String or DatabaseType.Character &&
                        (source.Collation ?? _catalog.DefaultCollation) != (target.Collation ?? _catalog.DefaultCollation))
                    {
                        throw new SqlExecutionException(SqlExecutionException.CollationMismatchCode,
                            string.Format(CultureInfo.CurrentCulture, ErrorMessages.ForeignKeyColumnCollations, constraint.Name));
                    }
                }
            }
        }
    }

    private void ValidateRewriteReferences(SqlCatalogTable replacement, SqlCatalogIndex[] indexes, IEnumerable<SqlCatalogTable> adjacent, string column,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var tables = adjacent.Append(replacement).ToArray();
        var snapshot = ConstraintCurrentSnapshot(statement);
        foreach (var child in tables)
        {
            foreach (var constraint in child.Constraints.Where(c => c.Kind == SqlCatalogConstraintKind.Reference))
            {
                var parent = tables.FirstOrDefault(table => string.Equals(table.Schema, constraint.ReferencedSchema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(table.Name, constraint.ReferencedTable, StringComparison.OrdinalIgnoreCase));
                if (parent is null || !ReferenceUsesAlteredColumn(child, constraint, parent, replacement, column)) { continue; }
                var reference = BindReference(child, constraint, parent, statement, cancellationToken);
                // The new descriptions are unpublished, but their enforcing trees are
                // complete. Seed the ordinary lookup cache so self/incoming validation
                // uses those trees instead of rescanning the replacement for every key.
                if (parent.ObjectId == replacement.ObjectId) { ConstraintLookup(reference.ParentKey, statement, indexes); }
                foreach (var row in Scan(child, statement, cancellationToken, snapshot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = reference.ChildKey.Read(row.Values);
                    if (key.Any(value => value is null)) { continue; }
                    if (!FindConstraintRows(reference.ParentKey, key, statement, cancellationToken, snapshot).Any())
                    { throw Violation(child, constraint, key); }
                }
            }
        }
    }

    private static bool ReferenceUsesAlteredColumn(SqlCatalogTable child, SqlCatalogConstraint constraint,
        SqlCatalogTable parent, SqlCatalogTable replacement, string column)
        => child.ObjectId == replacement.ObjectId && constraint.Columns.Contains(column, StringComparer.OrdinalIgnoreCase) ||
            parent.ObjectId == replacement.ObjectId && constraint.ReferencedColumns!.Contains(column, StringComparer.OrdinalIgnoreCase);
}
