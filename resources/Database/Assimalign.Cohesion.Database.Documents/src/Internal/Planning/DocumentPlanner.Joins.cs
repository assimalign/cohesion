using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    private DocumentJoinPlan PlanJoins(OqlSelectExpression query,
        IReadOnlyDictionary<OqlSubqueryExpression, DocumentNestedQueryPlan>? nested, IReadOnlySet<string>? outerNames)
    {
        if (string.IsNullOrEmpty(query.Alias))
        { throw new DocumentPlanningException("CD01035", ErrorMessages.JoinBindingInvalid, query.Location); }
        var collections = new Dictionary<OqlJoinExpression, DocumentCollectionMetadata>();
        foreach (var join in query.Bindings.OfType<OqlJoinExpression>())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            collections.Add(join, ResolveCollection(join.Collection, join.Location));
        }
        DocumentStatementPlan input = query.Source is not null ? PlanFrom(query, nested, outerNames) :
            DocumentSystemCollections.Find(query.Collection) is string system ?
                new DocumentSystemCollectionPlan(CreateLogicalPlan(query, _cancellationToken, _parameters, _functions, outerNames), system) : Plan(query, outerNames);
        var logical = QueryLogical(input);
        return new DocumentJoinPlan(input, logical with { CanStopEarly = !logical.IsGrouped && !query.IsDistinct && query.OrderBy.Count == 0 }, collections);
    }

    private static void ValidateQueryMetadata(OqlExpression expression, OqlSelectExpression query,
        IReadOnlySet<string>? outerNames, OqlSourceBindingExpression? before = null)
    {
        if (query.Bindings == query.Unnest)
        { ValidateMetadata(expression, query.Alias ?? query.Collection, DocumentSystemCollections.Find(query.Collection) is null, outerNames); return; }
        var sources = new HashSet<string>(StringComparer.Ordinal);
        if (outerNames is not null) { sources.UnionWith(outerNames); }
        sources.Add(query.Alias!);
        foreach (var binding in query.Bindings)
        {
            if (ReferenceEquals(binding, before)) { break; }
            sources.Remove(binding.Variable);
            if (binding is OqlJoinExpression) { sources.Add(binding.Variable); }
        }
        Visit(expression, sources);

        static void Visit(OqlExpression node, HashSet<string> names)
        {
            if (node is OqlSubqueryExpression) { return; }
            if (node is OqlMetaExpression meta)
            {
                if (meta.Member is not ("id" or "version") || meta.Arguments.Count != 1 ||
                    meta.Arguments[0] is not OqlPathExpression { Segments.Count: 1 } receiver || !names.Contains(receiver.Segments[0].Name!))
                { throw new DocumentPlanningException("CD01035", ErrorMessages.JoinMetadataInvalid, meta.Location); }
                return;
            }
            if (node is OqlComprehensionExpression comprehension)
            {
                Visit(comprehension.Collection, names);
                var inner = new HashSet<string>(names, StringComparer.Ordinal);
                inner.Remove(comprehension.Variable);
                Visit(comprehension.Projection, inner);
                if (comprehension.Predicate is not null) { Visit(comprehension.Predicate, inner); }
                return;
            }
            if (node is OqlQuantifiedExpression quantified)
            {
                Visit(quantified.Collection, names);
                var inner = new HashSet<string>(names, StringComparer.Ordinal);
                inner.Remove(quantified.Variable);
                Visit(quantified.Predicate, inner);
                return;
            }
            foreach (var child in NestedChildren(node)) { Visit(child, names); }
        }
    }
}
