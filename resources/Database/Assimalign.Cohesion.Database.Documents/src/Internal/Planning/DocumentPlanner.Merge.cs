using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    private static IEnumerable<OqlExpression> MergeActionExpressions(OqlMergeExpression merge)
    {
        if (merge.Update is { } update)
        {
            foreach (var assignment in update.Assignments) { yield return assignment.Value; }
            if (update.Predicate is not null) { yield return update.Predicate; }
        }
        if (merge.Delete?.Predicate is { } deleted) { yield return deleted; }
        if (merge.Insert is { } insert)
        {
            if (insert.Key is not null) { yield return insert.Key; }
            yield return insert.Value;
            if (insert.Predicate is not null) { yield return insert.Predicate; }
        }
        if (merge.Returning is not null) { yield return merge.Returning; }
    }

    private DocumentMergePlan PlanMerge(OqlMergeExpression merge,
        IReadOnlyDictionary<OqlSubqueryExpression, DocumentNestedQueryPlan>? nested,
        IReadOnlyDictionary<OqlQueryExpression, IReadOnlySet<string>>? queryOuterNames)
    {
        ValidateMergeDeclaration(merge);
        var collection = ResolveCollection(merge.Collection, merge.Location);
        string target = DocumentSourceName.Of(merge.Collection, merge.Alias);
        string source = MergeSourceName(merge.Source);
        var sourceQuery = MergeSourceQuery(merge.Source);
        var sourcePlan = PlanCore(sourceQuery, nested, queryOuterNames: queryOuterNames);
        OqlExpression key = merge.On;
        if (!merge.IsLookup)
        {
            if (merge.On is not OqlBinaryExpression { Operator: "=", Left: OqlMetaExpression { Member: "id", Arguments.Count: 1 } meta } equality ||
                meta.Arguments[0] is not OqlPathExpression { Segments.Count: 1 } receiver || receiver.Segments[0].Name != target)
            { throw new DocumentPlanningException("CK00001", ErrorMessages.MergeOnUnsupported, merge.On.Location); }
            key = equality.Right;
        }
        var matched = new DocumentMergeScope(source, target, false,
            merge.Source.Expression is null && DocumentSystemCollections.Find(merge.Source.Collection) is null);
        var lookup = matched with { TargetName = merge.IsLookup ? null : target };
        var insertScope = matched with { TargetIsMissing = true };
        ValidateMergeExpression(merge.On, lookup);
        if (!merge.IsLookup) { ValidateDependency(key, target, null, ErrorMessages.MergeOnUnsupported); }
        DocumentUpdatePlan? update = null;
        if (merge.Update is { } mutation)
        {
            foreach (var path in mutation.Assignments.Select(a => a.Path).Concat(mutation.Unset))
            {
                if (path.Segments.Count > 0 && path.Segments[0].Name == source)
                { throw new DocumentPlanningException("CD02016", ErrorMessages.UpdateTargetInvalid, path.Location); }
            }
            foreach (var assignment in mutation.Assignments) { ValidateMergeExpression(assignment.Value, matched); }
            if (mutation.Predicate is not null) { ValidateMergeExpression(mutation.Predicate, matched); }
            update = PlanUpdate(new OqlUpdateExpression(merge.Collection, merge.Alias, mutation.Assignments, mutation.Unset, mutation.Predicate, merge.Location),
                new HashSet<string>(StringComparer.Ordinal) { source });
        }
        if (merge.Delete?.Predicate is { } deleted) { ValidateMergeExpression(deleted, matched); }
        if (merge.Insert is { } inserted)
        {
            if (inserted.Key is not null) { ValidateMergeExpression(inserted.Key, insertScope); }
            ValidateMergeExpression(inserted.Value, insertScope);
            if (inserted.Predicate is not null) { ValidateMergeExpression(inserted.Predicate, insertScope); }
        }
        if (merge.Returning is { } returning)
        {
            // Ordinary RETURNING already defines target-relative field and META() binding.
            // Source visibility in MERGE RETURNING is not settled by the available references.
            ValidateDependency(returning, source, null, ErrorMessages.MergeReturningSourceUnsupported);
        }
        return new DocumentMergePlan(collection, merge, sourcePlan, source, target, key, lookup, matched, insertScope, update,
            PlanReturning(merge.Returning, merge.Collection, merge.Alias));

        void ValidateDependency(OqlExpression node, string targetName, DocumentBindingScope? local, string message)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            _cancellationToken.ThrowIfCancellationRequested();
            bool capturesTarget = node is OqlPathExpression { Segments.Count: > 0 } path && path.Segments[0].Name == targetName &&
                DocumentBindingScope.Resolve(local, targetName, out _) is null || node is OqlSubqueryExpression subquery &&
                nested is not null && nested[subquery].OuterNames.Contains(targetName);
            if (capturesTarget) { throw new DocumentPlanningException("CK00001", message, node.Location); }
            if (node is OqlSubqueryExpression) { return; }
            if (node is OqlComprehensionExpression comprehension)
            {
                ValidateDependency(comprehension.Collection, targetName, local, message);
                var inner = new DocumentBindingScope(comprehension.Variable, local);
                ValidateDependency(comprehension.Projection, targetName, inner, message);
                if (comprehension.Predicate is not null) { ValidateDependency(comprehension.Predicate, targetName, inner, message); }
                return;
            }
            if (node is OqlQuantifiedExpression quantified)
            {
                ValidateDependency(quantified.Collection, targetName, local, message);
                ValidateDependency(quantified.Predicate, targetName, new DocumentBindingScope(quantified.Variable, local), message);
                return;
            }
            foreach (var child in NestedChildren(node)) { ValidateDependency(child, targetName, local, message); }
        }
    }

    private static void ValidateMergeDeclaration(OqlMergeExpression merge)
    {
        DocumentSystemCollections.EnsureReadOnly(merge.Collection, merge.Location);
        string target = DocumentSourceName.Of(merge.Collection, merge.Alias);
        string source = MergeSourceName(merge.Source);
        if (merge.Alias is { Length: 0 } || merge.Source.Alias is { Length: 0 })
        { throw new DocumentPlanningException("CD01023", ErrorMessages.BindingNameMissing, merge.Location); }
        if (target == source)
        { throw new DocumentPlanningException("CD01023", ErrorMessages.BindingNameDuplicate, merge.Location); }
        if (source.Length == 0 || (merge.Source.Expression is null ? string.IsNullOrWhiteSpace(merge.Source.Collection) : merge.Source.Collection.Length != 0) ||
            merge.Source.Expression is OqlSubqueryExpression && merge.Source.Alias is null ||
            merge.IsPrimary && !merge.IsLookup || merge.Update is null && merge.Delete is null && merge.Insert is null ||
            merge.Insert is { } insertion && (merge.IsLookup ? insertion.Key is not null : insertion.Key is null))
        { throw new DocumentPlanningException("CD01039", ErrorMessages.MergeShapeInvalid, merge.Location); }
    }

    private static string MergeSourceName(OqlMergeSource source) => source.Alias ??
        (source.Expression is OqlPathExpression { Segments.Count: > 0 } path ? path.Segments[^1].Name ?? string.Empty :
            source.Expression is null ? DocumentSourceName.Of(source.Collection, null) : string.Empty);

    private static OqlSelectExpression MergeSourceQuery(OqlMergeSource source) => new(source.Collection,
        source.Alias ?? (source.Expression is null ? null : MergeSourceName(source)),
        [new OqlProjection(new OqlStarExpression(), null)], null, [], null, [], false, false, source: source.Expression,
        isAliasImplicit: source.Alias is null);

    private void ValidateMergeExpression(OqlExpression expression, DocumentMergeScope scope)
    {
        Visit(expression, null);
        Validate(expression, allowAggregate: false, functions: _functions);

        void Visit(OqlExpression node, DocumentBindingScope? local)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            _cancellationToken.ThrowIfCancellationRequested();
            if (node is OqlSubqueryExpression) { return; } // PlanNested owns lexical query captures.
            if (node is OqlMetaExpression meta)
            {
                if (meta.Arguments.Count == 0)
                { throw new DocumentPlanningException("CK00001", ErrorMessages.MergeBindingInvalid, node.Location); }
                if (meta.Member is not ("id" or "version") || meta.Arguments.Count != 1 ||
                    meta.Arguments[0] is not OqlPathExpression { Segments.Count: 1 } receiver ||
                    receiver.Segments[0].Name is null || receiver.Segments[0].Index is not null ||
                    DocumentBindingScope.Resolve(local, receiver.Segments[0].Name, out _) is not null ||
                    receiver.Segments[0].Name != scope.TargetName && receiver.Segments[0].Name != scope.SourceName)
                { throw new DocumentPlanningException("CD01040", ErrorMessages.MergeBindingInvalid, node.Location); }
                return;
            }
            if (node is OqlPathExpression path)
            {
                string? name = path.Segments.Count == 0 ? null : path.Segments[0].Name;
                bool namedRoot = !string.IsNullOrEmpty(name) && path.Segments[0].Index is null;
                if (!namedRoot || name != scope.SourceName && name != scope.TargetName && DocumentBindingScope.Resolve(local, name, out _) is null)
                { throw new DocumentPlanningException(namedRoot && path.Segments.Count == 1 ? "CK00001" : "CD01040", ErrorMessages.MergeBindingInvalid, node.Location); }
                return;
            }
            if (node is OqlComprehensionExpression comprehension)
            {
                Visit(comprehension.Collection, local);
                var inner = new DocumentBindingScope(comprehension.Variable, local);
                Visit(comprehension.Projection, inner);
                if (comprehension.Predicate is not null) { Visit(comprehension.Predicate, inner); }
                return;
            }
            if (node is OqlQuantifiedExpression quantified)
            {
                Visit(quantified.Collection, local);
                Visit(quantified.Predicate, new DocumentBindingScope(quantified.Variable, local));
                return;
            }
            foreach (var child in NestedChildren(node)) { Visit(child, local); }
        }
    }
}
