using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Execution.Internal;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlExpressionEvaluator
{
    /// <summary>
    /// Compiles an expression against this scope into the bound tree the evaluator walks for every
    /// row: columns become ordinals, parameters their supplied values, literals parsed values, calls
    /// the functions their signatures resolved to, subqueries the slots their plans fill, and each
    /// comparison carries the collation it uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Binding raises nothing the unbound evaluator raised only when it reached a node: such a node
    /// binds as a <see cref="SqlBoundFailure"/> that raises the same failure when evaluated, so a
    /// statement over an empty table, or one whose short-circuit skips the node, still succeeds.
    /// Missing parameters are the exception: the request contract rejects them during planning
    /// and binding, before a data row is read or written (#1102).
    /// The walk checks the stack before it descends (#1151), as every expression walker does, so a
    /// tree the thread cannot walk fails the statement with <c>CS05004</c>.
    /// </para>
    /// <para>
    /// Each node's collation candidate is computed from its operands' as the walk returns, so a
    /// statement binds in one pass however deeply its comparisons nest; the unbound evaluator walked
    /// both operand trees again for every comparison of every row.
    /// </para>
    /// </remarks>
    /// <param name="expression">The expression.</param>
    /// <returns>The bound expression.</returns>
    /// <exception cref="InsufficientExecutionStackException">The thread has too little stack left to walk the expression.</exception>
    internal SqlBoundExpression Bind(SqlExpression expression) => Bind(expression, out SqlCollationCandidate _);

    /// <summary>Binds a Boolean position, coercing only untyped Boolean string literals.</summary>
    internal SqlBoundExpression BindPredicate(SqlExpression expression)
        => Bind(expression, out SqlCollationCandidate _, requireBoolean: true);

    /// <summary>
    /// Binds an expression whose values are compared on their own — an <c>ORDER BY</c> key, a
    /// <c>DISTINCT</c> output, a grouping key or an aggregate's operand — together with the collation
    /// they compare under.
    /// </summary>
    /// <param name="expression">The expression.</param>
    /// <param name="collation">The collation the expression's values compare under.</param>
    /// <returns>The bound expression.</returns>
    internal SqlBoundExpression Bind(SqlExpression expression, out SqlBoundCollation collation)
        => Bind(expression, out collation, out _);

    /// <summary>Retains collation provenance for projected expressions without resolving deferred terms early.</summary>
    internal SqlBoundExpression Bind(SqlExpression expression, out SqlBoundCollation collation, out SqlCollationCandidate candidate)
    {
        var bound = Bind(expression, out candidate);
        collation = SqlBoundCollation.Of(candidate, default, _defaultCollation);
        return bound;
    }

    /// <summary>
    /// Resolves the collation an expression's values compare under without binding the expression:
    /// for <c>COUNT(*)</c>'s operand, which is never evaluated.
    /// </summary>
    /// <param name="expression">The expression.</param>
    /// <returns>The collation.</returns>
    internal SqlBoundCollation BindCollation(SqlExpression expression)
        => SqlBoundCollation.Of(FindCollation(expression, bound: true), default, _defaultCollation);

    /// <remarks>
    /// <para>
    /// A thin dispatcher: each level of a tree costs the walk this frame and one helper's, and the
    /// operand candidates, the collection-expression buffers and any closure live in the helpers
    /// only. With every case inline, this one recursive frame held all of them, and binding ran out
    /// of stack 10 to 23% shallower than the parser had read the same text, so a persisted CHECK
    /// that opened before could fail to open on the same thread. The helpers are kept out of line
    /// (<see cref="MethodImplOptions.NoInlining"/>) for the same reason: an inlined helper's locals
    /// would land back in this frame.
    /// </para>
    /// <para>
    /// No failure closure is created on the success path: each <see cref="SqlBoundFailure"/> is
    /// built by a factory that takes what it captures as an argument, because a lambda over a
    /// method's parameter or switch-scoped local makes the compiler allocate its closure when the
    /// method (or the switch) is entered, on every call.
    /// </para>
    /// </remarks>
    // Statistical arguments normalize numeric alternatives throughout this walk. The flag
    // stays local to binding; ordinary expressions and child query scopes keep their rules.
    private SqlBoundExpression Bind(SqlExpression expression, out SqlCollationCandidate collation, bool requireBoolean = false, bool statisticsInput = false)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();

        if (requireBoolean && TryBooleanLiteral(expression, out bool flag))
        {
            collation = default;
            return new SqlBoundConstant(flag);
        }

        // A grouping plan binds complete key expressions and aggregate calls to result slots, and an
        // ordering binds aliases and ordinals to output slots; scalar expressions compose over them.
        if (_valueOrdinals is not null && _valueOrdinals.TryGetValue(expression, out int ordinal))
        {
            collation = FindCollation(expression, bound: true);
            return new SqlBoundSlot(ordinal);
        }

        switch (expression)
        {
            case SqlLogicalExpression logical:
                return BindLogical(logical, out collation, statisticsInput);
            case SqlBinaryExpression binary:
                return BindBinary(binary, out collation, statisticsInput);
            case SqlUnaryExpression unary:
                return BindUnary(unary, out collation, statisticsInput);
            case SqlDistinctExpression distinct:
                return BindDistinct(distinct, out collation, statisticsInput);
            case SqlBooleanTestExpression test:
                return BindBooleanTest(test, out collation, statisticsInput);
            case SqlIsNullExpression isNull:
                return BindIsNull(isNull, out collation, statisticsInput);
            case SqlBetweenExpression between:
                return BindBetween(between, out collation, statisticsInput);
            case SqlQuantifiedComparisonExpression quantified:
                return BindQuantifiedComparison(quantified, out collation, statisticsInput);
            case SqlInExpression membership:
                return BindIn(membership, out collation, statisticsInput);
            case SqlLikeExpression like:
                return BindLike(like, out collation, statisticsInput);
            case SqlCaseExpression conditional:
                var boundCase = BindCase(conditional, out collation, requireBoolean, statisticsInput);
                return statisticsInput ? ConvertStatisticsAlternative(conditional, boundCase) : boundCase;
            case SqlFunctionCallExpression call:
                var boundCall = BindCall(call, out collation, requireBoolean, statisticsInput);
                return statisticsInput && SqlStandardLibrary.IsCoalesce(call.FunctionName)
                    ? ConvertStatisticsAlternative(call, boundCall) : boundCall;
            case SqlCastExpression cast:
                return BindCast(cast, out collation, statisticsInput);
            case SqlCollateExpression collate:
                return BindCollate(collate, out collation, statisticsInput);
            default:
                return BindLeaf(expression, out collation);
        }
    }

    /// <summary>Binds a node without operands to bind, or one the executor does not evaluate.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindLeaf(SqlExpression expression, out SqlCollationCandidate collation)
    {
        switch (expression)
        {
            case SqlSubqueryExpression or SqlExistsExpression:
                collation = CollationOf(expression, []);
                return _subquerySlots is not null && _subquerySlots.TryGetValue(expression, out var slot)
                    ? new SqlBoundSubquery(slot.Id)
                    : NotMaterialized(expression.Location);
            case SqlLiteralExpression literal:
                collation = CollationOf(expression, []);
                return BindLiteral(literal);
            case SqlColumnReferenceExpression column:
                collation = CollationOf(expression, []);
                return BindColumn(column);
            case SqlParameterExpression parameter:
                collation = CollationOf(expression, []);
                return BindParameter(parameter);
            default:
                collation = CollationOf(expression, ChildCollations(expression));
                return Unsupported(expression.GetType().Name, expression.Location);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindBinary(SqlBinaryExpression binary, out SqlCollationCandidate collation, bool statisticsInput)
    {
        var left = Bind(binary.Left, out SqlCollationCandidate leftCollation, statisticsInput: statisticsInput);
        var right = Bind(binary.Right, out SqlCollationCandidate rightCollation, statisticsInput: statisticsInput);
        if (binary.Operator is SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual or SqlBinaryOperator.LessThan
            or SqlBinaryOperator.LessOrEqual or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterOrEqual)
        {
            left = BindCharacterComparand(binary.Left, left, binary.Right);
            right = BindCharacterComparand(binary.Right, right, binary.Left);
        }
        if (binary.Operator == SqlBinaryOperator.Concat)
        {
            // JSONB shares byte[] with BYTEA; retain its declared identity for text output.
            left = JsonBinaryToText(binary.Left, left);
            right = JsonBinaryToText(binary.Right, right);
        }
        collation = CollationOf(binary, [leftCollation, rightCollation]);
        return new SqlBoundBinary(binary.Operator, left, right,
            SqlBoundCollation.Of(leftCollation, rightCollation, _defaultCollation), binary.Location);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindCharacterComparand(SqlExpression syntax, SqlBoundExpression bound, SqlExpression other)
    {
        return BindCharacterComparand(syntax, bound, StaticTypeOf(other));
    }

    private static SqlBoundExpression BindCharacterComparand(SqlExpression syntax, SqlBoundExpression bound, DatabaseType otherType)
    {
        if (otherType != DatabaseType.Character) { return bound; }
        while (syntax is SqlCollateExpression collate) { syntax = collate.Operand; }
        // Untyped literals/parameters take the CHAR operand's identity without length coercion.
        return (syntax, bound) switch
        {
            (SqlLiteralExpression { LiteralType: SqlLiteralType.String }, SqlBoundConstant { Value: string text })
                => new SqlBoundConstant(new SqlCharacter(text)),
            (SqlParameterExpression, SqlBoundParameter { Value: string text } parameter)
                => new SqlBoundParameter(parameter.Name, new SqlCharacter(text)),
            _ => bound,
        };
    }

    private SqlBoundExpression JsonBinaryToText(SqlExpression syntax, SqlBoundExpression value)
        => StaticTypeOf(syntax) == DatabaseType.JsonBinary
            ? new SqlBoundCast(value, "TEXT", SqlType.Text, _functions.Types.ResolveCasts(null, SqlType.Text),
                _functions.Database, _defaultCollation, sourceType: DatabaseType.JsonBinary)
            : value;

    private SqlBoundExpression CharacterToText(SqlExpression syntax, SqlBoundExpression value)
        => StaticTypeOf(syntax) == DatabaseType.Character
            ? new SqlBoundCast(value, "TEXT", SqlType.Text, _functions.Types.ResolveCasts(null, SqlType.Text), _functions.Database, _defaultCollation)
            : value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindDistinct(SqlDistinctExpression distinct, out SqlCollationCandidate collation, bool statisticsInput)
    {
        var left = Bind(distinct.Left, out SqlCollationCandidate leftCollation, statisticsInput: statisticsInput);
        var right = Bind(distinct.Right, out SqlCollationCandidate rightCollation, statisticsInput: statisticsInput);
        left = BindCharacterComparand(distinct.Left, left, distinct.Right);
        right = BindCharacterComparand(distinct.Right, right, distinct.Left);
        collation = CollationOf(distinct, [leftCollation, rightCollation]);
        return new SqlBoundDistinct(left, right, distinct.IsNegated,
            SqlBoundCollation.Of(leftCollation, rightCollation, _defaultCollation), distinct.Location);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindBooleanTest(SqlBooleanTestExpression test, out SqlCollationCandidate collation, bool statisticsInput)
    {
        var operand = Bind(test.Operand, out SqlCollationCandidate operandCollation, requireBoolean: true, statisticsInput: statisticsInput);
        var type = StaticTypeOf(test.Operand);
        if (type is not (DatabaseType.Boolean or DatabaseType.Null) && !TryBooleanLiteral(test.Operand, out _)
            && operand is not (SqlBoundCase { ResultType: DatabaseType.Boolean } or SqlBoundCoalesce { ResultType: DatabaseType.Boolean }))
        {
            throw new SqlPlanningException("CS02003", ErrorMessages.BooleanTestOperand, test.Location);
        }
        collation = CollationOf(test, [operandCollation]);
        return new SqlBoundBooleanTest(operand, test.TruthValue, test.IsNegated, test.Location);
    }

    private SqlBoundExpression BindIsNull(SqlIsNullExpression isNull, out SqlCollationCandidate collation, bool statisticsInput)
    {
        var operand = Bind(isNull.Operand, out SqlCollationCandidate operandCollation, statisticsInput: statisticsInput);
        collation = CollationOf(isNull, [operandCollation]);
        return new SqlBoundIsNull(operand, isNull.IsNegated);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindBetween(SqlBetweenExpression between, out SqlCollationCandidate collation, bool statisticsInput)
    {
        var operand = Bind(between.Operand, out SqlCollationCandidate operandCollation, statisticsInput: statisticsInput);
        var low = Bind(between.Low, out SqlCollationCandidate lowCollation, statisticsInput: statisticsInput);
        var high = Bind(between.High, out SqlCollationCandidate highCollation, statisticsInput: statisticsInput);
        // PostgreSQL parse_expr.c:1325-1333 binds the two comparisons separately.
        // Neither bound may change the value used to compare against the other.
        var lowOperand = BindCharacterComparand(between.Operand, operand, between.Low);
        var highOperand = BindCharacterComparand(between.Operand, operand, between.High);
        low = BindCharacterComparand(between.Low, low, between.Operand);
        high = BindCharacterComparand(between.High, high, between.Operand);
        collation = CollationOf(between, [operandCollation, lowCollation, highCollation]);
        return new SqlBoundBetween(lowOperand, highOperand, low, high, between.IsNegated,
            SqlBoundCollation.Of(operandCollation, lowCollation, _defaultCollation),
            SqlBoundCollation.Of(operandCollation, highCollation, _defaultCollation));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindLike(SqlLikeExpression like, out SqlCollationCandidate collation, bool statisticsInput)
    {
        var operand = Bind(like.Operand, out SqlCollationCandidate operandCollation, statisticsInput: statisticsInput);
        var pattern = Bind(like.Pattern, out SqlCollationCandidate patternCollation, statisticsInput: statisticsInput);
        SqlCollationCandidate escapeCollation = default;
        var escape = like.Escape is null ? new SqlBoundConstant("\\") : Bind(like.Escape, out escapeCollation, statisticsInput: statisticsInput);
        collation = CollationOf(like, [operandCollation, patternCollation, escapeCollation]);
        var effective = SqlBoundCollation.Of(operandCollation,
            SqlCollationCandidate.Fold(patternCollation, escapeCollation), _defaultCollation);
        // Immutable constants (including parameters) compile once, and invalid constants fail
        // before rows are read, including an empty table. NULL ESCAPE remains UNKNOWN.
        SqlLikePattern? compiled = null;
        try
        {
            if (TryLikeConstant(escape, out var escapeValue) && escapeValue is not null)
            {
                SqlLikePattern.ValidateEscape(LikeText(escapeValue));
                if (TryLikeConstant(pattern, out var patternValue) && patternValue is not null)
                {
                    compiled = SqlLikePattern.Compile(LikeText(patternValue), LikeText(escapeValue), _cancellationToken);
                    if (effective.IsFixed) { compiled = compiled.Normalize(effective.Resolve(null)); }
                }
            }
        }
        catch (SqlPlanningException exception) when (exception.Diagnostics[0].Start is null && like.Location is not null)
        {
            throw exception.At(like.Location);
        }
        return new SqlBoundLike(operand, pattern, escape, like.IsNegated, effective, compiled, like.Location);
    }

    private static bool TryLikeConstant(SqlBoundExpression expression, out object? value)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            case SqlBoundConstant constant: value = constant.Value; return true;
            case SqlBoundParameter parameter: value = parameter.Value; return true;
            case SqlBoundBinary { Operator: SqlBinaryOperator.Concat } concat
                when TryLikeConstant(concat.Left, out var left) && TryLikeConstant(concat.Right, out var right):
                value = left is null || right is null ? null : SqlCastText.Format(left) + SqlCastText.Format(right);
                return true;
            default: value = null; return false;
        }
    }

    private static string LikeText(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    /// <remarks>
    /// The target resolves once, here: a built-in type the parser resolved, or a domain of the engine's
    /// type catalog (phase E3); a name the catalog does not know binds as the failure it raises when
    /// evaluated (the planner reports it while planning first). The casts of the registry the CAST may
    /// apply are chosen here too, by the storage type of the value they convert, so a row looks up
    /// nothing: a cast from the operand's domain, when the plan knows it, before a cast from its storage
    /// type, and for a domain target a cast to its base type when none to the domain is registered.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindCast(SqlCastExpression cast, out SqlCollationCandidate collation, bool statisticsInput)
    {
        var operand = Bind(cast.Operand, out SqlCollationCandidate operandCollation, statisticsInput: statisticsInput);
        collation = CollationOf(cast, [operandCollation]);
        SqlType target;
        try
        {
            target = SqlPlanner.ResolveCastTarget(cast, _functions.Types);
        }
        catch (Exception exception) when (IsDeferrable(exception))
        {
            return CastTargetFailure(cast, _functions.Types);
        }

        return new SqlBoundCast(operand, cast.TargetType, target, _functions.Types.ResolveCasts(SourceDomain(cast.Operand), target),
            _functions.Database, _defaultCollation, cast.Location, StaticTypeOf(cast.Operand));
    }

    /// <summary>
    /// The domain a CAST's operand is known to be of while planning: a column of a domain, or a CAST to
    /// one, under any COLLATE; null for any other operand, whose casts are its storage type's. An
    /// expression over a domain's value (<c>COALESCE</c>, <c>CASE</c>, arithmetic) has the base type,
    /// so a cast from the domain does not apply to it: the dialect documents the shape.
    /// </summary>
    internal SqlType? SourceDomain(SqlExpression operand)
    {
        while (operand is SqlCollateExpression collate)
        {
            operand = collate.Operand;
        }

        switch (operand)
        {
            case SqlColumnReferenceExpression column:
                try
                {
                    return ColumnMetadata(column).DomainName is { } name && _functions.Types.TryGetDomain(name, out var domain)
                        ? domain
                        : null;
                }
                catch (DatabaseException)
                {
                    return null;
                }
            case SqlCastExpression inner:
                return TryResolveCastTarget(inner) is { IsDomain: true } target ? target : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Resolves a CAST's target for the planner's own questions (a static type, a source domain),
    /// without raising: null when the target does not resolve, which planning reports on its own.
    /// </summary>
    /// <param name="cast">The CAST.</param>
    /// <returns>The target type, or null.</returns>
    internal SqlType? TryResolveCastTarget(SqlCastExpression cast)
    {
        try
        {
            return SqlPlanner.ResolveCastTarget(cast, _functions.Types);
        }
        catch (DatabaseException)
        {
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindCollate(SqlCollateExpression collate, out SqlCollationCandidate collation, bool statisticsInput)
    {
        // COLLATE changes how a value compares, never the value: the operand stands for it.
        var operand = Bind(collate.Operand, out SqlCollationCandidate operandCollation, statisticsInput: statisticsInput);
        collation = CollationOf(collate, [operandCollation]);
        return operand;
    }

    /// <remarks>
    /// The literal is parsed once, here. One its type cannot hold (an integer beyond BIGINT, a
    /// numeric that DECIMAL cannot represent exactly) binds as a failure that parses it again when
    /// evaluated, so the evaluator still raises the literal's own overflow: coded at the evaluation
    /// boundary as <c>CS02002</c>, and reported by an enclosing CAST as a conversion failure.
    /// </remarks>
    private static SqlBoundExpression BindLiteral(SqlLiteralExpression literal)
    {
        if (literal.LiteralType == SqlLiteralType.Null)
        {
            return SqlBoundConstant.Null;
        }

        try
        {
            return new SqlBoundConstant(EvaluateLiteral(literal));
        }
        catch (Exception exception) when (IsDeferrable(exception))
        {
            return LiteralFailure(literal);
        }
    }

    private SqlBoundExpression BindColumn(SqlColumnReferenceExpression column)
    {
        try
        {
            int ordinal = ResolveColumn(column);
            if (ordinal < 0) { return new SqlBoundOuterColumn(OuterReference(ordinal)); }
            return column.TableAlias is null && column.SchemaName is null && MergedColumn(ordinal) is { } merged
                ? merged.Value : new SqlBoundColumn(ordinal);
        }
        catch (Exception exception) when (IsDeferrable(exception))
        {
            // Planning validates every column first; a scope that did not reports it when evaluated.
            return ColumnFailure(column);
        }
    }

    private SqlBoundExpression BindParameter(SqlParameterExpression parameter)
        => TryGetParameterValue(parameter, out object? value)
            ? new SqlBoundParameter(parameter.ParameterName, value)
            : MissingParameter(parameter.ParameterName, parameter.Location);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindLogical(SqlLogicalExpression logical, out SqlCollationCandidate collation, bool statisticsInput)
    {
        // Every term of the chain at one level, however long the chain (#1151).
        var operands = new SqlBoundExpression[logical.Operands.Count];
        var collations = new SqlCollationCandidate[operands.Length];
        for (int index = 0; index < operands.Length; index++)
        {
            operands[index] = Bind(logical.Operands[index], out collations[index], requireBoolean: true, statisticsInput: statisticsInput);
        }

        collation = CollationOf(logical, collations);
        return new SqlBoundLogical(logical.Operator == SqlLogicalOperator.Or, operands, logical.Location);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindUnary(SqlUnaryExpression unary, out SqlCollationCandidate collation, bool statisticsInput)
    {
        // The BIGINT minimum's magnitude is one past BIGINT's maximum, so its literal cannot be
        // parsed before the sign applies. A negated integer literal of exactly that magnitude is
        // read as the signed literal it spells.
        if (unary.Operator == SqlUnaryOperator.Negate
            && unary.Operand is SqlLiteralExpression { LiteralType: SqlLiteralType.Integer } magnitude
            && ulong.TryParse(magnitude.Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong digits)
            && digits == bigIntMinimumMagnitude)
        {
            collation = CollationOf(unary, [FindCollation(magnitude, bound: true)]);
            return new SqlBoundConstant(long.MinValue);
        }

        var operand = Bind(unary.Operand, out SqlCollationCandidate operandCollation, requireBoolean: unary.Operator == SqlUnaryOperator.Not, statisticsInput: statisticsInput);
        collation = CollationOf(unary, [operandCollation]);
        var bound = new SqlBoundUnary(unary.Operator, operand, unary.Location);

        // A sign over a constant is a constant, as PostgreSQL's grammar reads "-5" as the constant
        // -5 (doNegate, src/backend/parser/gram.y): computed once here, so an immutable call over it
        // folds too (ABS(-5)). One that fails is kept, and fails only when a row reaches it, as before.
        return _foldConstants && (unary.Operator is SqlUnaryOperator.Negate or SqlUnaryOperator.Plus) && operand.Kind == SqlBoundExpressionKind.Constant
            ? FoldSign(bound)
            : bound;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression FoldSign(SqlBoundUnary sign)
    {
        try
        {
            return new SqlBoundConstant(Evaluate(sign, []));
        }
        catch (Exception exception) when (IsDeferrable(exception))
        {
            return sign;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindQuantifiedComparison(SqlQuantifiedComparisonExpression expression,
        out SqlCollationCandidate collation, bool statisticsInput)
    {
        var operand = Bind(expression.Operand, out SqlCollationCandidate operandCollation, statisticsInput: statisticsInput);
        collation = default; // The predicate produces BOOLEAN; collation belongs to its comparison.
        if (_subquerySlots is null || !_subquerySlots.TryGetValue(expression, out var slot))
        {
            return NotMaterialized(expression.Location);
        }
        operand = BindCharacterComparand(expression.Operand, operand, slot.Type);
        return new SqlBoundQuantifiedComparison(operand, slot.Id, expression.Operator,
            expression.Quantifier == SqlComparisonQuantifier.All,
            SqlBoundCollation.Of(operandCollation, new SqlCollationCandidate(slot.Collation, 2), _defaultCollation),
            expression.Location);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindIn(SqlInExpression membership, out SqlCollationCandidate collation, bool statisticsInput)
    {
        var operand = Bind(membership.Operand, out SqlCollationCandidate operandCollation, statisticsInput: statisticsInput);
        var values = membership.Values;
        var collations = new SqlCollationCandidate[1 + (values?.Count ?? 0)];
        collations[0] = operandCollation;

        // A subquery contributes its whole result set as the candidate list. A value list beside it
        // never ran, so it is not bound; it only takes part in the node's collation, as it did.
        if (membership.Subquery is not null)
        {
            for (int index = 0; index < (values?.Count ?? 0); index++)
            {
                collations[index + 1] = FindCollation(values![index], bound: true);
            }

            collation = CollationOf(membership, collations);
            if (_subquerySlots is null || !_subquerySlots.TryGetValue(membership, out var slot))
            {
                return NotMaterialized(membership.Location);
            }

            operand = BindCharacterComparand(membership.Operand, operand, slot.Type);
            // Every value of one subquery carries the subquery column's collation.
            return new SqlBoundInSubquery(operand, slot.Id,
                SqlBoundCollation.Of(operandCollation, new SqlCollationCandidate(slot.Collation, 2), _defaultCollation),
                membership.IsNegated);
        }

        if (values is null)
        {
            collation = CollationOf(membership, collations);
            return MissingInValues(membership.Location);
        }

        var candidates = new SqlBoundExpression[values.Count];
        var comparisons = new SqlBoundCollation[values.Count];
        var referencesColumn = new bool[values.Count];
        int nonColumnCount = 0;
        DatabaseType comparisonType = InComparisonType(membership.Operand);
        for (int index = 0; index < candidates.Length; index++)
        {
            candidates[index] = Bind(values[index], out collations[index + 1], statisticsInput: statisticsInput);
            comparisons[index] = SqlBoundCollation.Of(operandCollation, collations[index + 1], _defaultCollation);
            referencesColumn[index] = ReferencesTableColumn(values[index]);
            if (!referencesColumn[index])
            {
                nonColumnCount++;
                // PostgreSQL parse_coerce.c:1397-1456, pg_cast.dat:313-325: the string
                // casts are implicit both ways, so the first non-unknown type wins.
                if (comparisonType == DatabaseType.Null) { comparisonType = InComparisonType(values[index]); }
            }
        }

        collation = CollationOf(membership, collations);
        if (comparisonType == DatabaseType.Null) { comparisonType = DatabaseType.String; }
        if (nonColumnCount == candidates.Length && nonColumnCount != 1)
        {
            return new SqlBoundIn(operand, candidates, comparisons, comparisonType, membership.IsNegated);
        }

        // PostgreSQL parse_expr.c:1163-1176 groups only non-Var candidates, and only
        // when there are at least two. Each remaining comparison binds independently
        // (:1273-1279), just as the corresponding = OR = / <> AND <> expression does.
        var terms = new List<SqlBoundExpression>();
        if (nonColumnCount > 1)
        {
            var commonCandidates = new SqlBoundExpression[nonColumnCount];
            var commonCollations = new SqlBoundCollation[nonColumnCount];
            int commonIndex = 0;
            for (int index = 0; index < candidates.Length; index++)
            {
                if (referencesColumn[index]) { continue; }
                commonCandidates[commonIndex] = candidates[index];
                commonCollations[commonIndex++] = comparisons[index];
            }
            terms.Add(new SqlBoundIn(operand, commonCandidates, commonCollations, comparisonType, membership.IsNegated));
        }
        for (int index = 0; index < candidates.Length; index++)
        {
            if (nonColumnCount > 1 && !referencesColumn[index]) { continue; }
            var left = BindCharacterComparand(membership.Operand, operand, values[index]);
            var right = BindCharacterComparand(values[index], candidates[index], membership.Operand);
            terms.Add(new SqlBoundBinary(membership.IsNegated ? SqlBinaryOperator.NotEqual : SqlBinaryOperator.Equal,
                left, right, comparisons[index]));
        }
        return terms.Count == 1 ? terms[0] : new SqlBoundLogical(!membership.IsNegated, terms.ToArray());
    }

    private DatabaseType InComparisonType(SqlExpression expression)
    {
        while (expression is SqlCollateExpression collate) { expression = collate.Operand; }
        var type = StaticTypeOf(expression);
        return type == DatabaseType.String && expression is SqlLiteralExpression or SqlParameterExpression
            ? DatabaseType.Null : type;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindCase(SqlCaseExpression conditional, out SqlCollationCandidate collation, bool requireBoolean, bool statisticsInput)
    {
        SqlBoundExpression? input = null;
        SqlCollationCandidate inputCollation = default;
        if (conditional.Input is not null)
        {
            input = Bind(conditional.Input, out inputCollation, statisticsInput: statisticsInput);
        }

        // CASE conditions select a result; their collation does not describe that result.
        bool textResult = !requireBoolean && StaticTypeOf(conditional) == DatabaseType.String;
        var clauses = conditional.WhenClauses;
        var whens = new SqlBoundWhen[clauses.Count];
        var results = new SqlCollationCandidate[clauses.Count + (conditional.ElseResult is null ? 0 : 1)];
        for (int index = 0; index < whens.Length; index++)
        {
            var condition = Bind(clauses[index].Condition, out SqlCollationCandidate conditionCollation, requireBoolean: input is null, statisticsInput: statisticsInput);
            if (conditional.Input is not null)
            {
                condition = BindCharacterComparand(clauses[index].Condition, condition, conditional.Input);
            }
            var result = Bind(clauses[index].Result, out results[index], requireBoolean, statisticsInput: statisticsInput);
            if (textResult) { result = CharacterToText(clauses[index].Result, result); }
            whens[index] = new SqlBoundWhen(condition, result,
                input is null ? default : SqlBoundCollation.Of(inputCollation, conditionCollation, _defaultCollation), clauses[index].Condition.Location);
        }

        SqlBoundExpression? elseResult = null;
        if (conditional.ElseResult is not null)
        {
            elseResult = Bind(conditional.ElseResult, out results[^1], requireBoolean, statisticsInput: statisticsInput);
            if (textResult) { elseResult = CharacterToText(conditional.ElseResult, elseResult); }
        }

        collation = StaticTypeOf(conditional) is DatabaseType.String or DatabaseType.Character
            ? CollationOf(conditional, results) : default;
        return new SqlBoundCase(input, whens, elseResult, requireBoolean ? DatabaseType.Boolean : StaticTypeOf(conditional));
    }

    /// <summary>
    /// Binds a call to the overload it resolves to in the engine's function catalog: once, here,
    /// instead of for every row. A call the evaluator does not compute binds as the failure it raises
    /// when it is reached — <c>CS01006</c> for arguments no overload accepts, <c>CS01008</c> for
    /// arguments several accept equally well, "not supported by the executor yet" for a name outside
    /// the catalog or an aggregate outside the grouping plan that binds it to a slot — and its
    /// arguments, which never run, are not bound. <c>COALESCE</c> is a special form, evaluated lazily.
    /// An <see cref="SqlFunctionVolatility.Immutable"/> call whose arguments are all constants or
    /// parameters is folded here into its value using this binding operation's token (PostgreSQL's <c>evaluate_function</c>,
    /// <c>src/backend/optimizer/util/clauses.c</c>); one that fails is left to fail when it is reached.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindCall(SqlFunctionCallExpression call, out SqlCollationCandidate collation, bool requireBoolean, bool statisticsInput)
    {
        if (SqlStandardLibrary.IsGrouping(call.FunctionName) && OuterGrouping(call) is { } grouping)
        {
            collation = default;
            return grouping;
        }
        if (SqlStandardLibrary.IsEvaluatorForm(call.FunctionName) && !SqlStandardLibrary.IsCoalesce(call.FunctionName))
        {
            ValidateEvaluatorForm(call);
            var operands = BindArguments(call.Arguments, out var specialCollation, statisticsInput);
            collation = CollationOf(call, ChildCollations(call));
            // NULLIF uses equality's CHAR identity for untyped text, while returning the
            // original first value. PostgreSQL: src/backend/parser/parse_expr.c:1083.
            bool nullIf = call.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase);
            bool firstCharacter = nullIf && !ReferenceEquals(operands[0], BindCharacterComparand(call.Arguments[0], operands[0], call.Arguments[1]));
            bool secondCharacter = nullIf && !ReferenceEquals(operands[1], BindCharacterComparand(call.Arguments[1], operands[1], call.Arguments[0]));
            return new SqlBoundSpecialForm(call.FunctionName.ToUpperInvariant(), operands, specialCollation, firstCharacter, secondCharacter);
        }
        bool coalesce = SqlStandardLibrary.IsCoalesce(call.FunctionName);
        SqlFunction? function = null;
        try
        {
            if (coalesce)
            {
                CheckEvaluatorForm(call);
            }
            else
            {
                function = ResolveFunction(call);
            }
        }
        catch (SqlEvaluationException exception)
        {
            collation = CollationOf(call, ChildCollations(call));
            return ResolutionFailure(exception);
        }

        if (!coalesce && function is not SqlScalarFunction)
        {
            // A name outside the catalog (a declared name that does not execute yet, such as NULLIF,
            // or an unknown name in a tree that was never planned), or an aggregate outside the
            // grouping plan that binds it to a slot.
            collation = CollationOf(call, ChildCollations(call));
            return UnsupportedFunction(call.FunctionName, call.Location);
        }

        var arguments = new SqlBoundExpression[call.Arguments.Count];
        var collations = new SqlCollationCandidate[arguments.Length];
        for (int index = 0; index < arguments.Length; index++)
        {
            arguments[index] = Bind(call.Arguments[index], out collations[index], requireBoolean && coalesce, statisticsInput: statisticsInput);
        }

        collation = CollationOf(call, collations);
        if (coalesce)
        {
            if (!requireBoolean && StaticCallType(call) == DatabaseType.String)
            {
                for (int index = 0; index < arguments.Length; index++)
                {
                    arguments[index] = CharacterToText(call.Arguments[index], arguments[index]);
                }
            }
            if (StaticCallType(call) is not (DatabaseType.String or DatabaseType.Character)) { collation = default; }
            return new SqlBoundCoalesce(arguments, requireBoolean ? DatabaseType.Boolean : StaticCallType(call));
        }

        var scalar = (SqlScalarFunction)function!;
        bool fixedArguments = AreFixed(arguments);
        var callCollation = SqlBoundCollation.Of(collation, default, _defaultCollation);
        BindParameterCoercions(scalar, arguments, callCollation);
        var bound = new SqlBoundCall(scalar, arguments, SqlFunctionResolver.CoercionTargets(scalar, arguments.Length),
            callCollation, _functions.Database, _cancellationToken, _functions.TransactionStart);
        return _foldConstants && scalar.Volatility == SqlFunctionVolatility.Immutable && fixedArguments ? Fold(bound) : bound;
    }

    /// <summary>Applies declared CHAR bounds and domain checks before scalar or aggregate STRICT dispatch.</summary>
    internal void BindParameterCoercions(SqlFunction function, SqlBoundExpression[] arguments, SqlBoundCollation collation)
    {
        for (int index = 0; index < arguments.Length; index++)
        {
            var parameter = function.ParameterAt(index);
            if (parameter.IsDomain || parameter.Storage.Type == DatabaseType.Character)
            {
                arguments[index] = new SqlBoundDomainArgument(arguments[index], parameter, function.Name,
                    collation, _functions.Database, _cancellationToken);
            }
        }
    }

    /// <summary>Whether every argument has a value fixed when the statement was bound: a constant or a parameter.</summary>
    private static bool AreFixed(SqlBoundExpression[] arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument.Kind is not (SqlBoundExpressionKind.Constant or SqlBoundExpressionKind.Parameter))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Computes an immutable call over fixed arguments once, so no row calls it again. A call that
    /// fails is kept, and fails, as before folding existed, only when a row reaches it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression Fold(SqlBoundCall call)
    {
        try
        {
            return new SqlBoundConstant(EvaluateCall(call, []));
        }
        catch (Exception exception) when (IsDeferrable(exception))
        {
            return call;
        }
    }

    /// <summary>The collation candidates of a node's operands, in operand order, for a node whose operands are not bound.</summary>
    private SqlCollationCandidate[] ChildCollations(SqlExpression expression)
    {
        var collations = new System.Collections.Generic.List<SqlCollationCandidate>();
        foreach (var child in expression is SqlFunctionCallExpression call
            ? call.FunctionName.Equals("STRING_AGG", StringComparison.OrdinalIgnoreCase) || call.FunctionName.Equals("LISTAGG", StringComparison.OrdinalIgnoreCase)
                ? call.Arguments.Take(1) : call.Arguments
            : SqlPlanner.Children(expression))
        {
            collations.Add(FindCollation(child, bound: true));
        }

        return collations.ToArray();
    }

    /// <summary>
    /// The collation candidate a node contributes to a comparison, from its operands' candidates:
    /// <see cref="FindCollation"/>'s rules for a bound expression, with the operands' already
    /// computed rather than walked again.
    /// </summary>
    /// <param name="expression">The node.</param>
    /// <param name="operands">
    /// The candidates of the node's operands in operand order; for a <c>CASE</c>, of its results and
    /// <c>ELSE</c> only.
    /// </param>
    private SqlCollationCandidate CollationOf(SqlExpression expression, ReadOnlySpan<SqlCollationCandidate> operands)
    {
        if (expression is SqlSubqueryExpression or SqlExistsExpression or SqlInExpression { Subquery: not null }
            && _subquerySlots is not null && _subquerySlots.TryGetValue(expression, out var slot))
        {
            return slot.Kind == SqlSubqueryKind.Set
                ? SqlCollationCandidate.Deferred(new SqlCollationSubqueryTerm(slot.Id, slot.Collation, CollationOfCore(expression, operands)))
                : SubqueryCollation(slot, default);
        }

        return CollationOfCore(expression, operands);
    }

    private SqlCollationCandidate CollationOfCore(SqlExpression expression, ReadOnlySpan<SqlCollationCandidate> operands)
    {
        if (_projectionSources is not null && _projectionSources.TryGetValue(expression, out var source))
        {
            return source.ColumnOrdinal is int ordinal ? ColumnCollation(ordinal) : FindCollation(source.Expression, bound: true);
        }
        if (expression is SqlCollateExpression collate)
        {
            return CollateCollation(operands[0], collate.CollationName, bound: true);
        }
        if (expression is SqlColumnReferenceExpression column)
        {
            return ColumnReferenceCollation(column, bound: true);
        }

        return SqlCollationCandidate.Fold(operands);
    }

    // The failures below are built only where binding defers one, never on the success path: each
    // closure captures its factory's argument, so it is allocated when the factory runs.

    private static SqlBoundFailure NotMaterialized(Location? location)
        => new(() => throw new SqlPlanningException("CS05013", ErrorMessages.EvaluatorNotMaterializedSqlPlanning, location));

    private static SqlBoundFailure MissingInValues(Location? location)
        => new(() => throw new SqlPlanningException("CS05013", ErrorMessages.EvaluatorMissingInValuesSqlPlanning, location));

    private static SqlBoundFailure Unsupported(string type, Location? location)
        => new(() => throw new SqlPlanningException("CS05013", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EvaluatorUnsupportedSqlPlanning, type), location));

    private static SqlBoundFailure LiteralFailure(SqlLiteralExpression literal) => new(() => EvaluateLiteral(literal));

    private SqlBoundFailure ColumnFailure(SqlColumnReferenceExpression column) => new(() => ResolveColumn(column));

    private static SqlBoundExpression MissingParameter(string parameterName, Location? location)
        => throw new SqlPlanningException("CS01020", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EvaluatorMissingParameterSqlPlanning, ParameterBindings.BareName(parameterName)), location);

    private static SqlBoundFailure ResolutionFailure(SqlEvaluationException exception) => new(() => throw exception.Duplicate());

    private static SqlBoundFailure UnsupportedFunction(string name, Location? location)
        => new(() => throw new SqlPlanningException("CK00001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.UnsupportedFunctionSqlPlanning, name), location));

    private static SqlBoundFailure CastTargetFailure(SqlCastExpression cast, SqlTypeCatalog types)
        => new(() => SqlPlanner.ResolveCastTarget(cast, types));
}
