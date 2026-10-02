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
    /// Resolves every function call in the statement, in every expression position, before
    /// binding reads the catalog or execution reads a row. A call to a name the SQL profile does
    /// not declare is unknown (#1068), and a call whose arguments its function's signature does
    /// not accept fails with <c>COHSQLE006</c> (#1189): <c>ABS(1, 2)</c>, <c>UPPER()</c>,
    /// <c>COALESCE()</c>, <c>COUNT(a, b)</c>, <c>SUM(*)</c>. The evaluator used to find an unknown
    /// name per row, and to evaluate a call's argument only when it had exactly one, so a
    /// wrong-arity call returned NULL and a CHECK built on one never fired. Declared names that
    /// do not execute yet, such as <c>NULLIF</c>, are neither unknown nor in the signature table:
    /// they still fail at evaluation until the shared coded-diagnostics work (#1103) rejects
    /// them at parse time.
    /// </summary>
    /// <remarks>
    /// A call's arguments are resolved before the call itself, as PostgreSQL's parse analysis
    /// does (<c>transformFuncCall</c> transforms the arguments, then <c>ParseFuncOrColumn</c>
    /// resolves the function, in <c>src/backend/parser/parse_expr.c</c>), so in
    /// <c>FOO(ABS())</c> the inner call's error is the one reported.
    /// </remarks>
    /// <param name="statement">The statement about to be planned.</param>
    /// <exception cref="DatabaseException">The statement calls an unknown function.</exception>
    /// <exception cref="SqlEvaluationException">
    /// The statement calls a function with arguments its signature does not accept (<c>COHSQLE006</c>).
    /// </exception>
    private static void ValidateFunctionCalls(SqlQueryExpression statement)
    {
        switch (statement)
        {
            case SqlSelectExpression select:
                ValidateFunctionCalls(select);
                break;
            case SqlInsertExpression insert:
                if (insert.Values is not null)
                {
                    foreach (var row in insert.Values)
                    {
                        foreach (var value in row)
                        {
                            ValidateFunctionCalls(value);
                        }
                    }
                }
                if (insert.SelectSource is not null)
                {
                    ValidateFunctionCalls(insert.SelectSource);
                }
                break;
            case SqlUpdateExpression update:
                foreach (var assignment in update.Assignments)
                {
                    ValidateFunctionCalls(assignment.Value);
                }
                ValidateFunctionCalls(update.Where);
                break;
            case SqlDeleteExpression delete:
                ValidateFunctionCalls(delete.Where);
                break;
            case SqlCreateTableExpression create:
                foreach (var column in create.Columns)
                {
                    ValidateFunctionCalls(column.DefaultValue);
                }
                foreach (var constraint in create.Constraints)
                {
                    ValidateFunctionCalls(constraint.CheckExpression);
                }
                break;
            case SqlAlterTableExpression { Action: SqlAlterAddColumnAction add }:
                ValidateFunctionCalls(add.Column.DefaultValue);
                foreach (var constraint in add.Column.Constraints)
                {
                    ValidateFunctionCalls(constraint.CheckExpression);
                }
                break;
            case SqlAlterTableExpression { Action: SqlAlterAddConstraintAction add }:
                ValidateFunctionCalls(add.Constraint.CheckExpression);
                break;
        }
    }

    private static void ValidateFunctionCalls(SqlSelectExpression select)
    {
        foreach (var column in select.Columns)
        {
            ValidateFunctionCalls(column.Expression);
        }
        foreach (var join in select.Joins)
        {
            ValidateFunctionCalls(join.Condition);
        }
        ValidateFunctionCalls(select.Where);
        foreach (var key in select.GroupBy)
        {
            ValidateFunctionCalls(key);
        }
        ValidateFunctionCalls(select.Having);
        foreach (var order in select.OrderBy)
        {
            ValidateFunctionCalls(order.Expression);
        }
        ValidateFunctionCalls(select.Limit);
        ValidateFunctionCalls(select.Offset);
    }

    private static void ValidateFunctionCalls(SqlExpression? expression)
    {
        // Subqueries recurse through here as well as children (#1151).
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            case null:
                return;
            // Children treats a query body as opaque; its own clauses are checked here.
            case SqlSubqueryExpression scalar:
                ValidateFunctionCalls(scalar.Select);
                break;
            case SqlExistsExpression exists:
                ValidateFunctionCalls(exists.Subquery);
                break;
            case SqlInExpression { Subquery: not null } membership:
                ValidateFunctionCalls(membership.Subquery);
                break;
        }

        foreach (var child in Children(expression))
        {
            ValidateFunctionCalls(child);
        }

        if (expression is SqlFunctionCallExpression call)
        {
            if (!_declaredFunctions.Contains(call.FunctionName))
            {
                throw new DatabaseException($"Unknown function '{call.FunctionName}'.");
            }

            SqlFunctionSignatures.Resolve(call);
        }
    }
}
