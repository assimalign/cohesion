using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Assimalign.Cohesion.Database.Execution.Internal;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed partial class GraphPlanner
{
    internal GraphCompositePlan PlanComposite(GqlQueryExpression query, IReadOnlyDictionary<string, object?>? parameters,
        bool paths, CancellationToken token)
    {
        var normalized = ParameterBindings.Normalize(parameters, name => new GraphPlanningException("CG01014",
            string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ParameterCollision, name), query.Location));
        var operation = query.Composite!.Operator;
        var pending = new Stack<GqlQueryExpression>();
        var leaves = new List<GqlQueryExpression>();
        pending.Push(query);
        while (pending.TryPop(out var operand))
        {
            token.ThrowIfCancellationRequested();
            if (operand.Composite is { } composite)
            {
                if (operand.CatalogSurface is not null || operand.TransactionCommand != GqlTransactionCommand.None ||
                    operand.ReturnStar || operand.SetQuantifier != GqlSetQuantifier.All || operand.GroupBy is not null ||
                    operand.OrderBy is null or { Count: > 0 } || operand.Offset is not null || operand.Limit is not null ||
                    !ReferenceEquals(operand, query) && operand.IsExplain)
                { throw new GraphPlanningException("CG05001", ErrorMessages.UnionOperandMalformed, operand.Location); }
                if (composite.Left is null || composite.Right is null || !Enum.IsDefined(composite.Operator))
                { throw new GraphPlanningException("CG05001", ErrorMessages.UnionOperandMalformed, operand.Location); }
                if (composite.Operator != operation)
                { throw new GraphPlanningException("CG00016", ErrorMessages.CompositeMixedUnion, operand.Location); }
                pending.Push(composite.Right);
                pending.Push(composite.Left);
            }
            else
            {
                if (operand.CatalogSurface is not null || operand.TransactionCommand != GqlTransactionCommand.None || operand.IsExplain)
                { throw new GraphPlanningException("CG05001", ErrorMessages.UnionOperandMalformed, operand.Location); }
                if (operand.Stages.Any(stage => stage is GqlInsertStage or GqlMergeStage or GqlDeleteStage or GqlSetStage or GqlRemoveStage))
                { throw new GraphPlanningException("CG05029", ErrorMessages.CompositeReadOnly, operand.Location); }
                leaves.Add(operand);
            }
        }
        var plans = new List<GraphPlan>(leaves.Count);
        var reductionWork = new GraphReductionWork(token);
        GraphFunctionKind[]? kinds = null;
        GraphProjectionKind[][]? entityKinds = null;
        foreach (var operand in leaves)
        {
            token.ThrowIfCancellationRequested();
            // Each planner owns its lexical and correlated caches; operands never import bindings.
            var shapes = new List<GraphBindingShape>();
            var planner = new GraphPlanner(_database, _snapshot)
            { _nativeExpressions = new ReductionPlanningState(null) { SharedWork = reductionWork } };
            var plan = planner.Plan(operand, normalized, paths, token,
                normalizedParameters: true, resultShapes: shapes);
            if (plan.Projections.Count == 0)
            { throw new GraphPlanningException("CG05001", ErrorMessages.UnionOperandMalformed, operand.Location); }
            if (plans.Count == 0)
            {
                kinds = new GraphFunctionKind[shapes.Count];
                entityKinds = shapes.Select(shape => new GraphProjectionKind[shape.EntityKinds.Length]).ToArray();
            }
            else if (!plan.Projections.Select(column => column.Name).SequenceEqual(plans[0].Projections.Select(column => column.Name), StringComparer.Ordinal))
            { throw new GraphPlanningException("CG01020", ErrorMessages.CompositeColumns, operand.Location); }
            for (int i = 0; i < shapes.Count; i++)
            {
                var kind = shapes[i].ValueKind & ~GraphFunctionKind.Null;
                if ((kind & GraphFunctionKind.Number) != 0) { kind |= GraphFunctionKind.Number; }
                if (kind != GraphFunctionKind.Unknown)
                {
                    if (kinds![i] != GraphFunctionKind.Unknown && (kinds[i] & kind) == 0)
                    { throw new GraphPlanningException("CG02017", ErrorMessages.CompositeShapes, operand.Location); }
                    kinds![i] = kinds[i] == GraphFunctionKind.Unknown ? kind : kinds[i] & kind;
                }
                // Nested entity lists preserve the existing scalar/entity wire boundary.
                int depth = Math.Min(entityKinds![i].Length, shapes[i].EntityKinds.Length);
                for (int level = 1; level < depth; level++)
                {
                    var left = entityKinds[i][level];
                    var right = shapes[i].EntityKinds[level].Kind;
                    if (left is GraphProjectionKind.Node or GraphProjectionKind.Relationship or GraphProjectionKind.Path &&
                        right is GraphProjectionKind.Node or GraphProjectionKind.Relationship or GraphProjectionKind.Path && left != right)
                    { throw new GraphPlanningException("CG02017", ErrorMessages.CompositeShapes, operand.Location); }
                    if (right is GraphProjectionKind.Node or GraphProjectionKind.Relationship or GraphProjectionKind.Path)
                    { entityKinds[i][level] = right; }
                }
            }
            plans.Add(plan);
        }
        return new(query, operation, plans.AsReadOnly(), plans.SelectMany(plan => plan.Warnings).ToArray(), kinds!);
    }
}
