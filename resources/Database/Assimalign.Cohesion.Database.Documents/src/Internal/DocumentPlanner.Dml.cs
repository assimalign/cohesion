using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
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
