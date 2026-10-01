using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    /// <summary>
    /// The function names the SQL profile declares. A call to any other name is unknown.
    /// </summary>
    private static readonly HashSet<string> _declaredFunctions = CreateDeclaredFunctions();

    private static HashSet<string> CreateDeclaredFunctions()
    {
        var profile = SqlLanguageProfile.Instance;
        var names = new HashSet<string>(profile.IsCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (string name in profile.Functions)
        {
            names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Rejects a call to a function the SQL profile does not declare, anywhere in the
    /// statement, before binding reads the catalog or execution reads a row (#1068). The
    /// evaluator used to find an unknown name per row, so the same statement failed on a
    /// populated table and succeeded on an empty one. Declared names that do not execute
    /// yet, such as <c>NULLIF</c>, are not unknown: they still fail at evaluation until the
    /// shared coded-diagnostics work (#1103) rejects them at parse time.
    /// </summary>
    /// <param name="statement">The statement about to be planned.</param>
    /// <exception cref="DatabaseException">The statement calls an unknown function.</exception>
    private static void RejectUnknownFunctions(SqlQueryExpression statement)
    {
        switch (statement)
        {
            case SqlSelectExpression select:
                RejectUnknownFunctions(select);
                break;
            case SqlInsertExpression insert:
                if (insert.Values is not null)
                {
                    foreach (var row in insert.Values)
                    {
                        foreach (var value in row)
                        {
                            RejectUnknownFunctions(value);
                        }
                    }
                }
                if (insert.SelectSource is not null)
                {
                    RejectUnknownFunctions(insert.SelectSource);
                }
                break;
            case SqlUpdateExpression update:
                foreach (var assignment in update.Assignments)
                {
                    RejectUnknownFunctions(assignment.Value);
                }
                RejectUnknownFunctions(update.Where);
                break;
            case SqlDeleteExpression delete:
                RejectUnknownFunctions(delete.Where);
                break;
            case SqlCreateTableExpression create:
                foreach (var column in create.Columns)
                {
                    RejectUnknownFunctions(column.DefaultValue);
                }
                foreach (var constraint in create.Constraints)
                {
                    RejectUnknownFunctions(constraint.CheckExpression);
                }
                break;
            case SqlAlterTableExpression { Action: SqlAlterAddColumnAction add }:
                RejectUnknownFunctions(add.Column.DefaultValue);
                foreach (var constraint in add.Column.Constraints)
                {
                    RejectUnknownFunctions(constraint.CheckExpression);
                }
                break;
            case SqlAlterTableExpression { Action: SqlAlterAddConstraintAction add }:
                RejectUnknownFunctions(add.Constraint.CheckExpression);
                break;
        }
    }

    private static void RejectUnknownFunctions(SqlSelectExpression select)
    {
        foreach (var column in select.Columns)
        {
            RejectUnknownFunctions(column.Expression);
        }
        foreach (var join in select.Joins)
        {
            RejectUnknownFunctions(join.Condition);
        }
        RejectUnknownFunctions(select.Where);
        foreach (var key in select.GroupBy)
        {
            RejectUnknownFunctions(key);
        }
        RejectUnknownFunctions(select.Having);
        foreach (var order in select.OrderBy)
        {
            RejectUnknownFunctions(order.Expression);
        }
        RejectUnknownFunctions(select.Limit);
        RejectUnknownFunctions(select.Offset);
    }

    private static void RejectUnknownFunctions(SqlExpression? expression)
    {
        // Subqueries recurse through here as well as children (#1151).
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            case null:
                return;
            case SqlFunctionCallExpression call when !_declaredFunctions.Contains(call.FunctionName):
                throw new DatabaseException($"Unknown function '{call.FunctionName}'.");
            // Children treats a query body as opaque; its own clauses are checked here.
            case SqlSubqueryExpression scalar:
                RejectUnknownFunctions(scalar.Select);
                break;
            case SqlExistsExpression exists:
                RejectUnknownFunctions(exists.Subquery);
                break;
            case SqlInExpression { Subquery: not null } membership:
                RejectUnknownFunctions(membership.Subquery);
                break;
        }

        foreach (var child in Children(expression))
        {
            RejectUnknownFunctions(child);
        }
    }
}
