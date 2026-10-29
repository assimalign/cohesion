using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    // The executor substitutes only a standalone projection alias. Direct paths use
    // the grouped row scope; only nested SELECTs receive the projection bindings.
    // Visitors carry both scopes through local quantifier/comprehension declarations.
    private static bool VisitOrdering<TScope>(OqlSelectExpression query, TScope grouped,
        Func<TScope, string, TScope> bind, Func<OqlExpression, TScope, TScope, bool> visit)
    {
        var nested = grouped;
        foreach (var projection in query.Projections)
        { if (projection.Alias is string name) { nested = bind(nested, name); } }
        foreach (var order in query.OrderBy)
        {
            var expression = order.Expression;
            if (expression is OqlPathExpression { Segments.Count: 1 } path &&
                query.Projections.FirstOrDefault(projection => projection.Alias is not null && projection.Alias == path.Segments[0].Name) is { } projection)
            { expression = projection.Expression; }
            if (!visit(expression, grouped, nested)) { return false; }
        }
        return true;
    }

    private static IReadOnlySet<string>? NonSourceNames(IReadOnlySet<string>? variables, IReadOnlySet<string>? outerNames)
    {
        if (outerNames is not { Count: > 0 }) { return variables; }
        if (variables is not { Count: > 0 }) { return outerNames; }
        var names = new HashSet<string>(variables, StringComparer.Ordinal);
        names.UnionWith(outerNames);
        return names;
    }

    private static bool IsLocalMetadata(OqlMetaExpression meta, string? alias)
        => meta.Arguments.Count == 0 || meta.Arguments.Count == 1 && alias is not null &&
            meta.Arguments[0] is OqlPathExpression { Segments.Count: 1 } path && path.Segments[0].Name == alias;

    private static bool EquivalentMetadataSource(OqlMetaExpression left, OqlMetaExpression right, OqlSelectExpression query,
        IReadOnlySet<string>? variables, DocumentBindingScope? leftScope, DocumentBindingScope? rightScope, int localDepth, bool captured)
    {
        bool Local(OqlMetaExpression meta, DocumentBindingScope? scope) => IsLocalMetadata(meta, query.Alias ?? query.Collection) &&
            (meta.Arguments.Count == 0 || DocumentBindingScope.Resolve(scope, query.Alias ?? query.Collection, out _) is null);
        if (Local(left, leftScope) && Local(right, rightScope)) { return true; }
        return left.Arguments.Count == 1 && right.Arguments.Count == 1 &&
            left.Arguments[0] is OqlPathExpression a && right.Arguments[0] is OqlPathExpression b &&
            EquivalentPath(a, b, query, variables, leftScope, rightScope, localDepth, captured);
    }

    private static bool EquivalentNestedBindings(OqlSelectExpression query, DocumentBindingScope? left,
        DocumentBindingScope? right, int localDepth)
    {
        if (ReferenceEquals(left, right)) { return true; }
        return SameQuery(query, null);

        bool SameName(string? name, DocumentBindingScope? local)
        {
            if (DocumentBindingScope.Resolve(local, name, out _) is not null) { return true; }
            var a = DocumentBindingScope.Resolve(left, name, out int aDistance);
            var b = DocumentBindingScope.Resolve(right, name, out int bDistance);
            return ReferenceEquals(a, b) || a is not null && b is not null && aDistance < localDepth && aDistance == bDistance;
        }

        bool SameQuery(OqlSelectExpression select, DocumentBindingScope? local)
        {
            // FROM resolves before its own alias; later clauses see only bindings
            // introduced so far. Local declarations mask the enclosing scopes being
            // compared, including in transitive captures and nested ORDER BY.
            if (select.Source is not null ? !Same(select.Source, local) : !SameName(select.Collection, local)) { return false; }
            var source = new DocumentBindingScope(select.Alias ?? select.Collection, local);
            foreach (var binding in select.Unnest)
            {
                if (!Same(binding.Collection, source)) { return false; }
                source = new DocumentBindingScope(binding.Variable, source);
            }
            if (select.Predicate is not null && !Same(select.Predicate, source)) { return false; }
            foreach (var key in select.GroupBy) { if (!Same(key, source)) { return false; } }
            var grouped = source;
            foreach (var name in select.GroupAliases)
            { if (name is not null) { grouped = new DocumentBindingScope(name, grouped); } }
            if (select.GroupAs is not null) { grouped = new DocumentBindingScope(select.GroupAs, grouped); }
            foreach (var projection in select.Projections) { if (!Same(projection.Expression, grouped)) { return false; } }
            if (select.Having is not null && !Same(select.Having, grouped)) { return false; }
            return VisitOrdering(select, grouped, (scope, name) => new DocumentBindingScope(name, scope),
                (expression, scope, nested) => Same(expression, scope, nested));
        }

        bool Same(OqlExpression expression, DocumentBindingScope? local, DocumentBindingScope? nestedOrder = null)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (expression is OqlSubqueryExpression subquery) { return SameQuery(subquery.Query, nestedOrder ?? local); }
            if (expression is OqlPathExpression { Segments.Count: > 0 } path) { return SameName(path.Segments[0].Name, local); }
            if (expression is OqlComprehensionExpression comprehension)
            {
                var inner = new DocumentBindingScope(comprehension.Variable, local);
                var orderedInner = nestedOrder is null ? null : new DocumentBindingScope(comprehension.Variable, nestedOrder);
                return Same(comprehension.Collection, local, nestedOrder) && Same(comprehension.Projection, inner, orderedInner) &&
                    (comprehension.Predicate is null || Same(comprehension.Predicate, inner, orderedInner));
            }
            if (expression is OqlQuantifiedExpression quantified)
            { return Same(quantified.Collection, local, nestedOrder) && Same(quantified.Predicate, new DocumentBindingScope(quantified.Variable, local),
                nestedOrder is null ? null : new DocumentBindingScope(quantified.Variable, nestedOrder)); }
            foreach (var child in NestedChildren(expression)) { if (!Same(child, local, nestedOrder)) { return false; } }
            return true;
        }
    }

    // Validate against this grouped query at every nested depth. Inner aggregate inputs
    // do not restore this query's hidden source; an aggregate in this query already
    // exempts its own row inputs before reaching this visitor. PostgreSQL's analogous
    // level-aware grouping check is src/backend/parser/parse_agg.c:1427-1514.
    private static void ValidateGroupedCaptures(OqlSelectExpression nested, OqlSelectExpression query,
        IReadOnlySet<string>? variables, DocumentBindingScopes scopes, DocumentFunctionCatalog functions,
        DocumentBindingScope? bound, IReadOnlySet<string>? outerNames, bool groupArrayInput = false)
    {
        VisitQuery(nested, bound);

        bool SourceName(string? name) => groupArrayInput ? name == query.GroupAs :
            name == (query.Alias ?? query.Collection) || name is not null && variables?.Contains(name) == true;

        void VisitQuery(OqlSelectExpression select, DocumentBindingScope? local)
        {
            if (select.Source is not null) { Visit(select.Source, local); }
            else if (select.Collection != query.Collection || query.Alias is not null)
            {
                // Bare explicit aliases are expression sources, resolved before the
                // inner row alias is introduced. An implicit keyspace stays a keyspace.
                if (SourceName(select.Collection) && DocumentBindingScope.Resolve(local, select.Collection, out _) is null)
                { Visit(new OqlPathExpression([new OqlPathSegment(select.Collection, null)], select.Location), local); }
            }
            var source = new DocumentBindingScope(select.Alias ?? select.Collection, local);
            foreach (var binding in select.Unnest)
            {
                Visit(binding.Collection, source);
                source = new DocumentBindingScope(binding.Variable, source);
            }
            if (select.Predicate is not null) { Visit(select.Predicate, source); }
            foreach (var key in select.GroupBy) { Visit(key, source); }
            var grouped = source;
            foreach (var name in select.GroupAliases)
            { if (name is not null) { grouped = new DocumentBindingScope(name, grouped); } }
            if (select.GroupAs is not null) { grouped = new DocumentBindingScope(select.GroupAs, grouped); }
            foreach (var projection in select.Projections) { Visit(projection.Expression, grouped); }
            if (select.Having is not null) { Visit(select.Having, grouped); }
            VisitOrdering(select, grouped, (scope, name) => new DocumentBindingScope(name, scope),
                (expression, scope, nested) => { Visit(expression, scope, nested); return true; });
        }

        void Visit(OqlExpression expression, DocumentBindingScope? local, DocumentBindingScope? nestedOrder = null)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            // Match complete keys before their components, while retaining every
            // inner binding's identity so shadowed names cannot prove a group key.
            if (query.GroupBy.Any(key => EquivalentExpression(key, expression, query, variables, scopes,
                rightScope: expression is OqlSubqueryExpression ? nestedOrder ?? local : local, captured: true))) { return; }
            if (expression is OqlSubqueryExpression subquery) { VisitQuery(subquery.Query, nestedOrder ?? local); return; }
            if (expression is OqlMetaExpression meta)
            {
                if (meta.Arguments.Count == 1 && meta.Arguments[0] is OqlPathExpression { Segments.Count: 1 } receiver &&
                    SourceName(receiver.Segments[0].Name) && DocumentBindingScope.Resolve(local, receiver.Segments[0].Name, out _) is null)
                { ValidateGrouped(meta, query, variables, scopes, functions, bound, null); }
                return;
            }
            if (expression is OqlPathExpression { Segments.Count: > 0 } path)
            {
                if (SourceName(path.Segments[0].Name) && DocumentBindingScope.Resolve(local, path.Segments[0].Name, out _) is null)
                {
                    if (groupArrayInput) { ValidateGroupArrayInput(path, query, scopes, functions, aggregateInput: true); }
                    // Complete keys were compared above in their respective scopes.
                    // An unmatched, unshadowed source path is hidden after grouping.
                    else { throw new DocumentPlanningException("CD05001", ErrorMessages.DocumentPlannerValidateGroupedFailure, path.Location); }
                }
                return;
            }
            if (expression is OqlComprehensionExpression comprehension)
            {
                Visit(comprehension.Collection, local, nestedOrder);
                var inner = scopes.Enter(comprehension, comprehension.Variable, local);
                var orderedInner = nestedOrder is null ? null : scopes.Enter(comprehension, comprehension.Variable, nestedOrder);
                Visit(comprehension.Projection, inner, orderedInner);
                if (comprehension.Predicate is not null) { Visit(comprehension.Predicate, inner, orderedInner); }
                return;
            }
            if (expression is OqlQuantifiedExpression quantified)
            {
                Visit(quantified.Collection, local, nestedOrder);
                Visit(quantified.Predicate, scopes.Enter(quantified, quantified.Variable, local),
                    nestedOrder is null ? null : scopes.Enter(quantified, quantified.Variable, nestedOrder));
                return;
            }
            foreach (var child in NestedChildren(expression)) { Visit(child, local, nestedOrder); }
        }
    }
}
