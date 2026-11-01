using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    internal async ValueTask WaitForTablesAsync(SqlQueryRequest request, CancellationToken cancellationToken,
        TransactionContext? transaction = null)
    {
        if (Deferred is not { } migration)
        {
            return;
        }
        var created = request.Statement.SqlExpression switch
        {
            SqlCreateTableExpression create => create.Table,
            SqlCreateTableAsExpression create => create.Table,
            _ => null,
        };
        if (created is not null && migration.JointPlan is { } joint &&
            joint.Tables.Any(table => table.Previous is null &&
                table.Shape.Schema.Equals(created.SchemaName ?? SqlPlanner.DefaultSchema, System.StringComparison.OrdinalIgnoreCase) &&
                table.Shape.Name.Equals(created.TableName, System.StringComparison.OrdinalIgnoreCase)))
        {
            throw new SqlCatalogException(SqlProvisioningCodes.JointNameReserved,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.JointTableNameReserved, created.TableName));
        }
        var tables = new SortedSet<ulong>();
        string referenceSchema = request.Statement.SqlExpression switch
        {
            SqlCreateTableExpression create => create.Table.SchemaName ?? SqlPlanner.DefaultSchema,
            SqlAlterTableExpression alter => alter.Table.SchemaName ?? SqlPlanner.DefaultSchema,
            _ => SqlPlanner.DefaultSchema,
        };
        foreach (var reference in References(request.Statement.SqlExpression))
        {
            if (_catalog.TryGetTable(reference.SchemaName ?? referenceSchema, reference.TableName, out var table)
                && migration.Tables.Contains(table.ObjectId))
            {
                tables.Add(table.ObjectId);
            }
            if (_catalog.TryGetView(reference.SchemaName ?? referenceSchema, reference.TableName, out var view))
            {
                var directory = _catalog.Views.ToDictionary(value => SqlObjectIdentity.View(value.Schema, value.Name), System.StringComparer.OrdinalIgnoreCase);
                var pending = new Stack<SqlCatalogView>();
                var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                pending.Push(view);
                while (pending.TryPop(out var current))
                {
                    if (!seen.Add(SqlObjectIdentity.View(current.Schema, current.Name))) { continue; }
                    foreach (ulong id in current.TableDependencies) { if (migration.Tables.Contains(id)) { tables.Add(id); } }
                    foreach (string key in current.Dependencies)
                    { if (directory.TryGetValue(key, out var dependency)) { pending.Push(dependency); } }
                }
            }
        }
        if (request.Statement.SqlExpression is SqlDropSchemaExpression drop)
        {
            foreach (var table in _catalog.Tables.Where(table => table.Schema.Equals(drop.Name, System.StringComparison.OrdinalIgnoreCase)))
            {
                if (migration.Tables.Contains(table.ObjectId)) { tables.Add(table.ObjectId); }
            }
        }
        if (tables.Count == 0)
        {
            return;
        }

        // Wait before binding a plan or capturing the auto-commit snapshot. Readers
        // ordinarily need no table lock, but must not bind the old schema here.
        var waiter = transaction ?? await _database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (ulong table in tables)
            {
                await _database.Coordinator.LockManager.AcquireAsync(waiter.Sequence, LockResource.Object(table),
                    LockMode.IntentShared, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TransactionDeadlockException failure) { throw SqlExecutionException.Deadlock(failure); }
        finally
        {
            if (transaction is null && !_database.IsOffline)
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
            SqlMergeExpression merge => merge.CommonTables,
            _ => [],
        };
        foreach (var definition in commonTables)
        {
            foreach (var table in References(definition.Body)) { yield return table; }
        }
        switch (query)
        {
            case SqlCopyToExpression copy:
                yield return copy.Table;
                break;
            case SqlExplainExpression explain:
                foreach (var table in References(explain.Statement))
                {
                    yield return table;
                }
                break;
            case SqlSetOperationExpression set:
                var pending = new Stack<SqlQueryBodyExpression>();
                pending.Push(set);
                while (pending.TryPop(out var body))
                {
                    if (body is SqlSetOperationExpression operation)
                    {
                        pending.Push(operation.Right);
                        pending.Push(operation.Left);
                        if (!ReferenceEquals(operation, set))
                        {
                            foreach (var definition in operation.CommonTables)
                            {
                                foreach (var table in References(definition.Body)) { yield return table; }
                            }
                        }
                        foreach (var expression in operation.OrderBy.Select(order => order.Expression).Append(operation.Limit).Append(operation.Offset))
                        {
                            foreach (var table in References(expression)) { yield return table; }
                        }
                    }
                    else { foreach (var table in References(body)) { yield return table; } }
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
                foreach (var table in SourceReferences(update.From)) { yield return table; }
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
                foreach (var table in SourceReferences(delete.Using)) { yield return table; }
                foreach (var table in References(delete.Where))
                {
                    yield return table;
                }
                break;
            case SqlMergeExpression merge:
                yield return merge.Table;
                foreach (var table in SourceReferences([merge.Source])) { yield return table; }
                foreach (var expression in merge.Actions.SelectMany(action =>
                    action.Assignments.Select(assignment => assignment.Value).Concat(action.Values ?? []).Append(action.Condition))
                    .Concat(merge.Returning?.Select(column => column.Expression) ?? []).Append(merge.Condition))
                {
                    foreach (var table in References(expression)) { yield return table; }
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
            case SqlCreateTableAsExpression createAs:
                foreach (var table in References(createAs.Query)) { yield return table; }
                break;
            case SqlCreateViewExpression createView:
                foreach (var table in References(createView.Query)) { yield return table; }
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
            case SqlTruncateExpression truncate:
                foreach (var target in truncate.Tables) { yield return target.Table; }
                break;
        }
    }

    private static IEnumerable<SqlTableReference> SourceReferences(IReadOnlyList<SqlFromItem> sources)
    {
        foreach (var item in sources)
        {
            foreach (var table in References(item.Table)) { yield return table; }
            foreach (var join in item.Joins)
            {
                foreach (var table in References(join.Table)) { yield return table; }
                foreach (var table in References(join.Condition)) { yield return table; }
            }
        }
    }

    private static IEnumerable<SqlTableReference> References(SqlRelationReference relation)
    {
        if (relation is SqlTableReference table) { yield return table; }
        else if (relation is SqlJoinedTableReference group)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            foreach (var nested in SourceReferences([group.Item])) { yield return nested; }
        }
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
            SqlQuantifiedComparisonExpression quantified => quantified.Subquery,
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
