using System.Collections.Generic;
using System.Globalization;

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
            Validate(assignment.Value, allowAggregate: false);
        }
        foreach (var path in update.Unset) { Add(path, null); }
        if (targets.Count == 0)
        {
            throw new DocumentPlanningException("COHDOC016", ErrorMessages.UpdateTargetsMissing, update.Location);
        }
        if (update.Predicate is { } predicate)
        {
            ValidateMetadata(predicate, update.Alias, storedSource: true);
            Validate(predicate, allowAggregate: false);
        }
        return new DocumentUpdatePlan(collection, update, targets.AsReadOnly(),
            ChooseAccess(update.Predicate, update.Alias, _catalog.GetIndexes(collection.Id, _snapshot)));

        void Add(OqlPathExpression path, OqlExpression? value)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var segments = new List<OqlPathSegment>();
            foreach (var segment in PathSegments(path, update.Alias))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if ((segment.Name is null) == (segment.Index is null) || segment.Index < 0)
                {
                    throw new DocumentPlanningException("COHDOC016", ErrorMessages.UpdateTargetInvalid, path.Location);
                }
                segments.Add(segment);
            }
            if (segments.Count == 0 ||
                value is null && segments[^1].Index is not null)
            {
                throw new DocumentPlanningException("COHDOC016", ErrorMessages.UpdateTargetInvalid, path.Location);
            }
            var node = paths;
            foreach (var segment in segments)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (node.IsTarget)
                {
                    throw new DocumentPlanningException("COHDOC017", ErrorMessages.UpdateTargetsOverlap, path.Location);
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
                throw new DocumentPlanningException("COHDOC017", ErrorMessages.UpdateTargetsOverlap, path.Location);
            }
            node.IsTarget = true;
            targets.Add(new DocumentUpdateTarget(path, segments.AsReadOnly(), value));
        }
    }

    private sealed class UpdatePathNode
    {
        internal Dictionary<OqlPathSegment, UpdatePathNode> Children { get; } = new();
        internal bool IsTarget { get; set; }
    }

    private DocumentDeletePlan PlanDelete(OqlDeleteExpression delete)
    {
        DocumentSystemCollections.EnsureReadOnly(delete.Collection, delete.Location);
        var collection = ResolveCollection(delete.Collection, delete.Location);
        if (delete.Predicate is { } predicate)
        {
            ValidateMetadata(predicate, delete.Alias, storedSource: true);
            Validate(predicate, allowAggregate: false);
        }
        return new DocumentDeletePlan(collection, delete,
            ChooseAccess(delete.Predicate, delete.Alias, _catalog.GetIndexes(collection.Id, _snapshot)));
    }

    private DocumentStatementPlan PlanInsert(OqlInsertExpression insert)
    {
        DocumentSystemCollections.EnsureReadOnly(insert.Collection, insert.Location);
        var collection = ResolveCollection(insert.Collection, insert.Location);
        if (insert.Select is not { } select)
        {
            if (insert.Values.Count == 0)
            {
                throw new DocumentPlanningException("COHDOC001", ErrorMessages.InsertValuesContext, insert.Location);
            }
            foreach (var pair in insert.Values)
            {
                foreach (var expression in new[] { pair.Key, pair.Value })
                {
                    if (!IsConstant(expression))
                    {
                        throw new DocumentPlanningException("COHDOC001", ErrorMessages.InsertValuesContext, expression.Location);
                    }
                    Validate(expression, allowAggregate: false);
                }
            }
            return new DocumentInsertPlan(collection, insert);
        }
        var source = Plan((OqlExpression)select);
        var logical = source is DocumentPlan plan ? plan.Logical : ((DocumentSystemCollectionPlan)source).Logical;
        int keyOrdinal = Bind(insert.KeyName);
        int? valueOrdinal = insert.ValueName is null ? null : Bind(insert.ValueName);
        return new DocumentInsertSelectPlan(collection, insert, source, keyOrdinal, valueOrdinal);

        int Bind(string? name)
        {
            for (int i = 0; i < logical.Projections.Count; i++)
            {
                if (logical.Projections[i].Name == name) { return i; }
            }
            throw new DocumentPlanningException("COHDOC001", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.InsertOutputMissing, name), insert.Location);
        }
    }

    private static void ValidateMetadata(OqlExpression expression, string? alias, bool storedSource)
    {
        switch (expression)
        {
            case OqlInExpression or OqlBetweenExpression or OqlLikeExpression or OqlCaseExpression:
                foreach (var child in PredicateChildren(expression)) { ValidateMetadata(child, alias, storedSource); }
                break;
            case OqlMetaExpression meta:
                if (!storedSource || meta.Member is not ("id" or "version") || meta.Arguments.Count > 1 ||
                    meta.Arguments.Count == 1 && (alias is null || meta.Arguments[0] is not OqlPathExpression { Segments.Count: 1 } path ||
                        path.Segments[0].Name != alias))
                {
                    throw new DocumentPlanningException("COHDOC001", ErrorMessages.MetaContextInvalid, meta.Location);
                }
                break;
            case OqlBinaryExpression binary:
                ValidateMetadata(binary.Left, alias, storedSource);
                ValidateMetadata(binary.Right, alias, storedSource);
                break;
            case OqlUnaryExpression unary:
                ValidateMetadata(unary.Operand, alias, storedSource);
                break;
            case OqlCallExpression call:
                foreach (var argument in call.Arguments) { ValidateMetadata(argument, alias, storedSource); }
                break;
            case OqlArrayExpression array:
                foreach (var element in array.Elements) { ValidateMetadata(element, alias, storedSource); }
                break;
            case OqlObjectExpression obj:
                foreach (var member in obj.Members) { ValidateMetadata(member.Value, alias, storedSource); }
                break;
        }
    }
}
