using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    /// <summary>
    /// Checks every function call in the statement, in every expression position, before binding
    /// reads the catalog or execution reads a row. A name the engine's function catalog does not hold
    /// is unknown (#1068) unless the SQL dialect declares it as a function that does not execute yet,
    /// such as <c>NULLIF</c>, which still fails at evaluation until the shared coded-diagnostics work
    /// (#1103) rejects such names at parse time; a call whose argument count, or <c>*</c>, no overload
    /// of its function accepts fails with <c>COHSQLE006</c> (#1189): <c>ABS(1, 2)</c>, <c>UPPER()</c>,
    /// <c>COALESCE()</c>, <c>COUNT(a, b)</c>, <c>SUM(*)</c>. The evaluator used to find an unknown name
    /// per row, and to evaluate a call's argument only when it had exactly one, so a wrong-arity call
    /// returned NULL and a CHECK built on one never fired. The overload a call executes is chosen by
    /// its arguments' types once a scope types them (<c>ValidateExpression</c>, binding).
    /// </summary>
    /// <remarks>
    /// A call's arguments are checked before the call itself, as PostgreSQL's parse analysis does
    /// (<c>transformFuncCall</c> transforms the arguments, then <c>ParseFuncOrColumn</c> resolves the
    /// function, in <c>src/backend/parser/parse_expr.c</c>), so in <c>FOO(ABS())</c> the inner call's
    /// error is the one reported. The catalog is the engine's, so a function an application
    /// registered is as known as a built-in (phase E2).
    /// </remarks>
    /// <param name="statement">The statement about to be planned.</param>
    /// <exception cref="DatabaseException">The statement calls an unknown function.</exception>
    /// <exception cref="SqlEvaluationException">
    /// The statement calls a function with a number of arguments no overload accepts (<c>COHSQLE006</c>).
    /// </exception>
    private void ValidateFunctionCalls(SqlQueryExpression statement)
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

    private void ValidateFunctionCalls(SqlSelectExpression select)
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

    private void ValidateFunctionCalls(SqlExpression? expression)
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
            CheckCall(call, _functions.Catalog);
        }
    }

    /// <summary>
    /// Checks every call of an expression no statement carries yet, a compiled schema's declared
    /// CHECK, as the planner checks a statement's calls: an unknown name, and a count no overload
    /// accepts (<c>COHSQLE006</c>), fail here, so the engine's build reports them as the DDL would.
    /// </summary>
    /// <param name="expression">The expression.</param>
    /// <param name="catalog">The engine's function catalog.</param>
    /// <exception cref="DatabaseException">The expression calls an unknown function.</exception>
    /// <exception cref="SqlEvaluationException">A call's argument count matches no overload (<c>COHSQLE006</c>).</exception>
    internal static void ValidateDeclaredCalls(SqlExpression expression, SqlFunctionCatalog catalog)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (var child in Children(expression))
        {
            ValidateDeclaredCalls(child, catalog);
        }

        if (expression is SqlFunctionCallExpression call)
        {
            CheckCall(call, catalog);
        }
    }

    /// <summary>Checks one call's name and argument count against the engine's function catalog.</summary>
    /// <param name="call">The call.</param>
    /// <param name="catalog">The engine's function catalog.</param>
    /// <exception cref="DatabaseException">The name is unknown.</exception>
    /// <exception cref="SqlEvaluationException">No overload accepts the call's argument count (<c>COHSQLE006</c>).</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckCall(SqlFunctionCallExpression call, SqlFunctionCatalog catalog)
    {
        if (SqlStandardLibrary.IsCoalesce(call.FunctionName))
        {
            SqlExpressionEvaluator.CheckCoalesce(call);
        }
        else if (catalog.TryGetOverloads(call.FunctionName, out var overloads))
        {
            SqlFunctionResolver.CheckArity(call.FunctionName, overloads, call.Arguments);
        }
        else if (!SqlStandardLibrary.IsDeclaredName(call.FunctionName))
        {
            throw new DatabaseException($"Unknown function '{call.FunctionName}'.");
        }
    }
}
