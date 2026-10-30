using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    private static bool HasArrayPredicate(OqlExpression? predicate) => predicate switch
    {
        OqlQuantifiedExpression { IsEvery: false, Collection: OqlPathExpression } => true,
        OqlBinaryExpression { Operator: "AND" } and => HasArrayPredicate(and.Left) || HasArrayPredicate(and.Right),
        _ => false,
    };

    private static IEnumerable<(OqlPathExpression Source, string Variable, OqlExpression Predicate)> ArrayScopes(
        OqlExpression? predicate, string? alias, IReadOnlySet<string>? variables, IReadOnlyList<OqlUnnestExpression>? unnest,
        IReadOnlySet<string>? outerNames)
        => outerNames is { Count: > 0 } ? CapturedArrayScopes(predicate, alias, variables, unnest, outerNames) :
            ArrayScopes(predicate, alias, variables, unnest);

    private static IEnumerable<(OqlPathExpression Source, string Variable, OqlExpression Predicate)> CapturedArrayScopes(
        OqlExpression? predicate, string? alias, IReadOnlySet<string>? variables, IReadOnlyList<OqlUnnestExpression>? unnest,
        IReadOnlySet<string> outerNames)
    {
        foreach (var scope in ArrayScopes(predicate, alias, variables, unnest))
        { if (!IsVariablePath(scope.Source, outerNames)) { yield return scope; } }
    }

    private static IEnumerable<(OqlPathExpression Source, string Variable, OqlExpression Predicate)> ArrayScopes(
        OqlExpression? predicate, string? alias, IReadOnlySet<string>? variables, IReadOnlyList<OqlUnnestExpression>? unnest)
    {
        foreach (var conjunct in Conjuncts(predicate))
        {
            if (conjunct is OqlQuantifiedExpression { IsEvery: false, Collection: OqlPathExpression source } any &&
                !IsVariablePath(source, variables))
            { yield return (source, any.Variable, any.Predicate); }
        }
        if (predicate is null || unnest is null) { yield break; }
        var preceding = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var binding in unnest)
        {
            // A direct document source is independent of preceding expansions. An
            // element comparison rejects LEFT's synthetic MISSING row as well.
            if (binding.Collection is OqlPathExpression source && !IsVariablePath(source, preceding) &&
                IndexPath(source, alias) is not null)
            { yield return (source, binding.Variable, predicate); }
            preceding.Add(binding.Variable);
        }
    }

    private static IEnumerable<OqlExpression> Conjuncts(OqlExpression? expression)
    {
        if (expression is OqlBinaryExpression { Operator: "AND" } and)
        {
            foreach (var child in Conjuncts(and.Left)) { yield return child; }
            foreach (var child in Conjuncts(and.Right)) { yield return child; }
        }
        else if (expression is not null) { yield return expression; }
    }

    private static bool WhenImplied(DocumentIndexPredicate? when, OqlExpression predicate, string variable)
    {
        if (when is null) { return true; }
        if (when.Operation == DocumentIndexPredicateKind.And)
        { return WhenImplied(when.Left, predicate, variable) && WhenImplied(when.Right, predicate, variable); }
        foreach (var conjunct in Conjuncts(predicate))
        {
            try
            {
                if (DocumentArrayIndexExpressions.BindPredicate(conjunct, variable) == when) { return true; }
            }
            catch (DocumentPlanningException)
            {
                // Implication is deliberately syntactic. Unrecognized expressions stay
                // residuals and cannot establish that every qualifying element is posted.
            }
        }
        return false;
    }
}
