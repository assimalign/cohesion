using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    private DocumentAccessPath AnalyzeOrdering(DocumentLogicalPlan logical, DocumentAccessPath input,
        IReadOnlyList<DocumentIndexMetadata> indexes, List<(DocumentIndexPath Path, int Prefix, bool Range)> accessCandidates,
        IReadOnlySet<string>? nonSourceNames)
    {
        // GetIndexes already supplies ordinal name order. Ordinary rejected plans
        // retain their access path without allocating EXPLAIN's candidate graph.
        var candidates = _estimateSinglePath ? new List<DocumentOrderedIndexCandidate>() : null;
        DocumentOrderedIndexCandidate? consumed = null;
        for (int i = 0; i < indexes.Count; i++)
        {
            var index = indexes[i];
            _cancellationToken.ThrowIfCancellationRequested();
            DocumentIndexPath? path = null;
            for (int j = 0; j < accessCandidates.Count; j++)
            {
                if (accessCandidates[j].Path.Index.ObjectId != index.ObjectId) { continue; }
                path = accessCandidates[j].Path;
                break;
            }
            var eligibility = OrderEligibility(logical, index, path, nonSourceNames);
            if (!eligibility.Eligible && candidates is null) { continue; }
            var candidate = new DocumentOrderedIndexCandidate(path ?? new DocumentIndexPath(index, null, null), eligibility);
            candidates?.Add(candidate);
            // Retain an intersection's identity merge; otherwise prefer the ordinary
            // predicate choice, then the first eligible index in ordinal name order.
            if (eligibility.Eligible && input is not DocumentIntersectionPath && (consumed is null ||
                input is DocumentIndexPath selected && selected.Index.ObjectId == index.ObjectId))
            { consumed = candidate; }
        }
        return candidates is not null ? new DocumentOrderingPath(input, candidates.AsReadOnly(), consumed) :
            consumed is null ? input : new DocumentOrderingPath(input, [consumed], consumed);
    }

    private DocumentOrderEligibility OrderEligibility(DocumentLogicalPlan logical, DocumentIndexMetadata index, DocumentIndexPath? path,
        IReadOnlySet<string>? nonSourceNames)
    {
        var query = logical.Query;
        DocumentOrderEligibility Refuse(string reason) => new(false, reason);
        if (logical.IsGrouped) { return Refuse("grouping_changes_order"); }
        if (query.IsDistinct) { return Refuse("distinct_changes_order"); }
        if (query.Bindings != query.Unnest) { return Refuse("key_join_requires_sort"); }
        if (query.Unnest.Count > 0) { return Refuse("unnest_changes_order"); }
        if (index.Paths.Count == 0) { return Refuse("array_index"); }
        bool Matches(OqlExpression expression, string name) => expression is OqlPathExpression field &&
            !IsVariablePath(field, nonSourceNames) &&
            MatchesOrderPath(field, query.Alias, name);
        bool Proves(string name, int fact) => ProvesOrderFact(query.Predicate, name, query.Alias, fact, nonSourceNames);
        if (!index.IncludeMissing && !Proves(index.Path, 0)) { return Refuse("leading_missing_not_rejected"); }
        int at = 0;
        bool? reverse = null;
        var equality = path?.EqualityPrefix ?? [];
        int equalityCount = equality.Count;
        // Single-column equality seeks preserve the older bound shape.
        if (index.Keys.Count == 1 && path?.Lower is { Inclusive: true } lower && path.Upper is { Inclusive: true } upper &&
            DocumentExpressionEvaluator.Compare(lower.Value, upper.Value) == 0) { equalityCount = 1; }
        for (int termIndex = 0; termIndex < query.OrderBy.Count; termIndex++)
        {
            var term = query.OrderBy[termIndex];
            while (at < equalityCount && !Matches(term.Expression, index.Paths[at])) { at++; }
            if (at >= index.Paths.Count || !Matches(term.Expression, index.Paths[at]))
            { return Refuse(term.Expression is OqlPathExpression ? "not_an_index_prefix" : "non_index_expression"); }
            // VALUE/RAW projects MISSING as NULL before sorting by the projected
            // expression. The physical two-key distinction cannot supply that tie.
            if (query.IsValue && logical.ProjectedOrdering[termIndex] && !Proves(index.Paths[at], 0))
            { return Refuse("value_missing_becomes_null"); }
            bool constant = at < equalityCount;
            if (!constant)
            {
                if (reverse.HasValue && reverse.Value != term.Descending) { return Refuse("mixed_directions"); }
                reverse = term.Descending;
                bool naturalNulls = term.NullPlacement == OqlNullPlacement.Default ||
                    term.NullPlacement == (term.Descending ? OqlNullPlacement.Last : OqlNullPlacement.First);
                if (!naturalNulls && !Proves(index.Paths[at], 1)) { return Refuse("null_placement_mismatch"); }
            }
            at++;
        }
        int orderPrefixKeys = at;
        at = 0;
        bool needsProbe = false;
        for (int termIndex = 0; !index.IsUnique && termIndex < query.OrderBy.Count; termIndex++)
        {
            var term = query.OrderBy[termIndex];
            while (at < equalityCount && !Matches(term.Expression, index.Paths[at])) { at++; }
            if (at >= equalityCount && !Proves(index.Paths[at], 2))
            {
                // Only the first varying key has a contiguous complex-tag interval.
                // Later unconstrained keys need a predicate proof; no data scan is added.
                if (at != equalityCount) { return Refuse("scalar_domain_unproven"); }
                needsProbe = true;
            }
            at++;
        }
        // Every term must pass its structural and predicate proofs before any data
        // probe. A later mismatch cannot benefit from probing an earlier component.
        if (needsProbe)
        {
            var probe = _catalog.ProbeComplexOrderKeys(index, equality, _snapshot, _cancellationToken);
            if (probe.Count > 0) { return Refuse("complex_values"); }
            if (!probe.IsComplete) { return Refuse("scalar_domain_unproven"); }
        }
        return new(true, "eligible", reverse ?? query.OrderBy[0].Descending, equalityCount, orderPrefixKeys);
    }

    private static bool MatchesOrderPath(OqlPathExpression path, string? alias, string name)
    {
        int start = alias is not null && path.Segments.Count > 0 && path.Segments[0].Name == alias ? 1 : 0;
        var remaining = name.AsSpan();
        Span<char> number = stackalloc char[11];
        for (int i = start; i < path.Segments.Count; i++)
        {
            var segment = path.Segments[i];
            if (segment.Name is { } field)
            {
                // Preserve canonical bracket quoting and the persisted legacy spelling.
                // Ordinary dotted paths compare directly without rebuilding strings.
                if (!CanUseDottedSegment(field))
                { return IndexPath(path, alias) == name || LegacyIndexPath(path, alias) == name; }
                if (i > start)
                {
                    if (remaining.IsEmpty || remaining[0] != '.') { return false; }
                    remaining = remaining[1..];
                }
                if (!remaining.StartsWith(field, StringComparison.Ordinal)) { return false; }
                remaining = remaining[field.Length..];
            }
            else
            {
                if (segment.Index is not int element || remaining.IsEmpty || remaining[0] != '[' || !element.TryFormat(number, out int length) ||
                    !remaining[1..].StartsWith(number[..length], StringComparison.Ordinal)) { return false; }
                remaining = remaining[(length + 1)..];
                if (remaining.IsEmpty || remaining[0] != ']') { return false; }
                remaining = remaining[1..];
            }
        }
        return path.Segments.Count > start && remaining.IsEmpty;
    }

    // fact 0 = MISSING rejected; 1 = both unknowns rejected; 2 = only scalar/nullish values.
    // AND needs one proof, OR needs both. Unknown functions and computed expressions
    // deliberately establish nothing. Range predicates compare only within scalar kinds.
    private bool ProvesOrderFact(OqlExpression? predicate, string name, string? alias, int fact, IReadOnlySet<string>? nonSourceNames)
    {
        if (predicate is null) { return false; }
        bool Field(OqlExpression expression) => expression is OqlPathExpression path &&
            !IsVariablePath(path, nonSourceNames) && MatchesOrderPath(path, alias, name);
        if (predicate is OqlBinaryExpression boolean && boolean.Operator is "AND" or "OR")
        {
            bool left = ProvesOrderFact(boolean.Left, name, alias, fact, nonSourceNames);
            return boolean.Operator == "AND" ? left || ProvesOrderFact(boolean.Right, name, alias, fact, nonSourceNames) :
                left && ProvesOrderFact(boolean.Right, name, alias, fact, nonSourceNames);
        }
        if (predicate is OqlUnaryExpression unary && Field(unary.Operand))
        {
            return fact switch
            {
                0 => unary.Operator is "IS NOT MISSING" or "IS NULL" or "IS NOT NULL" or "IS VALUED",
                1 => unary.Operator is "IS NOT NULL" or "IS VALUED",
                _ => unary.Operator is "IS NULL" or "IS MISSING",
            };
        }
        if (predicate is OqlUnaryExpression { Operator: "NOT", Operand: OqlUnaryExpression test } && Field(test.Operand))
        {
            string? operation = test.Operator switch
            { "IS MISSING" => "IS NOT MISSING", "IS NOT MISSING" => "IS MISSING", "IS NULL" => "IS NOT NULL",
                "IS NOT NULL" => "IS NULL", "IS NOT VALUED" => "IS VALUED", _ => null };
            return operation is not null && ProvesOrderFact(new OqlUnaryExpression(operation, test.Operand), name, alias, fact, nonSourceNames);
        }
        if (predicate is OqlBetweenExpression between && Field(between.Operand))
        { return fact < 2 || !between.Negated && ScalarConstant(between.Lower) && ScalarConstant(between.Upper); }
        if (predicate is OqlBinaryExpression comparison && comparison.Operator is "=" or "!=" or "<>" or "<" or "<=" or ">" or ">=")
        {
            var other = Field(comparison.Left) ? comparison.Right : Field(comparison.Right) ? comparison.Left : null;
            if (other is null) { return false; }
            return fact < 2 || comparison.Operator is not ("!=" or "<>") && ScalarConstant(other);
        }
        return false;
    }

    private bool ScalarConstant(OqlExpression expression)
    {
        if (!IsConstant(expression)) { return false; }
        try
        {
            return new DocumentExpressionEvaluator(null, _parameters, _cancellationToken).Evaluate(expression, default) is bool or decimal or string;
        }
        catch (DocumentPlanningException) { return false; } // Speculative proof; residual owns failures.
    }
}
