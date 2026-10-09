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
            case SqlConstantExpression constant:
                collation = CollationOf(expression, []);
                return new SqlBoundConstant(constant.Value);
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
            case SqlLogicalExpression logical:
                return BindLogical(logical, out collation);
            case SqlBinaryExpression binary:
            {
                var left = Bind(binary.Left, out SqlCollationCandidate leftCollation);
                var right = Bind(binary.Right, out SqlCollationCandidate rightCollation);
                collation = CollationOf(expression, [leftCollation, rightCollation]);
                return new SqlBoundBinary(binary.Operator, left, right,
                    SqlBoundCollation.Of(leftCollation, rightCollation, _defaultCollation));
            }
            case SqlUnaryExpression unary:
                return BindUnary(unary, out collation);
            case SqlIsNullExpression isNull:
            {
                var operand = Bind(isNull.Operand, out SqlCollationCandidate operandCollation);
                collation = CollationOf(expression, [operandCollation]);
                return new SqlBoundIsNull(operand, isNull.IsNegated);
            }
            case SqlBetweenExpression between:
            {
                var operand = Bind(between.Operand, out SqlCollationCandidate operandCollation);
                var low = Bind(between.Low, out SqlCollationCandidate lowCollation);
                var high = Bind(between.High, out SqlCollationCandidate highCollation);
                collation = CollationOf(expression, [operandCollation, lowCollation, highCollation]);
                return new SqlBoundBetween(operand, low, high, between.IsNegated,
                    SqlBoundCollation.Of(operandCollation, lowCollation, _defaultCollation),
                    SqlBoundCollation.Of(operandCollation, highCollation, _defaultCollation));
            }
            case SqlInExpression membership:
                return BindIn(membership, out collation);
            case SqlLikeExpression like:
            {
                var operand = Bind(like.Operand, out SqlCollationCandidate operandCollation);
                var pattern = Bind(like.Pattern, out SqlCollationCandidate patternCollation);
                collation = CollationOf(expression, [operandCollation, patternCollation]);
                return new SqlBoundLike(operand, pattern, like.IsNegated,
                    SqlBoundCollation.Of(operandCollation, patternCollation, _defaultCollation));
            }
            case SqlCaseExpression conditional:
                return BindCase(conditional, out collation);
            case SqlFunctionCallExpression call:
                return BindCall(call, out collation);
            case SqlCastExpression cast:
            {
                var operand = Bind(cast.Operand, out SqlCollationCandidate operandCollation);
                collation = CollationOf(expression, [operandCollation]);
                return new SqlBoundCast(operand, cast);
            }
            case SqlCollateExpression collate:
            {
                // COLLATE changes how a value compares, never the value: the operand stands for it.
                var operand = Bind(collate.Operand, out SqlCollationCandidate operandCollation);
                collation = CollationOf(expression, [operandCollation]);
                return operand;
            }
            default:
                collation = CollationOf(expression, ChildCollations(expression));
                string type = expression.GetType().Name;
                return new SqlBoundFailure(() => throw new DatabaseException($"Expression '{type}' is not supported by the executor yet."));
        }
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
            return new SqlBoundFailure(() => EvaluateLiteral(literal));
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
            return new SqlBoundFailure(() => ResolveColumn(column));
        }
    }

    private SqlBoundExpression BindParameter(SqlParameterExpression parameter)
    {
        if (TryGetParameterValue(parameter, out object? value))
        {
            return new SqlBoundParameter(parameter.ParameterName, value);
        }

        string name = parameter.ParameterName.TrimStart('@', '$');
        return new SqlBoundFailure(() => throw new DatabaseException($"No value was supplied for parameter '{name}'."));
    }

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
            return new SqlBoundFailure(() => throw new DatabaseException("An IN query requires a value list or a subquery."));
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
    /// Binds a call to the function its signature resolves to: once, here, instead of for every row.
    /// A call the evaluator does not compute binds as the failure it raised when it reached the call
    /// — <c>COHSQLE006</c> for arguments the signature refuses, "not supported by the executor yet"
    /// for a declared name outside the signature table or an aggregate outside a grouping plan — and
    /// its arguments, which never ran, are not bound.
    /// </summary>
    private SqlBoundExpression BindCall(SqlFunctionCallExpression call, out SqlCollationCandidate collation)
    {
        SqlFunctionSignature? signature;
        try
        {
            signature = SqlFunctionSignatures.Resolve(call);
        }
        catch (Exception exception) when (IsDeferrable(exception))
        {
            collation = CollationOf(call, ChildCollations(call));
            return new SqlBoundFailure(() => SqlFunctionSignatures.Resolve(call));
        }

        if (signature is not { Kind: SqlFunctionKind.Scalar, Function: SqlBuiltinFunction.Coalesce or SqlBuiltinFunction.Upper
            or SqlBuiltinFunction.Lower or SqlBuiltinFunction.Length or SqlBuiltinFunction.Abs })
        {
            // A declared name outside the signature table (NULLIF, TRIM, ...), an aggregate outside
            // the grouping plan that binds it to a slot, or a scalar entry without a case here.
            collation = CollationOf(call, ChildCollations(call));
            string name = call.FunctionName;
            return new SqlBoundFailure(() => throw new DatabaseException($"Function '{name}' is not supported by the executor yet."));
        }

        var arguments = new SqlBoundExpression[call.Arguments.Count];
        var collations = new SqlCollationCandidate[arguments.Length];
        for (int index = 0; index < arguments.Length; index++)
        {
            arguments[index] = Bind(call.Arguments[index], out collations[index]);
        }

        collation = CollationOf(call, collations);
        return signature.Function == SqlBuiltinFunction.Coalesce
            ? new SqlBoundCoalesce(arguments)
            : new SqlBoundCall(signature.Function, arguments);
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
        if (expression is SqlConstantExpression { Collation: not null } constant)
        {
            return new(constant.Collation, 2);
        }
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

    private static SqlBoundFailure NotMaterialized()
        => new(() => throw new DatabaseException("A subquery must be materialized by its plan before scalar evaluation."));
}
