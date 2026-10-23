using System;
using System.Globalization;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    /// <summary>
    /// Checks every function call in the statement, in every expression position, before binding
    /// reads the catalog or execution reads a row. A name the engine's function catalog does not hold
    /// is unknown (#1068) unless it is an evaluator form or the SQL dialect declares it. Recognized
    /// non-executing names such as <c>RANK</c> reject at parse time with <c>CK00001</c> (#1103);
    /// a call whose argument count, or <c>*</c>, no overload
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
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (statement)
        {
            case SqlSetOperationExpression set:
                var pending = new System.Collections.Generic.Stack<(SqlQueryBodyExpression Query, bool Visited)>();
                pending.Push((set, false));
                while (pending.TryPop(out var item))
                {
                    if (item.Query is SqlSetOperationExpression operation)
                    {
                        if (!item.Visited)
                        {
                            pending.Push((operation, true));
                            pending.Push((operation.Right, false));
                            pending.Push((operation.Left, false));
                            continue;
                        }
                        foreach (var order in operation.OrderBy) { ValidateFunctionCalls(order.Expression); }
                        ValidateFunctionCalls(operation.Limit);
                        ValidateFunctionCalls(operation.Offset);
                    }
                    else { ValidateFunctionCalls((SqlQueryExpression)item.Query); }
                }
                break;
            case SqlSelectExpression select:
                ValidateFunctionCalls(select);
                break;
            case SqlInsertExpression insert:
                if (insert.OnConflict is { } conflict)
                {
                    foreach (var assignment in conflict.Assignments) { ValidateFunctionCalls(assignment.Value); }
                    ValidateFunctionCalls(conflict.Where);
                }
                if (insert.Returning is not null)
                {
                    foreach (var column in insert.Returning) { ValidateFunctionCalls(column.Expression); }
                }
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
                if (update.Returning is not null)
                {
                    foreach (var column in update.Returning) { ValidateFunctionCalls(column.Expression); }
                }
                foreach (var assignment in update.Assignments)
                {
                    ValidateFunctionCalls(assignment.Value);
                }
                ValidateFunctionCalls(update.Where);
                break;
            case SqlDeleteExpression delete:
                if (delete.Returning is not null)
                {
                    foreach (var column in delete.Returning) { ValidateFunctionCalls(column.Expression); }
                }
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
            case SqlAlterTableExpression { Action: SqlAlterSetDefaultAction change }:
                ValidateFunctionCalls(change.DefaultValue);
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
        for (int i = 0; i < select.From.Count; i++)
        {
            if (select.From[i].Table is SqlDerivedTableReference derived) { ValidateFunctionCalls(derived.Query); }
            foreach (var join in select.From[i].Joins)
            {
                if (join.Table is SqlDerivedTableReference joined) { ValidateFunctionCalls(joined.Query); }
                ValidateFunctionCalls(join.Condition);
            }
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
        _containsLike |= expression is SqlLikeExpression;
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

        if (expression is SqlFunctionCallExpression call && _functions.ViewCalls?.ContainsKey(call) != true)
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
            if (!SqlStandardLibrary.IsEvaluatorForm(call.FunctionName) && !catalog.TryGetOverloads(call.FunctionName, out _) &&
                !SqlStandardLibrary.IsDeclaredName(call.FunctionName))
            {
                // The likeliest cause in a declared schema: the function was not registered on this engine.
                throw new SqlPlanningException("COHSQLE016",
                    $"Unknown function '{call.FunctionName}'. Register it on the engine's builder (SqlDatabaseEngineBuilder.Functions) before Build.", call.Location);
            }

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
        try
        {
            CheckCallCore(call, catalog);
        }
        catch (SqlEvaluationException exception) when (exception.Diagnostics[0].Start is null && call.Location is not null)
        {
            throw exception.At(call.Location);
        }
    }

    internal static void ValidateCallModifiers(SqlFunctionCallExpression call, SqlFunctionCatalog catalog)
    {
        if ((call.Quantifier != SqlSetQuantifier.Default || call.OrderBy.Count > 0) && !catalog.IsAggregate(call.FunctionName))
        {
            throw new SqlPlanningException("COHSQLE032", string.Format(CultureInfo.CurrentCulture, ErrorMessages.AggregateModifiersOnScalar, call.FunctionName), call.Location);
        }
    }

    private static void CheckCallCore(SqlFunctionCallExpression call, SqlFunctionCatalog catalog)
    {
        if (SqlStandardLibrary.IsEvaluatorForm(call.FunctionName))
        {
            SqlExpressionEvaluator.CheckEvaluatorForm(call);
        }
        else if (catalog.TryGetOverloads(call.FunctionName, out var overloads))
        {
            SqlFunctionResolver.CheckArity(call.FunctionName, overloads, call.Arguments);
        }
        else if (!SqlStandardLibrary.IsDeclaredName(call.FunctionName))
        {
            throw new SqlPlanningException("COHSQLE016", $"Unknown function '{call.FunctionName}'.", call.Location);
        }
        ValidateCallModifiers(call, catalog);
    }
}
