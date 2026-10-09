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
    {
        if (!_functions.Catalog.TryGetOverloads(call.FunctionName, out var overloads))
        {
            return null;
        }

        // A call typed earlier as another call's argument resolved the same way; one that failed
        // there resolves again, so the failure is raised here.
        return TryGetTypedCall(call, out var typed) && typed.Function is { } function ? function : Resolve(call, overloads);
    }

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
    /// when the plan cannot tell — a NULL literal or parameter, an operand that does not resolve —
    /// which matches any parameter. A grouping key or aggregate result has the type the grouping
    /// plan computed for its slot over the input row. An integer literal is typed by its magnitude
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
            return SlotType(expression);
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

        return ResolvedCallType(call, scalarOnly);
    }

    /// <summary>
    /// The type of a slot's value: an ordering output's projection type, or a grouping key's or
    /// aggregate result's type over the input row. Out of <see cref="StaticTypeOf"/>'s frame, which
    /// every level of a nest repeats.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private DatabaseType SlotType(SqlExpression expression)
    {
        if (_projectionSources is not null && _projectionSources.TryGetValue(expression, out var output))
        {
            return output.Type;
        }

        return _typing?.SlotScope is { } slotScope ? slotScope.StaticTypeOf(expression) : DatabaseType.Null;
    }

    /// <summary>A call's static type through <see cref="TypeCall"/>; out of <see cref="StaticCallType"/>'s frame, which a nest of COALESCE repeats.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private DatabaseType ResolvedCallType(SqlFunctionCallExpression call, bool scalarOnly)
    {
        var typed = TypeCall(call);
        return typed.Function is null || (scalarOnly && typed.Function.Kind != SqlFunctionKind.Scalar) ? DatabaseType.Null : typed.Type;
    }

    /// <summary>
    /// Resolves a call and types its result in one pass over its arguments: each argument is typed
    /// at most once, whether the overload's choice, its polymorphic result, or both need the types.
    /// (Typing them once to choose and again to type the result doubled the work at every level of
    /// a nest, so planning time grew exponentially with depth.) A call typed as another call's
    /// argument is remembered for this scope, so a nest of calls that need their arguments' types
    /// plans in time linear in its size, however often the planner asks about each level.
    /// </summary>
    /// <param name="call">The call; not <c>COALESCE</c>.</param>
    /// <returns>The overload and its result type; a null overload and <see cref="DatabaseType.Null"/> when the call does not resolve.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (SqlFunction? Function, DatabaseType Type) TypeCall(SqlFunctionCallExpression call)
    {
        if (TryGetTypedCall(call, out var typed))
        {
            return typed;
        }

        typed = default;
        if (_functions.Catalog.TryGetOverloads(call.FunctionName, out var overloads))
        {
            var arguments = call.Arguments;
            Span<DatabaseType> types = arguments.Count <= 16 ? stackalloc DatabaseType[arguments.Count] : new DatabaseType[arguments.Count];
            try
            {
                SqlFunctionResolver.CheckArity(call.FunctionName, overloads, arguments);
                bool needed = SqlFunctionResolver.NeedsArgumentTypes(overloads, arguments);
                if (needed)
                {
                    TypeArguments(arguments, types);
                }

                var function = SqlFunctionResolver.Choose(call.FunctionName, overloads, arguments, needed ? types : default);
                if (!function.ReturnType.IsPseudo)
                {
                    typed = (function, function.ReturnType.Storage.Type);
                }
                else
                {
                    if (!needed)
                    {
                        TypeArguments(arguments, types);
                    }

                    typed = (function, SqlFunctionResolver.ResultType(function, types));
                }
            }
            catch (SqlEvaluationException)
            {
                typed = default;
            }
        }

        RememberTypedCall(call, typed);
        return typed;
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
        TypeArguments(arguments, types);
        return SqlFunctionResolver.Choose(call.FunctionName, overloads, arguments, types);
    }

    /// <summary>Types a call's arguments, each once.</summary>
    private void TypeArguments(IReadOnlyList<SqlExpression> arguments, Span<DatabaseType> types)
    {
        for (int index = 0; index < types.Length; index++)
        {
            types[index] = StaticTypeOf(arguments[index]);
        }
    }

    /// <summary>
    /// Reads what this scope remembered about a call. The memory is cleared when the planner adds a
    /// subquery slot, the one input of a static type that changes while a scope plans.
    /// </summary>
    private bool TryGetTypedCall(SqlFunctionCallExpression call, out (SqlFunction? Function, DatabaseType Type) typed)
    {
        if (_typing?.Calls is { } calls && _typing.IsCurrent(_subquerySlots?.Count ?? 0))
        {
            return calls.TryGetValue(call, out typed);
        }

        typed = default;
        return false;
    }

    /// <summary>
    /// Remembers a typed call. Only a call's static type is asked for (another call's argument, a
    /// sign's operand), never a call the planner merely resolves, so a statement whose calls take
    /// only columns, constants and parameters allocates nothing for it; the shared execution scope
    /// remembers nothing.
    /// </summary>
    private void RememberTypedCall(SqlFunctionCallExpression call, (SqlFunction? Function, DatabaseType Type) typed)
    {
        if (ReferenceEquals(this, RowEvaluator))
        {
            return;
        }

        var typing = _typing ??= new SqlTypingMemory(null);
        typing.Remember(call, typed, _subquerySlots?.Count ?? 0);
    }

    /// <summary>
    /// What a scope's static typing keeps: the scope a grouping's slots are typed in, and the calls
    /// it typed with what each resolved to and its result type. The calls are forgotten when the
    /// planner adds a subquery slot, the one input of a static type that changes while a scope plans.
    /// </summary>
    private sealed class SqlTypingMemory
    {
        private int _subquerySlots;

        internal SqlTypingMemory(SqlExpressionEvaluator? slotScope) => SlotScope = slotScope;

        /// <summary>Gets the scope a grouping's slot expressions are typed in; null outside a grouping.</summary>
        internal SqlExpressionEvaluator? SlotScope { get; }

        /// <summary>Gets the typed calls; null until one is remembered.</summary>
        internal Dictionary<SqlFunctionCallExpression, (SqlFunction? Function, DatabaseType Type)>? Calls { get; private set; }

        /// <summary>Whether the remembered calls were typed with the scope's current subquery slots; forgets them otherwise.</summary>
        /// <param name="subquerySlots">The scope's number of subquery slots now.</param>
        /// <returns><see langword="true"/> when the remembered calls still hold.</returns>
        internal bool IsCurrent(int subquerySlots)
        {
            if (subquerySlots == _subquerySlots)
            {
                return true;
            }

            Calls?.Clear();
            _subquerySlots = subquerySlots;
            return false;
        }

        /// <summary>Remembers one typed call.</summary>
        internal void Remember(SqlFunctionCallExpression call, (SqlFunction? Function, DatabaseType Type) typed, int subquerySlots)
        {
            if (Calls is null)
            {
                Calls = new Dictionary<SqlFunctionCallExpression, (SqlFunction?, DatabaseType)>(ReferenceEqualityComparer.Instance);
                _subquerySlots = subquerySlots;
            }
            else
            {
                IsCurrent(subquerySlots);
            }

            Calls[call] = typed;
        }
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
