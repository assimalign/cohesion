using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    internal async ValueTask WaitForTablesAsync(SqlQueryRequest request, CancellationToken cancellationToken)
    {
        if (Deferred is not { } migration)
        {
            return;
        }
        var tables = new SortedSet<ulong>();
        foreach (var reference in References(request.Statement.SqlExpression))
        {
            if (_catalog.TryGetTable(reference.SchemaName ?? SqlPlanner.DefaultSchema, reference.TableName, out var table)
                && migration.Tables.Contains(table.ObjectId))
            {
                tables.Add(table.ObjectId);
            }
        }
        if (tables.Count == 0)
        {
            return;
        }

        // Wait before binding a plan or capturing the auto-commit snapshot. Readers
        // ordinarily need no table lock, but must not bind the old schema here.
        var waiter = await _database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (ulong table in tables)
            {
                await _database.Coordinator.LockManager.AcquireAsync(waiter.Sequence, LockResource.Object(table),
                    LockMode.IntentShared, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!_database.IsOffline)
            {
                await _database.Coordinator.RollbackAsync(waiter, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<SqlTableReference> References(SqlQueryExpression query)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        IReadOnlyList<SqlCommonTableExpression> commonTables = query switch
        {
            SqlQueryBodyExpression select => select.CommonTables,
            SqlInsertExpression insert => insert.CommonTables,
            SqlUpdateExpression update => update.CommonTables,
            SqlDeleteExpression delete => delete.CommonTables,
            _ => [],
        };
        foreach (var definition in commonTables)
        {
            foreach (var table in References(definition.Body)) { yield return table; }
        }
        switch (query)
        {
            case SqlExplainExpression explain:
                foreach (var table in References(explain.Statement))
                {
                    yield return table;
                }
                break;
            case SqlSetOperationExpression set:
                foreach (var table in References(set.Left)) { yield return table; }
                foreach (var table in References(set.Right)) { yield return table; }
                foreach (var expression in set.OrderBy.Select(order => order.Expression).Append(set.Limit).Append(set.Offset))
                {
                    foreach (var table in References(expression)) { yield return table; }
                }
                break;
            case SqlSelectExpression select:
                foreach (var item in select.From)
                {
                    foreach (var table in References(item.Table)) { yield return table; }
                }
                foreach (var join in select.From.SelectMany(item => item.Joins))
                {
                    foreach (var table in References(join.Table)) { yield return table; }
                }
                var expressions = select.Columns.Select(value => value.Expression)
                    .Concat(select.From.SelectMany(item => item.Joins).Select(value => value.Condition))
                    .Concat(select.GroupBy).Concat(select.OrderBy.Select(value => value.Expression))
                    .Append(select.Where).Append(select.Having).Append(select.Limit).Append(select.Offset);
                foreach (var expression in expressions)
                {
                    foreach (var table in References(expression))
                    {
                        yield return table;
                    }
                }
                break;
            case SqlInsertExpression insert:
                yield return insert.Table;
                if (insert.SelectSource is { } source)
                {
                    foreach (var table in References(source))
                    {
                        yield return table;
                    }
                }
                if (insert.Values is { } values)
                {
                    foreach (var row in values)
                    {
                        foreach (var expression in row)
                        {
                            foreach (var table in References(expression))
                            {
                                yield return table;
                            }
                        }
                    }
                }
                break;
            case SqlUpdateExpression update:
                yield return update.Table;
                foreach (var expression in update.Assignments.Select(value => value.Value).Append(update.Where))
                {
                    foreach (var table in References(expression))
                    {
                        yield return table;
                    }
                }
                break;
            case SqlDeleteExpression delete:
                yield return delete.Table;
                foreach (var table in References(delete.Where))
                {
                    yield return table;
                }
                break;
            case SqlAlterTableExpression alter:
                yield return alter.Table;
                IEnumerable<SqlConstraintDefinition> constraints = alter.Action switch
                {
                    SqlAlterAddConstraintAction add => [add.Constraint],
                    SqlAlterAddColumnAction add => add.Column.Constraints,
                    _ => [],
                };
                foreach (var constraint in constraints)
                {
                    if (constraint.ReferencedTable is { } parent)
                    {
                        yield return parent;
                    }
                }
                break;
            case SqlCreateTableExpression create:
                foreach (var constraint in create.Constraints)
                {
                    if (constraint.ReferencedTable is { } parent)
                    {
                        yield return parent;
                    }
                }
                break;
            case SqlCreateIndexExpression create:
                yield return create.Table;
                break;
            case SqlDropIndexExpression drop:
                yield return drop.Table;
                break;
            case SqlDropTableExpression drop:
                yield return drop.Table;
                break;
        }
    }

    private static IEnumerable<SqlTableReference> References(SqlRelationReference relation)
    {
        if (relation is SqlTableReference table) { yield return table; }
        else if (relation is SqlDerivedTableReference derived)
        {
            foreach (var nested in References(derived.Query)) { yield return nested; }
        }
    }

    private static IEnumerable<SqlTableReference> References(SqlExpression? expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        SqlQueryBodyExpression? query = expression switch
        {
            SqlSubqueryExpression scalar => scalar.Select,
            SqlExistsExpression exists => exists.Subquery,
            SqlInExpression member => member.Subquery,
            _ => null,
        };
        if (query is not null)
        {
            foreach (var table in References(query))
            {
                yield return table;
            }
        }
        if (expression is not null)
        {
            foreach (var child in SqlPlanner.Children(expression))
            {
                foreach (var table in References(child))
                {
                    yield return table;
                }
            }
        }
    }
}
