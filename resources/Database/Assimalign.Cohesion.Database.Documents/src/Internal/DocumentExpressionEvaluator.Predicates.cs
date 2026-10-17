using System.Collections.Generic;
using System.Linq;
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
            else if (operand is not null and not DocumentMissing && Compare(operand, value) == 0) { matched = true; }
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
            bool matches = expression.Operand is null ? condition is true :
                operand is not null and not DocumentMissing && condition is not null and not DocumentMissing && Compare(operand, condition) == 0;
            if (matches) { return Evaluate(branch.Value, document, group); }
        }
        // Couchbase expression/case_simple.go:83-108 and case_searched.go:73-98:
        // unknown conditions fall through; an omitted ELSE yields NULL.
        return expression.Otherwise is null ? null : Evaluate(expression.Otherwise, document, group);
    }

    private object? Like(OqlLikeExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group)
    {
        var operand = Evaluate(expression.Operand, document, group);
        var pattern = Evaluate(expression.Pattern, document, group);
        var escape = expression.Escape is null ? "\\" : Evaluate(expression.Escape, document, group);
        // Couchbase expression/comp_like.go:105-109: MISSING precedes non-string/NULL.
        if (operand is DocumentMissing || pattern is DocumentMissing || escape is DocumentMissing) { return DocumentMissing.Instance; }
        if (operand is not string input || pattern is not string text || escape is not string escapeText) { return null; }
        int[] escapes = escapeText.EnumerateRunes().Select(rune => rune.Value).ToArray();
        if (escapes.Length > 1)
        {
            throw new DocumentPlanningException("COHDOC013", ErrorMessages.LikeEscapeInvalid, expression.Escape?.Location);
        }
        bool matches = MatchLike(input, text, escapes.Length == 0 ? -1 : escapes[0]);
        return expression.Negated ? !matches : matches;
    }

    private static bool MatchLike(string input, string pattern, int escape)
    {
        const int many = -1;
        const int one = -2;
        var tokens = new List<int>();
        bool escaped = false;
        foreach (var rune in pattern.EnumerateRunes())
        {
            if (escaped) { tokens.Add(rune.Value); escaped = false; }
            else if (rune.Value == escape) { escaped = true; }
            else { tokens.Add(rune.Value == '%' ? many : rune.Value == '_' ? one : rune.Value); }
        }
        if (escaped) { throw new DocumentPlanningException("COHDOC013", ErrorMessages.LikeTrailingEscape); }

        int[] scalars = input.EnumerateRunes().Select(rune => rune.Value).ToArray();
        int value = 0, token = 0, percent = -1, retry = 0;
        // Only the most recent percent is a backtracking point. No suffix recursion,
        // regex engine or combinatorial exploration of earlier percent assignments.
        while (value < scalars.Length)
        {
            if (token < tokens.Count && tokens[token] == many)
            {
                percent = token++;
                retry = value;
            }
            else if (token < tokens.Count && (tokens[token] == one || tokens[token] == scalars[value]))
            {
                token++;
                value++;
            }
            else if (percent >= 0)
            {
                token = percent + 1;
                value = ++retry;
            }
            else { return false; }
        }
        while (token < tokens.Count && tokens[token] == many) { token++; }
        return token == tokens.Count;
    }
}
