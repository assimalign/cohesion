using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    private DocumentOrderingPath AnalyzeOrdering(DocumentLogicalPlan logical, DocumentAccessPath input,
        IReadOnlyList<DocumentIndexMetadata> indexes)
    {
        var candidates = new List<DocumentOrderedIndexCandidate>();
        foreach (var index in indexes.OrderBy(index => index.Name, StringComparer.Ordinal))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var path = ChooseAccess(logical.Query.Predicate, logical.Query.Alias, [index],
                logical.UnnestVariables, logical.Query.Unnest) as DocumentIndexPath ?? new DocumentIndexPath(index, null, null);
            candidates.Add(new DocumentOrderedIndexCandidate(path, OrderEligibility(logical, path)));
        }
        return new DocumentOrderingPath(input, candidates.AsReadOnly());
    }

    private DocumentOrderEligibility OrderEligibility(DocumentLogicalPlan logical, DocumentIndexPath path)
    {
        var query = logical.Query;
        var index = path.Index;
        DocumentOrderEligibility Refuse(string reason) => new(false, reason);
        if (logical.IsGrouped) { return Refuse("grouping_changes_order"); }
        if (query.IsDistinct) { return Refuse("distinct_changes_order"); }
        if (query.Unnest.Count > 0) { return Refuse("unnest_changes_order"); }
        if (index.Paths.Count == 0) { return Refuse("array_index"); }
        bool Matches(OqlExpression expression, string name) => expression is OqlPathExpression field &&
            !IsVariablePath(field, logical.UnnestVariables) &&
            (IndexPath(field, query.Alias) == name || LegacyIndexPath(field, query.Alias) == name);
        bool Proves(string name, int fact) => ProvesOrderFact(query.Predicate, name, query.Alias, fact);
        if (!index.IncludeMissing && !Proves(index.Path, 0)) { return Refuse("leading_missing_not_rejected"); }
        int at = 0;
        bool? reverse = null;
        var equality = path.EqualityPrefix ?? [];
        // Single-column equality seeks preserve the older bound shape.
        if (index.Keys.Count == 1 && path.Lower is { Inclusive: true } lower && path.Upper is { Inclusive: true } upper &&
            DocumentExpressionEvaluator.Compare(lower.Value, upper.Value) == 0) { equality = [lower.Value]; }
        foreach (var term in query.OrderBy)
        {
            while (at < equality.Count && !Matches(term.Expression, index.Paths[at])) { at++; }
            if (at >= index.Paths.Count || !Matches(term.Expression, index.Paths[at]))
            { return Refuse(term.Expression is OqlPathExpression ? "not_an_index_prefix" : "non_index_expression"); }
            bool constant = at < equality.Count;
            if (!constant)
            {
                if (reverse.HasValue && reverse.Value != term.Descending) { return Refuse("mixed_directions"); }
                reverse = term.Descending;
                bool naturalNulls = term.NullPlacement == OqlNullPlacement.Default ||
                    term.NullPlacement == (term.Descending ? OqlNullPlacement.Last : OqlNullPlacement.First);
                if (!naturalNulls && !Proves(index.Paths[at], 1)) { return Refuse("null_placement_mismatch"); }
                if (!index.IsUnique && !Proves(index.Paths[at], 2))
                {
                    // Only the first varying key has a contiguous complex-tag interval.
                    // Later unconstrained keys need a predicate proof; no data scan is added.
                    if (at != equality.Count) { return Refuse("scalar_domain_unproven"); }
                    var probe = _catalog.ProbeComplexOrderKeys(index, equality, _snapshot, _cancellationToken);
                    if (probe.Count > 0) { return Refuse("complex_values"); }
                    if (!probe.IsComplete) { return Refuse("scalar_domain_unproven"); }
                }
            }
            at++;
        }
        return new(true, "eligible", reverse ?? query.OrderBy[0].Descending, equality.Count, at);
    }

    // fact 0 = MISSING rejected; 1 = both unknowns rejected; 2 = only scalar/nullish values.
    // AND needs one proof, OR needs both. Unknown functions and computed expressions
    // deliberately establish nothing. Range predicates compare only within scalar kinds.
    private bool ProvesOrderFact(OqlExpression? predicate, string name, string? alias, int fact)
    {
        if (predicate is null) { return false; }
        bool Field(OqlExpression expression) => expression is OqlPathExpression path &&
            (IndexPath(path, alias) == name || LegacyIndexPath(path, alias) == name);
        if (predicate is OqlBinaryExpression boolean && boolean.Operator is "AND" or "OR")
        {
            bool left = ProvesOrderFact(boolean.Left, name, alias, fact);
            return boolean.Operator == "AND" ? left || ProvesOrderFact(boolean.Right, name, alias, fact) :
                left && ProvesOrderFact(boolean.Right, name, alias, fact);
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
            return operation is not null && ProvesOrderFact(new OqlUnaryExpression(operation, test.Operand), name, alias, fact);
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
