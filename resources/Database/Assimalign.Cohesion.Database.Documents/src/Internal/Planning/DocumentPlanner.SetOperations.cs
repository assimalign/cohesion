using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    private DocumentSetOperationPlan PlanSetOperation(OqlSetOperationExpression query,
        IReadOnlyDictionary<OqlSubqueryExpression, DocumentNestedQueryPlan>? nested, IReadOnlySet<string>? outerNames)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        _cancellationToken.ThrowIfCancellationRequested();
        var left = PlanCore(query.Left, nested, outerNames);
        var right = PlanCore(query.Right, nested, outerNames);
        var leftColumns = SetColumns(left);
        var rightColumns = SetColumns(right);
        var names = new HashSet<string>(StringComparer.Ordinal);
        AddNames(left);
        AddNames(right);
        bool openMembers = OpenMembers(left) || OpenMembers(right);
        bool columnsMatch = leftColumns.Select(column => column.Name).SequenceEqual(rightColumns.Select(column => column.Name), StringComparer.Ordinal);
        var shape = query.IsValue ? DocumentSetResultShape.Value :
            SetShape(left) == DocumentSetResultShape.Columns && SetShape(right) == DocumentSetResultShape.Columns && columnsMatch
                ? DocumentSetResultShape.Columns : DocumentSetResultShape.Document;
        var heterogeneous = new bool[shape == DocumentSetResultShape.Document ? 1 : leftColumns.Count];
        var columns = shape == DocumentSetResultShape.Document
            ? new[] { new DatabaseColumn { Name = "document", Ordinal = 0, Type = DatabaseType.Json } }
            : leftColumns.Select((column, ordinal) => new DatabaseColumn
            {
                Name = query.IsValue ? "value" : column.Name, Ordinal = ordinal,
                Type = WidenSetType(column.Type, rightColumns[ordinal].Type,
                    HasHeterogeneousColumn(left, ordinal), HasHeterogeneousColumn(right, ordinal), out heterogeneous[ordinal]),
            }).ToArray();
        foreach (var order in query.OrderBy)
        {
            if (query.IsValue) { throw new DocumentPlanningException("CD01037", ErrorMessages.RawCompoundOrderUnsupported, order.Expression.Location); }
            if (order.NullPlacement is not (OqlNullPlacement.Default or OqlNullPlacement.First or OqlNullPlacement.Last))
            { throw new DocumentPlanningException("CD01032", ErrorMessages.NullPlacementInvalid, order.Expression.Location); }
            Validate(order.Expression, allowAggregate: false, functions: _functions);
            ValidateOrder(order.Expression, null);
        }
        var evaluator = new DocumentExpressionEvaluator(null, _parameters, _cancellationToken);
        return new DocumentSetOperationPlan(query, left, right, shape, columns, heterogeneous, names, openMembers,
            evaluator.PagingCount(query.Limit, "LIMIT"), evaluator.PagingCount(query.Offset, "OFFSET") ?? 0);

        void AddNames(DocumentStatementPlan operand)
        {
            if (operand is DocumentSetOperationPlan compound) { names.UnionWith(compound.MemberNames); }
            else if (!QueryLogical(operand).Query.IsValue)
            { names.UnionWith(QueryLogical(operand).Projections.Select(projection => projection.Name)); }
        }
        void ValidateOrder(OqlExpression expression, DocumentBindingScope? bound)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (expression is OqlSubqueryExpression)
            { throw new DocumentPlanningException("CK00001", ErrorMessages.CompoundOrderSubqueryUnsupported, expression.Location); }
            if (expression is OqlPathExpression { Segments.Count: > 0 } path &&
                DocumentBindingScope.Resolve(bound, path.Segments[0].Name, out _) is null && !openMembers && !names.Contains(path.Segments[0].Name!))
            { throw new DocumentPlanningException("CD01038", ErrorMessages.CompoundOrderMemberUnbound, path.Location); }
            if (expression is OqlMetaExpression or OqlStarExpression)
            { throw new DocumentPlanningException("CD01038", ErrorMessages.CompoundOrderMemberUnbound, expression.Location); }
            if (expression is OqlComprehensionExpression comprehension)
            {
                ValidateOrder(comprehension.Collection, bound);
                var inner = new DocumentBindingScope(comprehension.Variable, bound);
                ValidateOrder(comprehension.Projection, inner);
                if (comprehension.Predicate is not null) { ValidateOrder(comprehension.Predicate, inner); }
                return;
            }
            if (expression is OqlQuantifiedExpression quantified)
            {
                ValidateOrder(quantified.Collection, bound);
                ValidateOrder(quantified.Predicate, new DocumentBindingScope(quantified.Variable, bound));
                return;
            }
            foreach (var child in NestedChildren(expression)) { ValidateOrder(child, bound); }
        }
    }

    private static IReadOnlyList<DatabaseColumn> SetColumns(DocumentStatementPlan plan)
        => plan is DocumentSetOperationPlan compound ? compound.Columns : QueryLogical(plan).Projections.Select((projection, ordinal) =>
            new DatabaseColumn { Name = projection.Name, Ordinal = ordinal, Type = projection.Type }).ToArray();

    private static DocumentSetResultShape SetShape(DocumentStatementPlan plan)
        => plan is DocumentSetOperationPlan compound ? compound.Shape : QueryLogical(plan).Query.IsValue ? DocumentSetResultShape.Value :
            DocumentRowWriter.WritesBareValue(QueryLogical(plan).Query) ? DocumentSetResultShape.Document : DocumentSetResultShape.Columns;

    private static bool OpenMembers(DocumentStatementPlan plan)
        => plan is DocumentSetOperationPlan compound ? compound.HasOpenMembers :
            !QueryLogical(plan).Query.IsValue && DocumentRowWriter.WritesBareValue(QueryLogical(plan).Query);

    private static bool HasHeterogeneousColumn(DocumentStatementPlan plan, int ordinal)
        => plan is DocumentSetOperationPlan compound && compound.HeterogeneousColumns[ordinal];

    private static DatabaseType WidenSetType(DatabaseType left, DatabaseType right,
        bool leftHeterogeneous, bool rightHeterogeneous, out bool heterogeneous)
    {
        // Null describes both an all-null column and a heterogeneous one. Preserve that
        // distinction so a later operand cannot narrow an already heterogeneous header.
        heterogeneous = leftHeterogeneous || rightHeterogeneous ||
            left != DatabaseType.Null && right != DatabaseType.Null && left != right &&
            !(left is DatabaseType.Int64 or DatabaseType.Decimal && right is DatabaseType.Int64 or DatabaseType.Decimal);
        if (heterogeneous) { return DatabaseType.Null; }
        return left == right ? left : left == DatabaseType.Null ? right : right == DatabaseType.Null ? left : DatabaseType.Decimal;
    }
}
