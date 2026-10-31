using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Strong implication: only TRUE restrictions may prove a TRUE index predicate.</summary>
internal static class SqlPredicateImplication
{
    // PostgreSQL optimizer/util/predtest.c:118-181 distinguishes this from CHECK's
    // non-false implication. No contradiction, parameter, OR or NOT reasoning occurs here.
    internal static IReadOnlyList<SqlBoundExpression>? Prove(SqlBoundExpression predicate,
        IReadOnlyList<SqlBoundExpression> restrictions, int offset = 0, CancellationToken cancellationToken = default)
    {
        var proof = new List<SqlBoundExpression>();
        foreach (var required in Conjuncts(predicate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SqlBoundExpression? witness = null;
            foreach (var restriction in restrictions)
            {
                foreach (var term in Conjuncts(restriction))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Implies(term, required, offset)) { witness = term; break; }
                }
                if (witness is not null) { break; }
            }
            if (witness is null) { return null; }
            if (!proof.Contains(witness)) { proof.Add(witness); }
        }
        return proof;
    }

    internal static IEnumerable<SqlBoundExpression> Conjuncts(SqlBoundExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlBoundLogical { IsOr: false } logical)
        {
            foreach (var operand in logical.Operands)
            { foreach (var term in Conjuncts(operand)) { yield return term; } }
        }
        else { yield return expression; }
    }

    private static bool Implies(SqlBoundExpression query, SqlBoundExpression predicate, int offset)
    {
        if (Same(query, predicate, offset)) { return true; }
        if (predicate is SqlBoundIsNull { IsNegated: true, Operand: SqlBoundColumn notNull } &&
            Comparison(query, out _, out _, out _, out _) && RejectsNull(query, notNull.Ordinal + offset)) { return true; }
        if (BooleanColumn(query) is int flag && BooleanColumn(predicate) is int required && flag == required + offset) { return true; }
        if (!Comparison(query, out var q, out var qop, out var qvalue, out var qc) ||
            !Comparison(predicate, out var p, out var pop, out var pvalue, out var pc) ||
            q.Ordinal != p.Ordinal + offset || !SameCollation(qc, pc) || qvalue.GetType() != pvalue.GetType()) { return false; }
        // IEEE NaN and signed-zero ordering are deliberately outside range inference.
        if (qvalue is float or double || pvalue is float or double) { return false; }
        int order;
        try { order = SqlExpressionEvaluator.Compare(qvalue, pvalue, qc.Resolve(null)); }
        catch (DatabaseException) { return false; }
        return qop switch
        {
            SqlBinaryOperator.Equal => pop switch
            {
                SqlBinaryOperator.Equal => order == 0,
                SqlBinaryOperator.GreaterThan => order > 0,
                SqlBinaryOperator.GreaterOrEqual => order >= 0,
                SqlBinaryOperator.LessThan => order < 0,
                SqlBinaryOperator.LessOrEqual => order <= 0,
                _ => false,
            },
            SqlBinaryOperator.GreaterThan => pop == SqlBinaryOperator.GreaterThan && order >= 0 || pop == SqlBinaryOperator.GreaterOrEqual && order >= 0,
            SqlBinaryOperator.GreaterOrEqual => pop == SqlBinaryOperator.GreaterThan && order > 0 || pop == SqlBinaryOperator.GreaterOrEqual && order >= 0,
            SqlBinaryOperator.LessThan => pop == SqlBinaryOperator.LessThan && order <= 0 || pop == SqlBinaryOperator.LessOrEqual && order <= 0,
            SqlBinaryOperator.LessOrEqual => pop == SqlBinaryOperator.LessThan && order < 0 || pop == SqlBinaryOperator.LessOrEqual && order <= 0,
            _ => false,
        };
    }

    // Shared by partial-index implication and ordering: a mandatory strict comparison
    // rejects a NULL key regardless of its other operand (including parameters).
    // Descend only through AND; neither OR nor NOT supplies a mandatory witness.
    internal static bool RejectsNull(SqlBoundExpression? expression, int ordinal)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlBoundLogical { IsOr: false } logical)
        {
            foreach (var term in logical.Operands) { if (RejectsNull(term, ordinal)) { return true; } }
            return false;
        }
        return expression switch
        {
            SqlBoundBinary binary when IsStrictComparison(binary.Operator) => IsColumn(binary.Left) || IsColumn(binary.Right),
            SqlBoundIsNull { IsNegated: true } test => IsColumn(test.Operand),
            SqlBoundIn { IsNegated: false } member => IsColumn(member.Operand),
            SqlBoundBetween { IsNegated: false } range => IsColumn(range.LowOperand) && IsColumn(range.HighOperand),
            SqlBoundLike { IsNegated: false } like => IsColumn(like.Operand),
            SqlBoundColumn column => column.Ordinal == ordinal,
            _ => false,
        };

        bool IsColumn(SqlBoundExpression operand) => operand is SqlBoundColumn column && column.Ordinal == ordinal;
    }

    private static bool IsStrictComparison(SqlBinaryOperator op) => op is SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual or
        SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterOrEqual or SqlBinaryOperator.LessThan or SqlBinaryOperator.LessOrEqual;

    private static int? BooleanColumn(SqlBoundExpression expression)
        => expression is SqlBoundColumn column ? column.Ordinal :
            Comparison(expression, out var key, out var op, out var value, out _) && op == SqlBinaryOperator.Equal && value is true ? key.Ordinal : null;

    internal static bool Comparison(SqlBoundExpression expression, out SqlBoundColumn column,
        out SqlBinaryOperator op, out object value, out SqlBoundCollation collation)
    {
        column = null!; value = null!; op = default; collation = default;
        if (expression is not SqlBoundBinary binary || !IsStrictComparison(binary.Operator) ||
            !binary.Collation.IsFixed) { return false; }
        if (binary.Left is SqlBoundColumn left && Constant(binary.Right, out var rightValue) && rightValue is not null)
        { column = left; value = rightValue; op = binary.Operator; }
        else if (binary.Right is SqlBoundColumn right && Constant(binary.Left, out var leftValue) && leftValue is not null)
        { column = right; value = leftValue; op = Flip(binary.Operator); }
        else { return false; }
        collation = binary.Collation;
        return true;
    }

    private static SqlBinaryOperator Flip(SqlBinaryOperator op) => op switch
    {
        SqlBinaryOperator.GreaterThan => SqlBinaryOperator.LessThan,
        SqlBinaryOperator.GreaterOrEqual => SqlBinaryOperator.LessOrEqual,
        SqlBinaryOperator.LessThan => SqlBinaryOperator.GreaterThan,
        SqlBinaryOperator.LessOrEqual => SqlBinaryOperator.GreaterOrEqual,
        _ => op,
    };

    private static bool SameCollation(SqlBoundCollation left, SqlBoundCollation right)
        => left.IsFixed && right.IsFixed && left.Resolve(null) == right.Resolve(null);

    // Bound column ordinals canonicalize aliases, qualification and parentheses. Constants
    // retain runtime type and representation; calls retain overload identity and conversions.
    private static bool Same(SqlBoundExpression query, SqlBoundExpression predicate, int offset)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (query is SqlBoundParameterConstant || predicate is SqlBoundParameterConstant) { return false; }
        if (Constant(query, out var qvalue) && Constant(predicate, out var pvalue))
        { return SameConstant(qvalue, pvalue); }
        return (query, predicate) switch
        {
            (SqlBoundColumn q, SqlBoundColumn p) => q.Ordinal == p.Ordinal + offset,
            (SqlBoundDomainArgument q, SqlBoundDomainArgument p) => q.HasSameCoercion(p) && Same(q.Operand, p.Operand, offset),
            (SqlBoundLogical q, SqlBoundLogical p) => !q.IsOr && !p.IsOr && SameList(q.Operands, p.Operands, offset),
            (SqlBoundBinary q, SqlBoundBinary p) => q.Operator == p.Operator && SameCollation(q.Collation, p.Collation) &&
                Same(q.Left, p.Left, offset) && Same(q.Right, p.Right, offset),
            (SqlBoundJsonAccess q, SqlBoundJsonAccess p) => q.Source == p.Source && q.AsText == p.AsText &&
                Same(q.Operand, p.Operand, offset) && q.Steps.Length == p.Steps.Length && q.Steps.Zip(p.Steps).All(pair =>
                    pair.First.SelectorType == pair.Second.SelectorType && pair.First.IsIndex == pair.Second.IsIndex &&
                    Same(pair.First.Selector, pair.Second.Selector, offset)),
            (SqlBoundIsJson q, SqlBoundIsJson p) => q.Source == p.Source && q.JsonKind == p.JsonKind && q.IsNegated == p.IsNegated &&
                Same(q.Operand, p.Operand, offset),
            (SqlBoundIsNull q, SqlBoundIsNull p) => q.IsNegated == p.IsNegated && Same(q.Operand, p.Operand, offset),
            (SqlBoundBooleanTest q, SqlBoundBooleanTest p) => q.IsNegated == p.IsNegated && q.TruthValue == p.TruthValue && Same(q.Operand, p.Operand, offset),
            (SqlBoundUnary q, SqlBoundUnary p) => q.Operator == p.Operator && q.Operator != SqlUnaryOperator.Not && Same(q.Operand, p.Operand, offset),
            (SqlBoundCall q, SqlBoundCall p) => ReferenceEquals(q.Function, p.Function) && q.Function.Volatility == SqlFunctionVolatility.Immutable &&
                SameCollation(q.Collation, p.Collation) && (q.Targets ?? []).SequenceEqual(p.Targets ?? []) && SameList(q.Arguments, p.Arguments, offset),
            (SqlBoundCast q, SqlBoundCast p) => q.Target.Storage.Equals(p.Target.Storage) && q.Target.IsDomain == p.Target.IsDomain &&
                q.Target.Name.Equals(p.Target.Name, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(q.Target.Check, p.Target.Check) && q.SourceType == p.SourceType && q.Assignment == p.Assignment &&
                q.Collation == p.Collation && q.Casts.SequenceEqual(p.Casts) && Same(q.Operand, p.Operand, offset),
            (SqlBoundCoalesce q, SqlBoundCoalesce p) => q.ResultType == p.ResultType && SameList(q.Arguments, p.Arguments, offset),
            (SqlBoundDistinct q, SqlBoundDistinct p) => q.IsNegated == p.IsNegated && SameCollation(q.Collation, p.Collation) &&
                Same(q.Left, p.Left, offset) && Same(q.Right, p.Right, offset),
            (SqlBoundBetween q, SqlBoundBetween p) => q.IsNegated == p.IsNegated && SameCollation(q.LowCollation, p.LowCollation) &&
                SameCollation(q.HighCollation, p.HighCollation) && Same(q.LowOperand, p.LowOperand, offset) && Same(q.HighOperand, p.HighOperand, offset) &&
                Same(q.Low, p.Low, offset) && Same(q.High, p.High, offset),
            (SqlBoundLike q, SqlBoundLike p) => q.IsNegated == p.IsNegated && q.IsCaseInsensitive == p.IsCaseInsensitive &&
                SameCollation(q.Collation, p.Collation) && Same(q.Operand, p.Operand, offset) && Same(q.Pattern, p.Pattern, offset) && Same(q.Escape, p.Escape, offset),
            (SqlBoundIn q, SqlBoundIn p) => q.IsNegated == p.IsNegated && q.ComparisonType == p.ComparisonType &&
                SameCollations(q.Collations, p.Collations) && Same(q.Operand, p.Operand, offset) && SameList(q.Candidates, p.Candidates, offset),
            (SqlBoundSpecialForm q, SqlBoundSpecialForm p) => q.Name == p.Name && q.FirstCharacterComparand == p.FirstCharacterComparand &&
                q.SecondCharacterComparand == p.SecondCharacterComparand && SameCollation(q.Collation, p.Collation) && SameList(q.Arguments, p.Arguments, offset),
            (SqlBoundCase q, SqlBoundCase p) => q.ResultType == p.ResultType && SameOptional(q.Input, p.Input, offset) &&
                SameOptional(q.ElseResult, p.ElseResult, offset) && q.Whens.Length == p.Whens.Length &&
                q.Whens.Zip(p.Whens).All(pair => SameCollation(pair.First.Collation, pair.Second.Collation) &&
                    Same(pair.First.Condition, pair.Second.Condition, offset) && Same(pair.First.Result, pair.Second.Result, offset)),
            _ => false,
        };
    }

    // PostgreSQL nodes/equalfuncs.c:95-113 delegates Const identity to datumIsEqual;
    // utils/adt/datum.c:205-264 compares representations, not SQL value equality.
    private static bool SameConstant(object? query, object? predicate)
    {
        if (query is null) { return predicate is null; }
        if (predicate is null || query.GetType() != predicate.GetType()) { return false; }
        switch (query)
        {
            case decimal number:
                Span<int> left = stackalloc int[4];
                Span<int> right = stackalloc int[4];
                decimal.GetBits(number, left);
                decimal.GetBits((decimal)predicate, right);
                return left.SequenceEqual(right);
            case double number:
                return BitConverter.DoubleToInt64Bits(number) == BitConverter.DoubleToInt64Bits((double)predicate);
            case float number:
                return BitConverter.SingleToInt32Bits(number) == BitConverter.SingleToInt32Bits((float)predicate);
            case DateTimeOffset timestamp:
                var other = (DateTimeOffset)predicate;
                return timestamp.Ticks == other.Ticks && timestamp.Offset == other.Offset;
            case byte[] bytes:
                return bytes.AsSpan().SequenceEqual((byte[])predicate);
            default:
                return query.Equals(predicate);
        }
    }

    private static bool SameOptional(SqlBoundExpression? query, SqlBoundExpression? predicate, int offset)
        => query is null ? predicate is null : predicate is not null && Same(query, predicate, offset);

    private static bool SameCollations(SqlBoundCollation[] query, SqlBoundCollation[] predicate)
        => query.Length == predicate.Length && query.Zip(predicate).All(pair => SameCollation(pair.First, pair.Second));

    private static bool Constant(SqlBoundExpression expression, out object? value)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlBoundParameterConstant) { value = null; return false; }
        if (expression is SqlBoundConstant constant) { value = constant.Value; return true; }
        value = null;
        bool literal = expression switch
        {
            SqlBoundUnary unary => unary.Operator is SqlUnaryOperator.Plus or SqlUnaryOperator.Negate && Constant(unary.Operand, out _),
            SqlBoundCast cast => !cast.Target.IsDomain && cast.Casts.All(conversion => conversion is null || SqlStandardCasts.Contains(conversion)) && Constant(cast.Operand, out _),
            _ => false,
        };
        if (!literal) { return false; }
        try { value = new SqlExpressionEvaluator([], null).Evaluate(expression, []); return true; }
        catch (DatabaseException) { return false; }
    }

    private static bool SameList(IReadOnlyList<SqlBoundExpression> query, IReadOnlyList<SqlBoundExpression> predicate, int offset)
    {
        if (query.Count != predicate.Count) { return false; }
        for (int i = 0; i < query.Count; i++) { if (!Same(query[i], predicate[i], offset)) { return false; } }
        return true;
    }

    internal static bool ReferencesOnly(SqlBoundExpression expression, int start, int count)
        => ReferencesOnly(expression, start, count, out _);

    internal static bool ReferencesOnly(SqlBoundExpression expression, int start, int count, out bool referencesColumn)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        bool containsColumn = expression is SqlBoundColumn;
        bool Local(SqlBoundExpression child)
        {
            bool valid = ReferencesOnly(child, start, count, out bool found);
            containsColumn |= found;
            return valid;
        }
        bool result = expression switch
        {
            SqlBoundColumn column => column.Ordinal >= start && column.Ordinal < start + count,
            SqlBoundDomainArgument domain => Local(domain.Operand),
            SqlBoundConstant => expression is not SqlBoundParameterConstant,
            SqlBoundLogical { IsOr: false } logical => logical.Operands.All(Local),
            SqlBoundBinary binary => Local(binary.Left) && Local(binary.Right),
            SqlBoundJsonAccess json => Local(json.Operand) && json.Steps.All(step => Local(step.Selector)),
            SqlBoundIsJson json => Local(json.Operand),
            SqlBoundIsNull test => Local(test.Operand),
            SqlBoundBooleanTest test => Local(test.Operand),
            SqlBoundUnary unary => unary.Operator != SqlUnaryOperator.Not && Local(unary.Operand),
            SqlBoundCall call => call.Function.Volatility == SqlFunctionVolatility.Immutable && call.Arguments.All(Local),
            SqlBoundCast cast => Local(cast.Operand),
            SqlBoundCoalesce coalesce => coalesce.Arguments.All(Local),
            SqlBoundDistinct distinct => Local(distinct.Left) && Local(distinct.Right),
            SqlBoundBetween between => Local(between.LowOperand) && Local(between.HighOperand) && Local(between.Low) && Local(between.High),
            SqlBoundLike like => Local(like.Operand) && Local(like.Pattern) && Local(like.Escape),
            SqlBoundIn member => Local(member.Operand) && member.Candidates.All(Local),
            SqlBoundSpecialForm special => special.Arguments.All(Local),
            SqlBoundCase conditional => (conditional.Input is null || Local(conditional.Input)) &&
                (conditional.ElseResult is null || Local(conditional.ElseResult)) && conditional.Whens.All(when => Local(when.Condition) && Local(when.Result)),
            _ => false,
        };
        referencesColumn = containsColumn;
        return result;
    }

    internal static string Describe(IReadOnlyList<SqlBoundExpression> proof, IReadOnlyList<SqlCatalogColumn> columns, int offset = 0)
        => string.Join(" AND ", proof.Select(term => Term(term, columns, offset)));

    private static string Term(SqlBoundExpression expression, IReadOnlyList<SqlCatalogColumn> columns, int offset)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        string Render(SqlBoundExpression child) => Term(child, columns, offset);
        return expression switch
        {
            SqlBoundColumn column => columns[column.Ordinal - offset].Name,
            SqlBoundDomainArgument domain => Render(domain.Operand),
            SqlBoundLogical logical => "(" + string.Join(" AND ", logical.Operands.Select(Render)) + ")",
            SqlBoundConstant constant => constant.Value switch
            {
                null => "NULL", true => "TRUE", false => "FALSE",
                string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
                SqlCharacter character => "'" + character.Value.Replace("'", "''", StringComparison.Ordinal) + "'",
                byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
                _ => Convert.ToString(constant.Value, CultureInfo.InvariantCulture)!,
            },
            SqlBoundBinary binary => "(" + Render(binary.Left) + " " + Operator(binary.Operator) + " " + Render(binary.Right) + ")",
            SqlBoundJsonAccess json => "(" + Render(json.Operand) + string.Concat(json.Steps.Select((step, index) =>
                (json.AsText && index == json.Steps.Length - 1 ? " ->> " : " -> ") + Render(step.Selector))) + ")",
            SqlBoundIsJson json => Render(json.Operand) + (json.IsNegated ? " IS NOT JSON " : " IS JSON ") + json.JsonKind.ToString().ToUpperInvariant(),
            SqlBoundIsNull test => Render(test.Operand) + (test.IsNegated ? " IS NOT NULL" : " IS NULL"),
            SqlBoundCall call => call.Function.Name + "(" + string.Join(", ", call.Arguments.Select(Render)) + ")",
            SqlBoundUnary unary => unary.Operator + "(" + Render(unary.Operand) + ")",
            SqlBoundCast cast => "CAST(" + Render(cast.Operand) + " AS " + cast.TargetText + ")",
            SqlBoundBooleanTest test => Render(test.Operand) + " IS " + (test.IsNegated ? "NOT " : "") + test.TruthValue,
            SqlBoundCoalesce coalesce => "COALESCE(" + string.Join(", ", coalesce.Arguments.Select(Render)) + ")",
            SqlBoundDistinct distinct => Render(distinct.Left) + (distinct.IsNegated ? " IS NOT DISTINCT FROM " : " IS DISTINCT FROM ") + Render(distinct.Right),
            SqlBoundBetween between => Render(between.LowOperand) + (between.IsNegated ? " NOT BETWEEN " : " BETWEEN ") + Render(between.Low) + " AND " + Render(between.High),
            SqlBoundLike like => Render(like.Operand) + (like.IsNegated ? " NOT" : "") + (like.IsCaseInsensitive ? " ILIKE " : " LIKE ") + Render(like.Pattern) + " ESCAPE " + Render(like.Escape),
            SqlBoundIn member => Render(member.Operand) + (member.IsNegated ? " NOT IN (" : " IN (") + string.Join(", ", member.Candidates.Select(Render)) + ")",
            SqlBoundSpecialForm special => special.Name + "(" + string.Join(", ", special.Arguments.Select(Render)) + ")",
            SqlBoundCase conditional => "CASE " + (conditional.Input is null ? "" : Render(conditional.Input)) +
                string.Join(" ", conditional.Whens.Select(when => "WHEN " + Render(when.Condition) + " THEN " + Render(when.Result))) +
                (conditional.ElseResult is null ? "" : " ELSE " + Render(conditional.ElseResult)) + " END",
            _ => expression.Kind.ToString(),
        };
    }

    private static string Operator(SqlBinaryOperator op) => op switch
    {
        SqlBinaryOperator.Equal => "=", SqlBinaryOperator.NotEqual => "<>",
        SqlBinaryOperator.GreaterThan => ">", SqlBinaryOperator.GreaterOrEqual => ">=",
        SqlBinaryOperator.LessThan => "<", SqlBinaryOperator.LessOrEqual => "<=",
        _ => op.ToString(),
    };
}
