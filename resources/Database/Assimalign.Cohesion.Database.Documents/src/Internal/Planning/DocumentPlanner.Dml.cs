using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    private DocumentUpdatePlan PlanUpdate(OqlUpdateExpression update)
    {
        DocumentSystemCollections.EnsureReadOnly(update.Collection, update.Location);
        var collection = ResolveCollection(update.Collection, update.Location);
        var targets = new List<DocumentUpdateTarget>();
        var paths = new UpdatePathNode();
        foreach (var assignment in update.Assignments)
        {
            Add(assignment.Path, assignment.Value);
            ValidateMetadata(assignment.Value, update.Alias, storedSource: true);
            Validate(assignment.Value, allowAggregate: false, functions: _functions);
        }
        foreach (var path in update.Unset) { Add(path, null); }
        if (targets.Count == 0)
        {
            throw new DocumentPlanningException("CD02016", ErrorMessages.UpdateTargetsMissing, update.Location);
        }
        if (update.Predicate is { } predicate)
        {
            ValidateMetadata(predicate, update.Alias, storedSource: true);
            Validate(predicate, allowAggregate: false, functions: _functions);
        }
        return new DocumentUpdatePlan(collection, update, targets.AsReadOnly(),
            ChooseAccess(update.Predicate, update.Alias, _catalog.GetIndexes(collection.Id, _snapshot)),
            PlanReturning(update.Returning, update.Collection, update.Alias));

        void Add(OqlPathExpression path, OqlExpression? value)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var segments = new List<OqlPathSegment>();
            foreach (var segment in PathSegments(path, update.Alias))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if ((segment.Name is null) == (segment.Index is null) || segment.Index < 0)
                {
                    throw new DocumentPlanningException("CD02016", ErrorMessages.UpdateTargetInvalid, path.Location);
                }
                segments.Add(segment);
            }
            if (segments.Count == 0 ||
                value is null && segments[^1].Index is not null)
            {
                throw new DocumentPlanningException("CD02016", ErrorMessages.UpdateTargetInvalid, path.Location);
            }
            var node = paths;
            foreach (var segment in segments)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (node.IsTarget)
                {
                    throw new DocumentPlanningException("CD01017", ErrorMessages.UpdateTargetsOverlap, path.Location);
                }
                if (!node.Children.TryGetValue(segment, out var child))
                {
                    child = new UpdatePathNode();
                    node.Children.Add(segment, child);
                }
                node = child;
            }
            if (node.IsTarget || node.Children.Count > 0)
            {
                throw new DocumentPlanningException("CD01017", ErrorMessages.UpdateTargetsOverlap, path.Location);
            }
            node.IsTarget = true;
            targets.Add(new DocumentUpdateTarget(path, segments.AsReadOnly(), value));
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("UpdatePathNode")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentPlanner.UpdatePathNode.DebugView))]
    private sealed class UpdatePathNode
    {
        internal Dictionary<OqlPathSegment, UpdatePathNode> Children { get; } = new();
        internal bool IsTarget { get; set; }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentPlanner.UpdatePathNode value)
            {
                _ = value;
            }
        }
    }

    private DocumentDeletePlan PlanDelete(OqlDeleteExpression delete)
    {
        DocumentSystemCollections.EnsureReadOnly(delete.Collection, delete.Location);
        var collection = ResolveCollection(delete.Collection, delete.Location);
        if (delete.Predicate is { } predicate)
        {
            ValidateMetadata(predicate, delete.Alias, storedSource: true);
            Validate(predicate, allowAggregate: false, functions: _functions);
        }
        return new DocumentDeletePlan(collection, delete,
            ChooseAccess(delete.Predicate, delete.Alias, _catalog.GetIndexes(collection.Id, _snapshot)),
            PlanReturning(delete.Returning, delete.Collection, delete.Alias));
    }

    private DocumentStatementPlan PlanWrite(OqlExpression statement, string destination,
        IReadOnlyList<(OqlExpression Key, OqlExpression Value)> values, OqlSelectExpression? select,
        string? keyName, string? valueName, IReadOnlyDictionary<OqlSubqueryExpression, DocumentNestedQueryPlan>? nested = null)
    {
        string verb = statement is OqlUpsertExpression ? "UPSERT" : "INSERT";
        DocumentSystemCollections.EnsureReadOnly(destination, statement.Location);
        var collection = ResolveCollection(destination, statement.Location);
        var returning = statement switch
        {
            OqlInsertExpression insert => PlanReturning(insert.Returning, destination, insert.Alias),
            OqlUpsertExpression upsert => PlanReturning(upsert.Returning, destination, upsert.Alias),
            _ => null,
        };
        if (select is null)
        {
            if (values.Count == 0)
            {
                throw new DocumentPlanningException("CD05001", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteValuesContext, verb), statement.Location);
            }
            foreach (var pair in values)
            {
                foreach (var expression in new[] { pair.Key, pair.Value })
                {
                    if (!IsWriteValue(expression))
                    {
                        throw new DocumentPlanningException("CD05001", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteValuesContext, verb), expression.Location);
                    }
                    Validate(expression, allowAggregate: false, functions: _functions);
                }
            }
            return statement is OqlUpsertExpression upsert
                ? new DocumentUpsertPlan(collection, upsert, Returning: returning)
                : new DocumentInsertPlan(collection, (OqlInsertExpression)statement, returning);
        }
        var source = PlanCore(select, nested);
        var logical = QueryLogical(source);
        int keyOrdinal = Bind(keyName);
        int? valueOrdinal = valueName is null ? null : Bind(valueName);
        return statement is OqlUpsertExpression upsertSelect
            ? new DocumentUpsertPlan(collection, upsertSelect, source, keyOrdinal, valueOrdinal, returning)
            : new DocumentInsertSelectPlan(collection, (OqlInsertExpression)statement, source, keyOrdinal, valueOrdinal, returning);

        int Bind(string? name)
        {
            for (int i = 0; i < logical.Projections.Count; i++)
            {
                if (logical.Projections[i].Name == name) { return i; }
            }
            throw new DocumentPlanningException("CD05001", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.WriteOutputMissing, verb, name), statement.Location);
        }
    }

    private static bool IsWriteValue(OqlExpression expression)
        => HasSubquery(expression) ? IsNestedWriteValue(expression, null) : IsConstant(expression);

    private static bool IsNestedWriteValue(OqlExpression expression, DocumentBindingScope? bound) => expression switch
    {
        OqlSubqueryExpression => true,
        OqlComprehensionExpression comprehension => IsNestedWriteValue(comprehension.Collection, bound) &&
            IsNestedWriteValue(comprehension.Projection, new DocumentBindingScope(comprehension.Variable, bound)) &&
            (comprehension.Predicate is null || IsNestedWriteValue(comprehension.Predicate, new DocumentBindingScope(comprehension.Variable, bound))),
        OqlQuantifiedExpression quantified => IsNestedWriteValue(quantified.Collection, bound) &&
            IsNestedWriteValue(quantified.Predicate, new DocumentBindingScope(quantified.Variable, bound)),
        OqlPathExpression { Segments.Count: > 0 } path => DocumentBindingScope.Resolve(bound, path.Segments[0].Name, out _) is not null,
        OqlMetaExpression or OqlStarExpression => false,
        _ => IsConstant(expression, bound) || NestedChildren(expression).Any() && NestedChildren(expression).All(child => IsNestedWriteValue(child, bound)),
    };

    private DocumentLogicalPlan? PlanReturning(OqlReturningExpression? returning, string collection, string? alias)
    {
        if (returning is null) { return null; }
        foreach (var projection in returning.Projections)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (projection.Alias is { Length: 0 })
            { throw new DocumentPlanningException("CD01026", ErrorMessages.ReturningNameInvalid, projection.Expression.Location); }
            Validate(projection.Expression, allowAggregate: false, allowStar: !returning.IsValue, functions: _functions);
        }
        // A destination without AS is its own binding, independently of an INSERT SELECT source.
        var logical = CreateLogicalPlan(new OqlSelectExpression(collection, alias ?? collection, returning.Projections,
            null, [], null, [], false, returning.IsValue, location: returning.Location), _cancellationToken, _parameters, _functions);
        if (logical.Projections.Any(projection => projection.Name.Length == 0))
        { throw new DocumentPlanningException("CD01026", ErrorMessages.ReturningNameInvalid, returning.Location); }
        return logical;
    }

    private static void ValidateMetadata(OqlExpression expression, string? alias, bool storedSource, IReadOnlySet<string>? outerNames = null)
    {
        switch (expression)
        {
            case OqlComprehensionExpression comprehension:
                ValidateMetadata(comprehension.Collection, alias, storedSource, outerNames);
                ValidateMetadata(comprehension.Projection, comprehension.Variable == alias ? null : alias, storedSource, outerNames);
                if (comprehension.Predicate is not null)
                { ValidateMetadata(comprehension.Predicate, comprehension.Variable == alias ? null : alias, storedSource, outerNames); }
                break;
            case OqlQuantifiedExpression quantified:
                ValidateMetadata(quantified.Collection, alias, storedSource, outerNames);
                ValidateMetadata(quantified.Predicate, quantified.Variable == alias ? null : alias, storedSource, outerNames);
                break;
            case OqlInExpression or OqlBetweenExpression or OqlLikeExpression or OqlCaseExpression:
                foreach (var child in PredicateChildren(expression)) { ValidateMetadata(child, alias, storedSource, outerNames); }
                break;
            case OqlMetaExpression meta:
                bool captured = meta.Arguments.Count == 1 && meta.Arguments[0] is OqlPathExpression { Segments.Count: 1 } capturedSource &&
                    capturedSource.Segments[0].Name is string capturedName && outerNames?.Contains(capturedName) == true;
                if (meta.Member is not ("id" or "version") || meta.Arguments.Count > 1 || !captured && (!storedSource ||
                    meta.Arguments.Count == 1 && (alias is null || meta.Arguments[0] is not OqlPathExpression { Segments.Count: 1 } path ||
                        path.Segments[0].Name != alias)))
                {
                    throw new DocumentPlanningException("CD05001", ErrorMessages.MetaContextInvalid, meta.Location);
                }
                break;
            case OqlBinaryExpression binary:
                ValidateMetadata(binary.Left, alias, storedSource, outerNames);
                ValidateMetadata(binary.Right, alias, storedSource, outerNames);
                break;
            case OqlUnaryExpression unary:
                ValidateMetadata(unary.Operand, alias, storedSource, outerNames);
                break;
            case OqlCallExpression call:
                foreach (var argument in call.Arguments) { ValidateMetadata(argument, alias, storedSource, outerNames); }
                break;
            case OqlArrayExpression array:
                foreach (var element in array.Elements) { ValidateMetadata(element, alias, storedSource, outerNames); }
                break;
            case OqlObjectExpression obj:
                foreach (var member in obj.Members) { ValidateMetadata(member.Value, alias, storedSource, outerNames); }
                break;
        }
    }
}
