using System;
using System.Collections.Generic;
using System.Text;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentExpressionEvaluator
{
    private object? In(OqlInExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group)
    {
        var operand = Evaluate(expression.Operand, document, group);
        bool missing = false;
        bool nullItem = false;
        bool matched = false;
        // Couchbase expression/coll_in.go:58-159: a match wins, then NULL, then MISSING.
        foreach (var item in expression.Values)
        {
            var value = Evaluate(item, document, group);
            if (value is DocumentMissing) { missing = true; }
            else if (value is null) { nullItem = true; }
            else if (operand is not null and not DocumentMissing && Equal(operand, value) is true) { matched = true; }
        }
        if (operand is DocumentMissing) { return DocumentMissing.Instance; }
        if (operand is null) { return null; }
        if (matched) { return !expression.Negated; }
        if (nullItem) { return null; }
        if (missing) { return DocumentMissing.Instance; }
        return expression.Negated;
    }

    private object? Between(OqlBetweenExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group)
    {
        var operand = Evaluate(expression.Operand, document, group);
        var lower = Evaluate(expression.Lower, document, group);
        var upper = Evaluate(expression.Upper, document, group);
        // Couchbase expression/comp_between.go:54-72 checks both unknown comparisons
        // before returning a Boolean, even if one ordinary bound would exclude the value.
        if (operand is DocumentMissing || lower is DocumentMissing || upper is DocumentMissing) { return DocumentMissing.Instance; }
        if (operand is null || lower is null || upper is null) { return null; }
        bool matches = SameScalarKind(operand, lower) && SameScalarKind(operand, upper) &&
            Compare(operand, lower) >= 0 && Compare(operand, upper) <= 0;
        return expression.Negated ? !matches : matches;
    }

    private object? Case(OqlCaseExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group)
    {
        var operand = expression.Operand is null ? null : Evaluate(expression.Operand, document, group);
        foreach (var branch in expression.Branches)
        {
            var condition = Evaluate(branch.Key, document, group);
            bool matches = expression.Operand is null ? Truth(condition) is true :
                operand is not null and not DocumentMissing && condition is not null and not DocumentMissing && Equal(operand, condition) is true;
            if (matches) { return Evaluate(branch.Value, document, group); }
        }
        // Couchbase expression/case_simple.go:83-108 and case_searched.go:73-98:
        // unknown conditions fall through; an omitted ELSE yields NULL.
        return expression.Otherwise is null ? null : Evaluate(expression.Otherwise, document, group);
    }

    private object? Like(OqlLikeExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group)
    {
        var operand = Evaluate(expression.Operand, document, group);
        if (!_likePatterns.TryGetValue(expression, out var tokens))
        {
            var pattern = Evaluate(expression.Pattern, document, group);
            var escape = expression.Escape is null ? "\\" : Evaluate(expression.Escape, document, group);
            // Couchbase expression/comp_like.go:105-109: MISSING precedes non-string/NULL.
            if (operand is DocumentMissing || pattern is DocumentMissing || escape is DocumentMissing) { return DocumentMissing.Instance; }
            if (operand is not string || pattern is not string text || escape is not string escapeText) { return null; }
            int escapeScalar = -1;
            foreach (var rune in escapeText.EnumerateRunes())
            {
                if (escapeScalar >= 0)
                {
                    throw new DocumentPlanningException("COHDOC013", ErrorMessages.LikeEscapeInvalid, expression.Escape?.Location);
                }
                escapeScalar = rune.Value;
            }
            tokens = CompileLike(text, escapeScalar);
            if (DocumentPlanner.IsConstant(expression.Pattern) && (expression.Escape is null || DocumentPlanner.IsConstant(expression.Escape)))
            {
                _likePatterns.Add(expression, tokens);
            }
        }
        if (operand is DocumentMissing) { return DocumentMissing.Instance; }
        if (operand is not string input) { return null; }
        bool matches = MatchLike(input, tokens);
        return expression.Negated ? !matches : matches;
    }

    private int[] CompileLike(string pattern, int escape)
    {
        var tokens = new List<int>();
        bool escaped = false;
        int iterations = 0;
        foreach (var rune in pattern.EnumerateRunes())
        {
            if ((iterations++ & 1023) == 0) { _cancellationToken.ThrowIfCancellationRequested(); }
            if (escaped) { tokens.Add(rune.Value); escaped = false; }
            else if (rune.Value == escape) { escaped = true; }
            else { tokens.Add(rune.Value == '%' ? -1 : rune.Value == '_' ? -2 : rune.Value); }
        }
        if (escaped) { throw new DocumentPlanningException("COHDOC013", ErrorMessages.LikeTrailingEscape); }
        return tokens.ToArray();
    }

    private bool MatchLike(string input, int[] tokens)
    {
        const int many = -1;
        const int one = -2;
        int value = 0, token = 0, percent = -1, retry = 0, iterations = 0;
        // Only the most recent percent is a backtracking point. No suffix recursion,
        // regex engine or combinatorial exploration of earlier percent assignments.
        while (value < input.Length)
        {
            // PostgreSQL src/backend/utils/adt/like_match.c:302 checks interrupts
            // within matching. Bound cancellation latency even for a long literal suffix.
            if ((iterations++ & 1023) == 0) { _cancellationToken.ThrowIfCancellationRequested(); }
            if (token < tokens.Length && tokens[token] == many)
            {
                percent = token++;
                retry = value;
                if (token == tokens.Length) { _cancellationToken.ThrowIfCancellationRequested(); return true; }
            }
            else
            {
                // Decode at the current UTF-16 offset instead of allocating a scalar array per row.
                // DecodeFromUtf16 consumes one code unit and yields U+FFFD for invalid UTF-16,
                // preserving EnumerateRunes behavior for caller-supplied strings.
                Rune.DecodeFromUtf16(input.AsSpan(value), out var rune, out int consumed);
                if (token < tokens.Length && (tokens[token] == one || tokens[token] == rune.Value))
                {
                    token++;
                    value += consumed;
                }
                else if (percent >= 0)
                {
                    token = percent + 1;
                    Rune.DecodeFromUtf16(input.AsSpan(retry), out _, out int retryConsumed);
                    retry += retryConsumed;
                    value = retry;
                }
                else { return false; }
            }
        }
        while (token < tokens.Length && tokens[token] == many)
        {
            if ((token & 1023) == 0) { _cancellationToken.ThrowIfCancellationRequested(); }
            token++;
        }
        _cancellationToken.ThrowIfCancellationRequested();
        return token == tokens.Length;
    }
}
