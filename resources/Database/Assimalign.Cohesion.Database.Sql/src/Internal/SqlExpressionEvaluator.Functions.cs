using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlExpressionEvaluator
{
    /// <summary>
    /// Resolves a call against the engine's function catalog, once per statement: the overload whose
    /// parameters the call's argument count and static types match best (<see cref="SqlFunctionResolver"/>).
    /// </summary>
    /// <param name="call">The call.</param>
    /// <returns>The overload; null when the catalog has no function of the call's name.</returns>
    /// <exception cref="SqlEvaluationException">
    /// No overload accepts the call (<c>COHSQLE006</c>), or several accept it equally well (<c>COHSQLE008</c>).
    /// </exception>
    internal SqlFunction? ResolveFunction(SqlFunctionCallExpression call)
        => _functions.Catalog.TryGetOverloads(call.FunctionName, out var overloads) ? Resolve(call, overloads) : null;

    /// <summary>
    /// Checks a <c>COALESCE</c>'s operands: one or more, none of them <c>*</c>, as PostgreSQL's
    /// grammar takes them (ISO requires two; one operand is the operand itself).
    /// </summary>
    /// <param name="call">The call to <c>COALESCE</c>.</param>
    /// <exception cref="SqlEvaluationException">The call passes no operand or a <c>*</c> (<c>COHSQLE006</c>).</exception>
    internal static void CheckCoalesce(SqlFunctionCallExpression call)
    {
        var arguments = call.Arguments;
        bool star = false;
        for (int index = 0; index < arguments.Count; index++)
        {
            star |= arguments[index] is SqlStarExpression;
        }
        if (arguments.Count == 0 || star)
        {
            throw SqlEvaluationException.FunctionSignatureMismatch(call.FunctionName, "1 or more arguments",
                SqlFunctionResolver.DescribeArguments(arguments), SqlStandardLibrary.CoalesceUsage);
        }
    }

    /// <summary>
    /// Binds an aggregate call's arguments over the input row, with the collation the call's input
    /// compares under: no argument for <c>name(*)</c>, whose collation is still resolved, as
    /// <c>COUNT(*)</c>'s always was.
    /// </summary>
    /// <param name="arguments">The call's arguments.</param>
    /// <param name="collation">The collation the arguments' values compare under.</param>
    /// <returns>The bound arguments.</returns>
    internal SqlBoundExpression[] BindArguments(IReadOnlyList<SqlExpression> arguments, out SqlBoundCollation collation)
    {
        if (arguments.Count == 1 && arguments[0] is SqlStarExpression star)
        {
            collation = BindCollation(star);
            return [];
        }
        if (arguments.Count == 1)
        {
            return [Bind(arguments[0], out collation)];
        }

        var bound = new SqlBoundExpression[arguments.Count];
        var candidates = new SqlCollationCandidate[arguments.Count];
        for (int index = 0; index < bound.Length; index++)
        {
            bound[index] = Bind(arguments[index], out candidates[index]);
        }

        collation = SqlBoundCollation.Of(SqlCollationCandidate.Fold(candidates), default, _defaultCollation);
        return bound;
    }

    /// <summary>
    /// The type an expression's values have whatever row it is evaluated over, when the plan can
    /// tell: what overload resolution compares a call's arguments by. <see cref="DatabaseType.Null"/>
    /// when the plan cannot tell — a NULL literal or parameter, a grouping slot, an operand that does
    /// not resolve — which matches any parameter. An integer literal is typed by its magnitude
    /// (INTEGER when it fits, else BIGINT), as PostgreSQL types an integer constant, so <c>f(5)</c>
    /// calls an overload over INTEGER.
    /// </summary>
    /// <param name="expression">The expression.</param>
    /// <returns>The static type; never throws for an expression planning would reject.</returns>
    /// <exception cref="InsufficientExecutionStackException">The thread has too little stack left to walk the expression.</exception>
    internal DatabaseType StaticTypeOf(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (_valueOrdinals is not null && _valueOrdinals.ContainsKey(expression))
        {
            return _projectionSources is not null && _projectionSources.TryGetValue(expression, out var output)
                ? output.Type
                : DatabaseType.Null;
        }

        switch (expression)
        {
            case SqlLiteralExpression literal:
                return literal.LiteralType switch
                {
                    SqlLiteralType.Integer => int.TryParse(literal.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                        ? DatabaseType.Int32 : DatabaseType.Int64,
                    SqlLiteralType.Float => DatabaseType.Decimal,
                    SqlLiteralType.String => DatabaseType.String,
                    SqlLiteralType.Boolean => DatabaseType.Boolean,
                    _ => DatabaseType.Null,
                };
            case SqlColumnReferenceExpression column:
                try
                {
                    int ordinal = ResolveColumn(column);
                    return ordinal < _columns.Count ? _columns[ordinal].Type.Type : DatabaseType.Null;
                }
                catch (DatabaseException)
                {
                    return DatabaseType.Null;
                }
            case SqlParameterExpression parameter:
                return TryGetParameterValue(parameter, out object? value) ? ValueTypeOf(value) : DatabaseType.Null;
            case SqlCollateExpression collate:
                return StaticTypeOf(collate.Operand);
            case SqlCastExpression cast:
                return cast.TargetTypeInfo?.Type ?? DatabaseType.Null;
            case SqlUnaryExpression { Operator: SqlUnaryOperator.Not }:
                return DatabaseType.Boolean;
            case SqlUnaryExpression { Operator: SqlUnaryOperator.Plus } plus:
                return StaticTypeOf(plus.Operand);
            case SqlUnaryExpression negation:
                return StaticTypeOf(negation.Operand) switch
                {
                    DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 => DatabaseType.Int64,
                    DatabaseType.Float32 => DatabaseType.Float64,
                    var type => type,
                };
            case SqlBinaryExpression { Operator: SqlBinaryOperator.Concat }:
                return DatabaseType.String;
            case SqlBinaryExpression { Operator: SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply
                or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo } arithmetic:
                return ArithmeticType(StaticTypeOf(arithmetic.Left), StaticTypeOf(arithmetic.Right));
            case SqlBinaryExpression or SqlLogicalExpression or SqlIsNullExpression or SqlBetweenExpression
                or SqlInExpression or SqlLikeExpression or SqlExistsExpression:
                return DatabaseType.Boolean;
            case SqlSubqueryExpression:
                return _subquerySlots is not null && _subquerySlots.TryGetValue(expression, out var slot) ? slot.Type : DatabaseType.Null;
            case SqlCaseExpression conditional:
                foreach (var clause in conditional.WhenClauses)
                {
                    if (StaticTypeOf(clause.Result) is var result && result != DatabaseType.Null)
                    {
                        return result;
                    }
                }

                return conditional.ElseResult is null ? DatabaseType.Null : StaticTypeOf(conditional.ElseResult);
            case SqlFunctionCallExpression call:
                return StaticCallType(call);
            default:
                return DatabaseType.Null;
        }
    }

    /// <summary>
    /// The type a call's result has whatever row it is evaluated over: its overload's declared
    /// result type, or for a polymorphic result the type its arguments give it. A call that does not
    /// resolve, and an aggregate's when <paramref name="scalarOnly"/> is set, has none.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="scalarOnly">Whether an aggregate call is untyped, as a sign's operand check wants it.</param>
    /// <returns>The static type, or <see cref="DatabaseType.Null"/>.</returns>
    internal DatabaseType StaticCallType(SqlFunctionCallExpression call, bool scalarOnly = false)
    {
        if (SqlStandardLibrary.IsCoalesce(call.FunctionName))
        {
            foreach (var argument in call.Arguments)
            {
                if (StaticTypeOf(argument) is var type && type != DatabaseType.Null)
                {
                    return type;
                }
            }

            return DatabaseType.Null;
        }

        SqlFunction? function;
        try
        {
            function = ResolveFunction(call);
        }
        catch (SqlEvaluationException)
        {
            return DatabaseType.Null;
        }

        if (function is null || (scalarOnly && function.Kind != SqlFunctionKind.Scalar))
        {
            return DatabaseType.Null;
        }
        if (!function.ReturnType.IsPseudo)
        {
            return function.ReturnType.Storage.Type;
        }

        return ResultTypeOf(function, call.Arguments);
    }

    /// <summary>A resolved call's result type, its arguments typed statically (<see cref="SqlFunctionResolver.ResultType"/>).</summary>
    /// <param name="function">The overload.</param>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns>The result type, or <see cref="DatabaseType.Null"/>.</returns>
    internal DatabaseType ResultTypeOf(SqlFunction function, IReadOnlyList<SqlExpression> arguments)
    {
        Span<DatabaseType> types = arguments.Count <= 16 ? stackalloc DatabaseType[arguments.Count] : new DatabaseType[arguments.Count];
        for (int index = 0; index < types.Length; index++)
        {
            types[index] = StaticTypeOf(arguments[index]);
        }

        return SqlFunctionResolver.ResultType(function, types);
    }

    /// <summary>Resolves a call against its name's overloads: by count, then, when they matter, by static type.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlFunction Resolve(SqlFunctionCallExpression call, SqlFunction[] overloads)
    {
        var arguments = call.Arguments;
        SqlFunctionResolver.CheckArity(call.FunctionName, overloads, arguments);
        if (!SqlFunctionResolver.NeedsArgumentTypes(overloads, arguments))
        {
            return SqlFunctionResolver.Choose(call.FunctionName, overloads, arguments, default);
        }

        Span<DatabaseType> types = arguments.Count <= 16 ? stackalloc DatabaseType[arguments.Count] : new DatabaseType[arguments.Count];
        for (int index = 0; index < types.Length; index++)
        {
            types[index] = StaticTypeOf(arguments[index]);
        }

        return SqlFunctionResolver.Choose(call.FunctionName, overloads, arguments, types);
    }

    /// <summary>The arithmetic result type the evaluator computes: DECIMAL over any decimal or approximate operand, BIGINT over integers.</summary>
    private static DatabaseType ArithmeticType(DatabaseType left, DatabaseType right)
    {
        static bool Inexact(DatabaseType type) => type is DatabaseType.Decimal or DatabaseType.Float32 or DatabaseType.Float64;
        static bool Integer(DatabaseType type) => type is DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64;

        if (Inexact(left) || Inexact(right))
        {
            return DatabaseType.Decimal;
        }

        return Integer(left) && Integer(right) ? DatabaseType.Int64 : DatabaseType.Null;
    }

    /// <summary>The storage type of a supplied value, or <see cref="DatabaseType.Null"/> for NULL or a CLR type without one.</summary>
    private static DatabaseType ValueTypeOf(object? value)
    {
        try
        {
            return SqlValue.FromObject(value).Type;
        }
        catch (DatabaseException)
        {
            return DatabaseType.Null;
        }
    }
}
