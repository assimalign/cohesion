using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Assimalign.Cohesion.Database.Execution.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed partial class GraphPlanner
{
    internal GraphCompositePlan PlanComposite(GqlQueryExpression query, IReadOnlyDictionary<string, object?>? parameters,
        bool paths, CancellationToken token, IReadOnlyDictionary<string, GraphBindingShape>? inputShapes = null, GraphReductionWork? sharedWork = null, GraphTokenResolver? tokens = null)
    {
        ValidateCompositeReadOnly(query, token);
        tokens ??= new GraphTokenResolver(_database.Catalog, _snapshot);
        var normalized = inputShapes is not null ? parameters : ParameterBindings.Normalize(parameters, name => new GraphPlanningException("CG01014",
            string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ParameterCollision, name), query.Location));
        var operation = query.Composite!.Operator;
        var pending = new Stack<(GqlQueryExpression Query, int Parent, bool Right)>();
        var nodes = new List<GraphCompositeNode>();
        var leaves = new List<GqlQueryExpression>();
        GqlCompositeOperator? unionMode = null;
        bool homogeneousUnion = operation is GqlCompositeOperator.Union or GqlCompositeOperator.UnionAll;
        pending.Push((query, -1, false));
        while (pending.TryPop(out var step))
        {
            token.ThrowIfCancellationRequested();
            var operand = step.Query;
            int node = nodes.Count;
            nodes.Add(new(operand.Composite?.Operator, -1, -1, operand.Composite is null ? leaves.Count : -1));
            if (step.Parent >= 0)
            {
                nodes[step.Parent] = step.Right ? nodes[step.Parent] with { Right = node } : nodes[step.Parent] with { Left = node };
            }
            if (operand.Composite is { } composite)
            {
                if (operand.CatalogSurface is not null || operand.TransactionCommand != GqlTransactionCommand.None ||
                    operand.ReturnStar || operand.SetQuantifier != GqlSetQuantifier.All || operand.GroupBy is not null ||
                    operand.OrderBy is null or { Count: > 0 } || operand.Offset is not null || operand.Limit is not null ||
                    !ReferenceEquals(operand, query) && operand.IsExplain)
                { throw new GraphPlanningException("CG05001", ErrorMessages.UnionOperandMalformed, operand.Location); }
                if (composite.Left is null || composite.Right is null || !Enum.IsDefined(composite.Operator))
                { throw new GraphPlanningException("CG05001", ErrorMessages.UnionOperandMalformed, operand.Location); }
                homogeneousUnion &= composite.Operator == operation;
                if (composite.Operator is GqlCompositeOperator.Union or GqlCompositeOperator.UnionAll)
                {
                    if (unionMode is not null && unionMode != composite.Operator)
                    { throw new GraphPlanningException("CG00016", ErrorMessages.CompositeMixedUnion, operand.Location); }
                    unionMode = composite.Operator;
                }
                pending.Push((composite.Right, node, true));
                pending.Push((composite.Left, node, false));
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
        var reductionWork = sharedWork ?? new GraphReductionWork(token);
        GraphBindingShape[]? combinedShapes = null;
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
                normalizedParameters: true, resultShapes: shapes, inputShapes: inputShapes, tokens: tokens);
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
                // A skipped fallback cannot supply runtime nullability observations. Keep
                // these extra proofs local to composite planning; ordinary plans are unchanged.
                if (shapes[i].ResultNullable is null && plan.Projections[i].Expression is not GqlLiteralExpression)
                {
                    var expression = plan.Projections[i].Expression;
                    while (expression is GqlCastExpression cast) { expression = cast.Operand; }
                    bool? nullable = expression switch
                    {
                        GqlLiteralExpression value => value.Value is null,
                        GqlAggregateExpression { Function: GqlAggregateFunction.Count } => false,
                        _ => null,
                    };
                    if (nullable is not null) { shapes[i] = shapes[i] with { ResultNullable = nullable }; }
                }
                if (plan.Projections[i].Expression is GqlLiteralExpression literal)
                { shapes[i] = shapes[i] with { Type = GraphCompositeMetadata.Type(literal.Value),
                    ValueKind = literal.Value is null ? GraphFunctionKind.Null : shapes[i].ValueKind,
                    ResultNullable = literal.Value is null }; }
                // Paths, like nodes and relationships, retain the proven projection kind
                // when the ordinary scalar-shape walk has no root entity representation.
                if (plan.Projections[i].Kind is GraphProjectionKind.Node or GraphProjectionKind.Relationship or GraphProjectionKind.Path)
                {
                    var rootKinds = (GraphEntityShape[])shapes[i].EntityKinds.Clone();
                    rootKinds[0] = new(plan.Projections[i].Kind, plan.Projections[i].Optional);
                    shapes[i] = shapes[i] with { EntityKinds = rootKinds };
                }
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
            if (combinedShapes is null) { combinedShapes = shapes.ToArray(); }
            else
            {
                for (int i = 0; i < shapes.Count; i++) { combinedShapes[i] = MergeCompositeShape(combinedShapes[i], shapes[i]); }
            }
            plans.Add(plan);
        }
        var projections = plans[0].Projections.Select((projection, i) => projection with
        {
            Type = combinedShapes![i].Type,
            Kind = combinedShapes[i].EntityKinds[0].Kind is GraphProjectionKind.Node or GraphProjectionKind.Relationship or GraphProjectionKind.Path
                ? combinedShapes[i].EntityKinds[0].Kind : combinedShapes[i].Entities[0] ? GraphProjectionKind.EntityValue : GraphProjectionKind.Scalar,
            MayBeComposite = combinedShapes[i].Composites[0],
            Optional = combinedShapes[i].EntityKinds[0].Optional,
            ResultNullable = combinedShapes[i].ResultNullable,
        }).ToArray();
        return new(query, operation, plans.AsReadOnly(), tokens.Warnings, kinds!, projections, combinedShapes!, nodes.AsReadOnly(), homogeneousUnion);
    }

    private static void ValidateCompositeReadOnly(GqlQueryExpression query, CancellationToken token)
    {
        var pending = new Stack<GqlQueryExpression>();
        pending.Push(query);
        while (pending.TryPop(out var operand))
        {
            token.ThrowIfCancellationRequested();
            if (operand is null)
            { throw new GraphPlanningException("CG05001", ErrorMessages.UnionOperandMalformed, query.Location); }
            if (operand.Composite is { } composite)
            {
                if (composite.Left is null || composite.Right is null || !Enum.IsDefined(composite.Operator))
                { throw new GraphPlanningException("CG05001", ErrorMessages.UnionOperandMalformed, operand.Location); }
                pending.Push(composite.Right); pending.Push(composite.Left);
            }
            foreach (var stage in operand.Stages)
            {
                if (stage is GqlInsertStage or GqlMergeStage or GqlDeleteStage or GqlSetStage or GqlRemoveStage)
                { throw new GraphPlanningException("CG05029", ErrorMessages.CompositeReadOnly, operand.Location); }
                if (stage is GqlCompositeStage nested) { pending.Push(new GqlQueryExpression(nested.Query, stage.Location)); }
            }
        }
    }

    private static GraphBindingShape MergeCompositeShape(GraphBindingShape left, GraphBindingShape right)
    {
        bool[] Combine(bool[] a, bool[] b, bool every = false) => Enumerable.Range(0, Math.Max(a.Length, b.Length))
            .Select(i => every ? i < a.Length && a[i] && i < b.Length && b[i] : i < a.Length && a[i] || i < b.Length && b[i]).ToArray();
        var entities = Enumerable.Range(0, Math.Max(left.EntityKinds.Length, right.EntityKinds.Length)).Select(i =>
        {
            var a = i < left.EntityKinds.Length ? left.EntityKinds[i] : default;
            var b = i < right.EntityKinds.Length ? right.EntityKinds[i] : default;
            bool Empty(GraphBindingShape shape) =>
                (i >= shape.Entities.Length || !shape.Entities[i]) &&
                (i >= shape.Composites.Length || !shape.Composites[i]) &&
                (i >= shape.Scalars.Length || !shape.Scalars[i]);
            return new GraphEntityShape(a.Kind == b.Kind ? a.Kind : Empty(left) ? b.Kind : Empty(right) ? a.Kind : GraphProjectionKind.Scalar,
                a.Optional || b.Optional || (left.ValueKind & GraphFunctionKind.Null) != 0 || (right.ValueKind & GraphFunctionKind.Null) != 0);
        }).ToArray();
        var type = left.ValueKind == GraphFunctionKind.Null ? right.Type : right.ValueKind == GraphFunctionKind.Null ? left.Type
            : left.Type == DatabaseType.Null || right.Type == DatabaseType.Null ? DatabaseType.Null : GraphCompositeMetadata.Widen(left.Type, right.Type);
        return new(Combine(left.Entities, right.Entities), Combine(left.Composites, right.Composites), Combine(left.Scalars, right.Scalars, true), entities, type)
        { Groups = Combine(left.Groups, right.Groups), ValueKind = left.ValueKind == GraphFunctionKind.Unknown || right.ValueKind == GraphFunctionKind.Unknown
                ? GraphFunctionKind.Unknown : left.ValueKind | right.ValueKind,
          ResultNullable = left.ResultNullable == true || right.ResultNullable == true ? true : left.ResultNullable == false && right.ResultNullable == false ? false : null };
    }
}
