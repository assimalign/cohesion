using System;
using System.Globalization;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;

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
    /// The walk checks the stack before it descends (#1151), as every expression walker does, so a
    /// tree the thread cannot walk fails the statement with <c>COHSQLE004</c>.
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

    /// <summary>
    /// Binds an expression whose values are compared on their own — an <c>ORDER BY</c> key, a
    /// <c>DISTINCT</c> output, a grouping key or an aggregate's operand — together with the collation
    /// they compare under.
    /// </summary>
    /// <param name="expression">The expression.</param>
    /// <param name="collation">The collation the expression's values compare under.</param>
    /// <returns>The bound expression.</returns>
    internal SqlBoundExpression Bind(SqlExpression expression, out SqlBoundCollation collation)
    {
        var bound = Bind(expression, out SqlCollationCandidate candidate);
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
    private SqlBoundExpression Bind(SqlExpression expression, out SqlCollationCandidate collation)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();

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
                return BindLogical(logical, out collation);
            case SqlBinaryExpression binary:
                return BindBinary(binary, out collation);
            case SqlUnaryExpression unary:
                return BindUnary(unary, out collation);
            case SqlIsNullExpression isNull:
                return BindIsNull(isNull, out collation);
            case SqlBetweenExpression between:
                return BindBetween(between, out collation);
            case SqlInExpression membership:
                return BindIn(membership, out collation);
            case SqlLikeExpression like:
                return BindLike(like, out collation);
            case SqlCaseExpression conditional:
                return BindCase(conditional, out collation);
            case SqlFunctionCallExpression call:
                return BindCall(call, out collation);
            case SqlCastExpression cast:
                return BindCast(cast, out collation);
            case SqlCollateExpression collate:
                return BindCollate(collate, out collation);
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
                    : NotMaterialized();
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
                return Unsupported(expression.GetType().Name);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindBinary(SqlBinaryExpression binary, out SqlCollationCandidate collation)
    {
        var left = Bind(binary.Left, out SqlCollationCandidate leftCollation);
        var right = Bind(binary.Right, out SqlCollationCandidate rightCollation);
        collation = CollationOf(binary, [leftCollation, rightCollation]);
        return new SqlBoundBinary(binary.Operator, left, right,
            SqlBoundCollation.Of(leftCollation, rightCollation, _defaultCollation));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindIsNull(SqlIsNullExpression isNull, out SqlCollationCandidate collation)
    {
        var operand = Bind(isNull.Operand, out SqlCollationCandidate operandCollation);
        collation = CollationOf(isNull, [operandCollation]);
        return new SqlBoundIsNull(operand, isNull.IsNegated);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindBetween(SqlBetweenExpression between, out SqlCollationCandidate collation)
    {
        var operand = Bind(between.Operand, out SqlCollationCandidate operandCollation);
        var low = Bind(between.Low, out SqlCollationCandidate lowCollation);
        var high = Bind(between.High, out SqlCollationCandidate highCollation);
        collation = CollationOf(between, [operandCollation, lowCollation, highCollation]);
        return new SqlBoundBetween(operand, low, high, between.IsNegated,
            SqlBoundCollation.Of(operandCollation, lowCollation, _defaultCollation),
            SqlBoundCollation.Of(operandCollation, highCollation, _defaultCollation));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindLike(SqlLikeExpression like, out SqlCollationCandidate collation)
    {
        var operand = Bind(like.Operand, out SqlCollationCandidate operandCollation);
        var pattern = Bind(like.Pattern, out SqlCollationCandidate patternCollation);
        collation = CollationOf(like, [operandCollation, patternCollation]);
        return new SqlBoundLike(operand, pattern, like.IsNegated,
            SqlBoundCollation.Of(operandCollation, patternCollation, _defaultCollation));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindCast(SqlCastExpression cast, out SqlCollationCandidate collation)
    {
        var operand = Bind(cast.Operand, out SqlCollationCandidate operandCollation);
        collation = CollationOf(cast, [operandCollation]);
        return new SqlBoundCast(operand, cast);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindCollate(SqlCollateExpression collate, out SqlCollationCandidate collation)
    {
        // COLLATE changes how a value compares, never the value: the operand stands for it.
        var operand = Bind(collate.Operand, out SqlCollationCandidate operandCollation);
        collation = CollationOf(collate, [operandCollation]);
        return operand;
    }

    /// <remarks>
    /// The literal is parsed once, here. One its type cannot hold (an integer beyond BIGINT, a
    /// numeric that DECIMAL cannot represent exactly) binds as a failure that parses it again when
    /// evaluated, so the evaluator still raises the literal's own overflow: coded at the evaluation
    /// boundary as <c>COHSQLE002</c>, and reported by an enclosing CAST as a conversion failure.
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
            return new SqlBoundColumn(ResolveColumn(column));
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
            : MissingParameter(parameter.ParameterName);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindLogical(SqlLogicalExpression logical, out SqlCollationCandidate collation)
    {
        // Every term of the chain at one level, however long the chain (#1151).
        var operands = new SqlBoundExpression[logical.Operands.Count];
        var collations = new SqlCollationCandidate[operands.Length];
        for (int index = 0; index < operands.Length; index++)
        {
            operands[index] = Bind(logical.Operands[index], out collations[index]);
        }

        collation = CollationOf(logical, collations);
        return new SqlBoundLogical(logical.Operator == SqlLogicalOperator.Or, operands);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindUnary(SqlUnaryExpression unary, out SqlCollationCandidate collation)
    {
        // The BIGINT minimum's magnitude is one past BIGINT's maximum, so its literal cannot be
        // parsed before the sign applies. A negated integer literal of exactly that magnitude is
        // read as the signed literal it spells.
        if (unary.Operator == SqlUnaryOperator.Negate
            && unary.Operand is SqlLiteralExpression { LiteralType: SqlLiteralType.Integer } magnitude
            && ulong.TryParse(magnitude.Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong digits)
            && digits == BigIntMinimumMagnitude)
        {
            collation = CollationOf(unary, [FindCollation(magnitude, bound: true)]);
            return new SqlBoundConstant(long.MinValue);
        }

        var operand = Bind(unary.Operand, out SqlCollationCandidate operandCollation);
        collation = CollationOf(unary, [operandCollation]);
        return new SqlBoundUnary(unary.Operator, operand);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindIn(SqlInExpression membership, out SqlCollationCandidate collation)
    {
        var operand = Bind(membership.Operand, out SqlCollationCandidate operandCollation);
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
                return NotMaterialized();
            }

            // Every value of one subquery carries the subquery column's collation.
            return new SqlBoundInSubquery(operand, slot.Id,
                SqlBoundCollation.Of(operandCollation, new SqlCollationCandidate(slot.Collation, 2), _defaultCollation),
                membership.IsNegated);
        }

        if (values is null)
        {
            collation = CollationOf(membership, collations);
            return MissingInValues();
        }

        var candidates = new SqlBoundExpression[values.Count];
        var comparisons = new SqlBoundCollation[values.Count];
        for (int index = 0; index < candidates.Length; index++)
        {
            candidates[index] = Bind(values[index], out collations[index + 1]);
            comparisons[index] = SqlBoundCollation.Of(operandCollation, collations[index + 1], _defaultCollation);
        }

        collation = CollationOf(membership, collations);
        return new SqlBoundIn(operand, candidates, comparisons, membership.IsNegated);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindCase(SqlCaseExpression conditional, out SqlCollationCandidate collation)
    {
        SqlBoundExpression? input = null;
        SqlCollationCandidate inputCollation = default;
        if (conditional.Input is not null)
        {
            input = Bind(conditional.Input, out inputCollation);
        }

        // CASE conditions select a result; their collation does not describe that result.
        var clauses = conditional.WhenClauses;
        var whens = new SqlBoundWhen[clauses.Count];
        var results = new SqlCollationCandidate[clauses.Count + (conditional.ElseResult is null ? 0 : 1)];
        for (int index = 0; index < whens.Length; index++)
        {
            var condition = Bind(clauses[index].Condition, out SqlCollationCandidate conditionCollation);
            var result = Bind(clauses[index].Result, out results[index]);
            whens[index] = new SqlBoundWhen(condition, result,
                input is null ? default : SqlBoundCollation.Of(inputCollation, conditionCollation, _defaultCollation));
        }

        SqlBoundExpression? elseResult = null;
        if (conditional.ElseResult is not null)
        {
            elseResult = Bind(conditional.ElseResult, out results[^1]);
        }

        collation = CollationOf(conditional, results);
        return new SqlBoundCase(input, whens, elseResult);
    }

    /// <summary>
    /// Binds a call to the overload it resolves to in the engine's function catalog: once, here,
    /// instead of for every row. A call the evaluator does not compute binds as the failure it raises
    /// when it is reached — <c>COHSQLE006</c> for arguments no overload accepts, <c>COHSQLE008</c> for
    /// arguments several accept equally well, "not supported by the executor yet" for a name outside
    /// the catalog or an aggregate outside the grouping plan that binds it to a slot — and its
    /// arguments, which never run, are not bound. <c>COALESCE</c> is a special form, evaluated lazily.
    /// An <see cref="SqlFunctionVolatility.Immutable"/> call whose arguments are all constants or
    /// parameters is folded here into its value (PostgreSQL's <c>evaluate_function</c>,
    /// <c>src/backend/optimizer/util/clauses.c</c>); one that fails is left to fail when it is reached.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindCall(SqlFunctionCallExpression call, out SqlCollationCandidate collation)
    {
        bool coalesce = SqlStandardLibrary.IsCoalesce(call.FunctionName);
        SqlFunction? function = null;
        try
        {
            if (coalesce)
            {
                CheckCoalesce(call);
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
            return UnsupportedFunction(call.FunctionName);
        }

        var arguments = new SqlBoundExpression[call.Arguments.Count];
        var collations = new SqlCollationCandidate[arguments.Length];
        for (int index = 0; index < arguments.Length; index++)
        {
            arguments[index] = Bind(call.Arguments[index], out collations[index]);
        }

        collation = CollationOf(call, collations);
        if (coalesce)
        {
            return new SqlBoundCoalesce(arguments);
        }

        var scalar = (SqlScalarFunction)function!;
        var bound = new SqlBoundCall(scalar, arguments, SqlFunctionResolver.CoercionTargets(scalar, arguments.Length),
            SqlBoundCollation.Of(collation, default, _defaultCollation), _functions.Database);
        return scalar.Volatility == SqlFunctionVolatility.Immutable && AreFixed(arguments) ? Fold(bound) : bound;
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
        foreach (var child in SqlPlanner.Children(expression))
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

    private static SqlBoundFailure NotMaterialized()
        => new(() => throw new DatabaseException("A subquery must be materialized by its plan before scalar evaluation."));

    private static SqlBoundFailure MissingInValues()
        => new(() => throw new DatabaseException("An IN query requires a value list or a subquery."));

    private static SqlBoundFailure Unsupported(string type)
        => new(() => throw new DatabaseException($"Expression '{type}' is not supported by the executor yet."));

    private static SqlBoundFailure LiteralFailure(SqlLiteralExpression literal) => new(() => EvaluateLiteral(literal));

    private SqlBoundFailure ColumnFailure(SqlColumnReferenceExpression column) => new(() => ResolveColumn(column));

    private static SqlBoundFailure MissingParameter(string parameterName)
        => new(() => throw new DatabaseException($"No value was supplied for parameter '{parameterName.TrimStart('@', '$')}'."));

    private static SqlBoundFailure ResolutionFailure(SqlEvaluationException exception) => new(() => throw exception.Duplicate());

    private static SqlBoundFailure UnsupportedFunction(string name)
        => new(() => throw new DatabaseException($"Function '{name}' is not supported by the executor yet."));
}
