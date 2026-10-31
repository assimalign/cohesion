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
    /// No overload accepts the call (<c>CS01006</c>), or several accept it equally well (<c>CS01008</c>).
    /// </exception>
    internal SqlFunction? ResolveFunction(SqlFunctionCallExpression call)
    {
        if (SqlSequenceExecution.IsFunction(call.FunctionName))
        {
            SqlSequenceExecution.CheckCall(call);
            if (call.Arguments.Count > 0 && StaticTypeOf(call.Arguments[0]) is not
                (DatabaseType.Null or DatabaseType.String or DatabaseType.Character or DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64))
            { throw SqlEvaluationException.FunctionArgumentTypeMismatch(call.FunctionName, StaticTypeOf(call.Arguments[0]).ToString(), "sequence name or integer object id"); }
        }
        if (SqlStandardLibrary.IsGrouping(call.FunctionName)) { return null; }
        // A MERGE action slot has its own type; an application function with the same
        // spelling must not supply its arity, length or domain metadata.
        if (_projectionSources?.ContainsKey(call) == true &&
            call.FunctionName.Equals("MERGE_ACTION", StringComparison.OrdinalIgnoreCase)) { return null; }
        if (!TryGetCallOverloads(call, out var overloads))
        {
            return null;
        }

        // A call typed earlier as another call's argument resolved the same way; one that failed
        // there resolves again, so the failure is raised here.
        var resolved = TryGetTypedCall(call, out var typed) && typed.Function is { } function ? function : Resolve(call, overloads);
        if (resolved is SqlBuiltinScalarFunction { Name: "DATE_TRUNC" })
        {
            var field = Bind(call.Arguments[0]);
            if (field is SqlBoundConstant or SqlBoundParameter && Evaluate(field, []) is string text &&
                !SqlTemporalFunctions.AcceptsTruncation(text, StaticTypeOf(call.Arguments[1])))
            {
                throw SqlEvaluationException.FunctionArgumentTypeMismatch(call.FunctionName, text, "DATE_TRUNC(compatible field, temporal)");
            }
        }
        RecordViewCall(call, resolved);
        return resolved;
    }

    private void RecordViewCall(SqlFunctionCallExpression call, SqlFunction function)
    {
        if (_functions.ViewCalls is SqlViewCallCapture capture) { capture[call] = SqlDefinitionBinder.Key(function); }
    }

    internal void PinFunction(SqlFunctionCallExpression call, string identity)
    {
        var functions = new List<SqlFunction>();
        foreach (var candidate in _functions.Catalog.GetOverloads(call.FunctionName))
        {
            if (SqlDefinitionBinder.Key(candidate).Equals(identity, StringComparison.OrdinalIgnoreCase))
            {
                functions.Add(candidate);
            }
        }
        var typing = _typing ??= new SqlTypingMemory(null);
        (typing.RecordedCalls ??= new(ReferenceEqualityComparer.Instance)).Add(call, (identity, functions.ToArray()));
    }

    internal string? RecordedIdentity(SqlFunctionCallExpression call)
        => _typing?.RecordedCalls is { } calls && calls.TryGetValue(call, out var recorded) ? recorded.Identity : null;

    private bool TryGetCallOverloads(SqlFunctionCallExpression call, out SqlFunction[] overloads)
    {
        if (_typing?.RecordedCalls is { } calls && calls.TryGetValue(call, out var recorded))
        {
            overloads = recorded.Functions;
            return overloads.Length != 0;
        }
        if (_functions.ViewCalls is { } pins && pins is not SqlViewCallCapture && pins.TryGetValue(call, out var identity))
        {
            overloads = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(_functions.Catalog.GetOverloads(call.FunctionName), function =>
                SqlDefinitionBinder.Key(function).Equals(identity, StringComparison.OrdinalIgnoreCase)));
            if (overloads.Length == 0) { throw new SqlPlanningException("CS01009", ErrorMessages.ViewFunctionMissing, call.Location); }
            return true;
        }
        return _functions.Catalog.TryGetOverloads(call.FunctionName, out overloads!);
    }

    /// <summary>
    /// Checks a <c>COALESCE</c>'s operands: one or more, none of them <c>*</c>, as PostgreSQL's
    /// grammar takes them (ISO requires two; one operand is the operand itself).
    /// </summary>
    /// <param name="call">The call to <c>COALESCE</c>.</param>
    /// <exception cref="SqlEvaluationException">The call passes no operand or a <c>*</c> (<c>CS01006</c>).</exception>
    internal static void CheckEvaluatorForm(SqlFunctionCallExpression call)
    {
        var arguments = call.Arguments;
        bool star = false;
        for (int index = 0; index < arguments.Count; index++)
        {
            star |= arguments[index] is SqlStarExpression;
        }
        if (arguments.Count == 0 || star || !SqlStandardLibrary.IsCoalesce(call.FunctionName) && arguments.Count != 2)
        {
            bool coalesce = SqlStandardLibrary.IsCoalesce(call.FunctionName);
            throw SqlEvaluationException.FunctionSignatureMismatch(call.FunctionName, coalesce ? ErrorMessages.OneOrMoreArgumentsExpected : ErrorMessages.TwoArgumentsExpected,
                SqlFunctionResolver.DescribeArguments(arguments), coalesce ? SqlStandardLibrary.CoalesceUsage :
                    call.FunctionName.Equals("EXTRACT", StringComparison.OrdinalIgnoreCase) ? "EXTRACT(field FROM temporal)" : "NULLIF(value, value)");
        }
    }

    /// <summary>
    /// Binds an aggregate call's arguments over the input row, with the collation the call's input
    /// compares under: no argument for <c>name(*)</c>, whose collation is still resolved, as
    /// <c>COUNT(*)</c>'s always was.
    /// </summary>
    /// <param name="arguments">The call's arguments.</param>
    /// <param name="collation">The collation the arguments' values compare under.</param>
    /// <param name="statisticsInput">Whether to normalize every numeric CASE/COALESCE boundary.</param>
    /// <param name="jsonTypes">Resolved JSON-position types for NULLIF's first operand, when applicable.</param>
    /// <returns>The bound arguments.</returns>
    internal SqlBoundExpression[] BindArguments(IReadOnlyList<SqlExpression> arguments, out SqlBoundCollation collation, bool statisticsInput = false,
        Dictionary<SqlExpression, DatabaseType>? jsonTypes = null)
    {
        if (arguments.Count == 1 && arguments[0] is SqlStarExpression star)
        {
            collation = BindCollation(star);
            return [];
        }
        if (arguments.Count == 1)
        {
            var value = jsonTypes is null ? Bind(arguments[0], out SqlCollationCandidate candidate, statisticsInput: statisticsInput)
                : BindJsonOperand(arguments[0], out candidate, statisticsInput, jsonTypes);
            collation = SqlBoundCollation.Of(candidate, default, _defaultCollation);
            return [value];
        }

        var bound = new SqlBoundExpression[arguments.Count];
        var candidates = new SqlCollationCandidate[arguments.Count];
        for (int index = 0; index < bound.Length; index++)
        {
            // NULLIF returns its first operand; the second remains an ordinary comparand.
            bound[index] = index == 0 && jsonTypes is not null ? BindJsonOperand(arguments[index], out candidates[index], statisticsInput, jsonTypes)
                : Bind(arguments[index], out candidates[index], statisticsInput: statisticsInput);
        }

        collation = SqlBoundCollation.Of(SqlCollationCandidate.Fold(candidates), default, _defaultCollation);
        return bound;
    }

    /// <summary>The declared character/binary length of a projected expression, if fixed.</summary>
    /// <param name="expression">The projected expression.</param>
    /// <returns>The declared length, or null when no single modifier describes the result.</returns>
    internal int? MaximumLengthOf(SqlExpression expression) => OutputTypeInfo(expression)?.MaxLength;

    /// <summary>Retains declared type facets through scalar slots, casts and function outputs.</summary>
    internal DatabaseTypeInfo? OutputTypeInfo(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return expression switch
        {
            SqlWindowExpression window when window.Function.Arguments.Count > 0 &&
                window.Function.FunctionName.ToUpperInvariant() is "FIRST_VALUE" or "LAST_VALUE" or "NTH_VALUE"
                => OutputTypeInfo(window.Function.Arguments[0]),
            SqlColumnReferenceExpression column => ColumnMetadata(column).Type,
            SqlCastExpression cast => TryResolveCastTarget(cast)?.Storage,
            SqlCollateExpression collate => OutputTypeInfo(collate.Operand),
            SqlSubqueryExpression => _subquerySlots is not null && _subquerySlots.TryGetValue(expression, out var slot) ? slot.TypeInfo : null,
            SqlCaseExpression conditional => CaseOutputTypeInfo(conditional),
            SqlFunctionCallExpression call when SqlStandardLibrary.IsCoalesce(call.FunctionName) => CommonOutputTypeInfo(call.Arguments),
            // PostgreSQL nodeFuncs.c:327-336: NULLIF returns its first argument or NULL.
            SqlFunctionCallExpression call when call.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase)
                => call.Arguments.Count == 0 ? null : OutputTypeInfo(call.Arguments[0]),
            SqlFunctionCallExpression call when !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName)
                && ResolveFunction(call) is { ReturnType.IsPseudo: false } function => function.ReturnType.Storage,
            _ => null,
        };
    }

    private DatabaseTypeInfo? CaseOutputTypeInfo(SqlCaseExpression conditional)
    {
        // PostgreSQL nodeFuncs.c:389-419: the implicit NULL ELSE has no typmod.
        if (conditional.ElseResult is null || OutputTypeInfo(conditional.ElseResult) is not { } common) { return null; }
        string? domain = OutputDomainName(conditional.ElseResult);
        foreach (var clause in conditional.WhenClauses)
        {
            if (!SameOutputTypeInfo(common, OutputTypeInfo(clause.Result)) ||
                !string.Equals(domain, OutputDomainName(clause.Result), StringComparison.OrdinalIgnoreCase)) { return null; }
        }
        return common;
    }

    private DatabaseTypeInfo? CommonOutputTypeInfo(IReadOnlyList<SqlExpression> arguments)
    {
        // PostgreSQL nodeFuncs.c:452-480: every argument must have the same type/typmod.
        if (arguments.Count == 0 || OutputTypeInfo(arguments[0]) is not { } common) { return null; }
        string? domain = OutputDomainName(arguments[0]);
        for (int index = 1; index < arguments.Count; index++)
        {
            if (!SameOutputTypeInfo(common, OutputTypeInfo(arguments[index])) ||
                !string.Equals(domain, OutputDomainName(arguments[index]), StringComparison.OrdinalIgnoreCase)) { return null; }
        }
        return common;
    }

    private static bool SameOutputTypeInfo(DatabaseTypeInfo first, DatabaseTypeInfo? next)
        => next is not null && first.Type == next.Type && first.MaxLength == next.MaxLength
            && first.Precision == next.Precision && first.Scale == next.Scale;

    /// <summary>Preserves a domain only when every result branch has that same type identity.</summary>
    internal string? OutputDomainName(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        // PostgreSQL parse_coerce.c:1360-1363,1658-1660: domain identity is part
        // of type agreement, even when two domains share their base type/facets.
        switch (expression)
        {
            case SqlWindowExpression window when window.Function.Arguments.Count > 0 &&
                window.Function.FunctionName.ToUpperInvariant() is "FIRST_VALUE" or "LAST_VALUE" or "NTH_VALUE":
                return OutputDomainName(window.Function.Arguments[0]);
            case SqlColumnReferenceExpression column: return ColumnMetadata(column).DomainName;
            case SqlCastExpression cast: return TryResolveCastTarget(cast) is { IsDomain: true } domain ? domain.Name : null;
            case SqlCollateExpression collate: return OutputDomainName(collate.Operand);
            case SqlSubqueryExpression: return _subquerySlots is not null && _subquerySlots.TryGetValue(expression, out var slot) ? slot.DomainName : null;
            case SqlCaseExpression conditional:
                if (conditional.ElseResult is null || OutputDomainName(conditional.ElseResult) is not { } name) { return null; }
                foreach (var clause in conditional.WhenClauses)
                {
                    if (!string.Equals(name, OutputDomainName(clause.Result), StringComparison.OrdinalIgnoreCase)) { return null; }
                }
                return name;
            case SqlFunctionCallExpression call when SqlStandardLibrary.IsCoalesce(call.FunctionName):
                if (call.Arguments.Count == 0 || OutputDomainName(call.Arguments[0]) is not { } common) { return null; }
                for (int index = 1; index < call.Arguments.Count; index++)
                {
                    if (!string.Equals(common, OutputDomainName(call.Arguments[index]), StringComparison.OrdinalIgnoreCase)) { return null; }
                }
                return common;
            case SqlFunctionCallExpression call when !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName):
                return ResolveFunction(call) is { ReturnType.IsDomain: true } function ? function.ReturnType.Name : null;
            default: return null;
        }
    }

    private static DatabaseType AlternativeType(DatabaseType first, DatabaseType next)
        => SqlTemporalArithmetic.IsCalendar(first) && SqlTemporalArithmetic.IsCalendar(next) ? SqlTemporalArithmetic.CommonCalendarType(first, next)
            : first is DatabaseType.Character or DatabaseType.String && next is DatabaseType.Character or DatabaseType.String && first != next
            ? DatabaseType.String : first == DatabaseType.Null ? next : first;

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
    /// <returns>The static type. JSON alternatives enforce their common-type check during binding.</returns>
    /// <exception cref="SqlPlanningException">JSON-position alternatives have incompatible declared types.</exception>
    /// <exception cref="InsufficientExecutionStackException">The thread has too little stack left to walk the expression.</exception>
    internal DatabaseType StaticTypeOf(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlWindowExpression window) { return WindowType(window); }
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
                    SqlLiteralType.Date => DatabaseType.Date,
                    SqlLiteralType.Time => DatabaseType.Time,
                    SqlLiteralType.Timestamp => DatabaseType.DateTime,
                    SqlLiteralType.TimestampWithTimeZone => DatabaseType.DateTimeOffset,
                    SqlLiteralType.Interval => DatabaseType.TimeSpan,
                    SqlLiteralType.Binary => DatabaseType.Binary,
                    _ => DatabaseType.Null,
                };
            case SqlColumnReferenceExpression column:
                try
                {
                    return ResolveColumnType(column) ?? DatabaseType.Null;
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
                return cast.TargetTypeInfo?.Type ?? TryResolveCastTarget(cast)?.Storage.Type ?? DatabaseType.Null;
            case SqlUnaryExpression { Operator: SqlUnaryOperator.Not }:
                return DatabaseType.Boolean;
            case SqlUnaryExpression { Operator: SqlUnaryOperator.Plus or SqlUnaryOperator.BitwiseNot } plus:
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
                return ArithmeticType(arithmetic.Operator, StaticTypeOf(arithmetic.Left), StaticTypeOf(arithmetic.Right));
            case SqlBinaryExpression json when SqlJsonAccess.IsOperator(json.Operator):
                return SqlJsonAccess.ResultType(json.Operator, JsonOperandType(json.Left));
            case SqlBinaryExpression bitwise when SqlBitwise.IsBinary(bitwise.Operator):
                return SqlBitwise.ResultType(bitwise.Operator, StaticTypeOf(bitwise.Left), StaticTypeOf(bitwise.Right));
            case SqlBinaryExpression or SqlLogicalExpression or SqlDistinctExpression or SqlBooleanTestExpression or SqlIsJsonExpression or SqlIsNullExpression or SqlBetweenExpression
                or SqlInExpression or SqlQuantifiedComparisonExpression or SqlLikeExpression or SqlExistsExpression:
                return DatabaseType.Boolean;
            case SqlSubqueryExpression:
                return _subquerySlots is not null && _subquerySlots.TryGetValue(expression, out var slot) ? slot.Type : DatabaseType.Null;
            case SqlCaseExpression conditional:
                DatabaseType alternative = DatabaseType.Null;
                foreach (var clause in conditional.WhenClauses)
                {
                    alternative = AlternativeType(alternative, StaticTypeOf(clause.Result));
                }
                return conditional.ElseResult is null ? alternative : AlternativeType(alternative, StaticTypeOf(conditional.ElseResult));
            case SqlNextValueExpression:
                return DatabaseType.Int64;
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
        if (SqlSequenceExecution.IsFunction(call.FunctionName)) { return DatabaseType.Int64; }
        if (SqlStandardLibrary.IsGrouping(call.FunctionName)) { return DatabaseType.Int32; }
        if (call.FunctionName.Equals("EXTRACT", StringComparison.OrdinalIgnoreCase)) { return DatabaseType.Decimal; }
        if (call.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase)) { return call.Arguments.Count > 0 ? StaticTypeOf(call.Arguments[0]) : DatabaseType.Null; }
        if (SqlStandardLibrary.IsCoalesce(call.FunctionName))
        {
            DatabaseType alternative = DatabaseType.Null;
            foreach (var argument in call.Arguments)
            {
                alternative = AlternativeType(alternative, StaticTypeOf(argument));
            }
            return alternative;
        }

        return ResolvedCallType(call, scalarOnly);
    }

    /// <summary>Whether a call's result type depends on its arguments, using non-throwing binder resolution.</summary>
    internal bool CallResultDependsOnArguments(SqlFunctionCallExpression call)
        => SqlStandardLibrary.IsEvaluatorForm(call.FunctionName) || TypeCall(call).Function?.ReturnType.IsPseudo is true;

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
        if (TryGetCallOverloads(call, out var overloads))
        {
            var arguments = call.Arguments;
            Span<DatabaseType> types = arguments.Count <= 16 ? stackalloc DatabaseType[arguments.Count] : new DatabaseType[arguments.Count];
            try
            {
                SqlFunctionResolver.CheckArity(call.FunctionName, overloads, arguments);
                bool needed = SqlFunctionResolver.NeedsArgumentTypes(overloads, arguments);
                if (needed)
                {
                    TypeArguments(arguments, types, overloads);
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
                        TypeArguments(arguments, types, overloads);
                    }

                    typed = (function, SqlFunctionResolver.ResultType(function, types));
                }
            }
            catch (SqlEvaluationException)
            {
                typed = default;
            }
        }

        if (typed.Function is { } resolved) { RecordViewCall(call, resolved); }
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
        TypeArguments(arguments, types, overloads);
        return SqlFunctionResolver.Choose(call.FunctionName, overloads, arguments, types);
    }

    /// <summary>Types a call's arguments, each once.</summary>
    private void TypeArguments(IReadOnlyList<SqlExpression> arguments, Span<DatabaseType> types, SqlFunction[] overloads)
    {
        for (int index = 0; index < types.Length; index++)
        {
            types[index] = overloads[0] is SqlStatisticsFunction ? StatisticsInputType(arguments[index]) : StaticTypeOf(arguments[index]);
        }
    }

    // Statistics have different exact/floating overloads. The ordinary evaluator keeps
    // the first alternative's type for CASE/COALESCE; these calls need their numeric
    // common type and a matching conversion before DISTINCT or the transition runs.
    private DatabaseType StatisticsInputType(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlCaseExpression conditional)
        {
            var type = conditional.ElseResult is null ? DatabaseType.Null : StatisticsInputType(conditional.ElseResult);
            foreach (var clause in conditional.WhenClauses) { type = StatisticsAlternative(type, StatisticsInputType(clause.Result)); }
            return type;
        }
        if (expression is SqlFunctionCallExpression call && SqlStandardLibrary.IsCoalesce(call.FunctionName))
        {
            var type = DatabaseType.Null;
            foreach (var argument in call.Arguments) { type = StatisticsAlternative(type, StatisticsInputType(argument)); }
            return type;
        }
        if (expression is SqlFunctionCallExpression nullIf && nullIf.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase)
            && nullIf.Arguments.Count > 0)
        {
            return StatisticsInputType(nullIf.Arguments[0]);
        }
        return StaticTypeOf(expression);
    }

    private static DatabaseType StatisticsAlternative(DatabaseType first, DatabaseType next)
    {
        if (first == DatabaseType.Null) { return next; }
        if (next == DatabaseType.Null || first == next) { return first; }
        if (!SqlFunctionResolver.IsNumeric(first) || !SqlFunctionResolver.IsNumeric(next)) { return first; }
        if (first == DatabaseType.Float64 || next == DatabaseType.Float64) { return DatabaseType.Float64; }
        if (first == DatabaseType.Float32 || next == DatabaseType.Float32) { return DatabaseType.Float32; }
        if (first == DatabaseType.Decimal || next == DatabaseType.Decimal) { return DatabaseType.Decimal; }
        if (first == DatabaseType.Int64 || next == DatabaseType.Int64) { return DatabaseType.Int64; }
        if (first == DatabaseType.Int32 || next == DatabaseType.Int32) { return DatabaseType.Int32; }
        return first == DatabaseType.Int16 || next == DatabaseType.Int16 ? DatabaseType.Int16 : DatabaseType.Int8;
    }

    // Children have already bound and converted their own alternatives. PostgreSQL
    // parse_expr.c:1742-1768,2244-2257 likewise converts each CASE/COALESCE boundary.
    private SqlBoundExpression ConvertStatisticsAlternative(SqlExpression syntax, SqlBoundExpression value)
    {
        var type = StatisticsInputType(syntax);
        if (!SqlFunctionResolver.IsNumeric(type)) { return value; }
        var target = SqlType.ForStorage(new DatabaseTypeInfo(type));
        return new SqlBoundCast(value, target.Name, target, ResolveExpressionCasts(syntax, null, target, syntax.Location),
            _functions.Database, _defaultCollation, syntax.Location);
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
        if (ReferenceEquals(this, _rowEvaluator))
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
    [global::System.Diagnostics.DebuggerDisplay("SqlTypingMemory")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlExpressionEvaluator.SqlTypingMemory.DebugView))]
    private sealed class SqlTypingMemory
    {
        private int _subquerySlots;

        internal Dictionary<SqlFunctionCallExpression, (string Identity, SqlFunction[] Functions)>? RecordedCalls { get; set; }

        internal SqlTypingMemory(SqlExpressionEvaluator? slotScope) => SlotScope = slotScope;

        internal SqlCorrelationScope? OuterScope { get; set; }
        internal HashSet<int>? OuterDependencies { get; set; }
        internal List<SqlOuterReference>? OuterReferences { get; set; }

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

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlExpressionEvaluator.SqlTypingMemory value)
            {
                _ = value;
            }
        }
    }

    /// <summary>The arithmetic result type the evaluator computes: DECIMAL over any decimal or approximate operand, BIGINT over integers.</summary>
    internal static DatabaseType ArithmeticType(SqlBinaryOperator op, DatabaseType left, DatabaseType right)
    {
        if (SqlTemporalArithmetic.IsTemporal(left) || SqlTemporalArithmetic.IsTemporal(right))
        { return SqlTemporalArithmetic.ResultType(op, left, right); }
        static bool Inexact(DatabaseType type) => type is DatabaseType.Decimal or DatabaseType.Float32 or DatabaseType.Float64;
        static bool Integer(DatabaseType type) => type is DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64;

        if (Inexact(left) || Inexact(right))
        {
            return DatabaseType.Decimal;
        }

        return (Integer(left) || left == DatabaseType.Null) && (Integer(right) || right == DatabaseType.Null) ? DatabaseType.Int64 : DatabaseType.Null;
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
