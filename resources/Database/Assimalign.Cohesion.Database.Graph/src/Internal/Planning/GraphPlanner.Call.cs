using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed partial class GraphPlanner
{
    private GraphCallStagePlan PlanCall(GqlCallStage call, GqlQueryExpression statement, IReadOnlyDictionary<string, BindingKind> outer,
        Func<string, GraphBindingShape> inputShape,
        GraphTokenResolver tokens, GraphReductionWork work, CancellationToken token, out IReadOnlyList<GraphBindingShape> outputShapes)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (call.Body is null || call.ImportAll && call.Imports is { Count: > 0 })
        { throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, call.Location); }
        ValidateCallReadOnly(call.Body, token);
        IReadOnlyList<string>? legacyImports = null;
        var body = call.Imports is null && !call.ImportAll ? NormalizeImport(call.Body) : call.Body;
        var names = call.ImportAll ? outer.Keys.ToArray() : call.Imports ?? legacyImports ?? [];
        var imports = new Dictionary<string, GraphBindingShape>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(name) || !outer.ContainsKey(name))
            { throw new GraphPlanningException("CG05001", ErrorMessages.CallImportMissing, call.Location); }
            if (!imports.TryAdd(name, inputShape(name)))
            { throw new GraphPlanningException("CG03003", ErrorMessages.CallImportDuplicate, call.Location); }
        }
        var planner = new GraphPlanner(_database, _snapshot)
        { _parsed = statement.Text is not null || _parsed,
            _nativeExpressions = new ReductionPlanningState(null) { SharedWork = work } };
        GraphCompositePlan? composite = null;
        GraphPlan inner;
        if (body.Composite is not null)
        {
            composite = planner.PlanComposite(body, _parameters, false, token, imports, work, tokens, imports);
            inner = new GraphPlan(body, []) { Projections = composite.Projections, Parameters = _parameters };
            outputShapes = composite.Shapes;
        }
        else
        {
            var shapes = new List<GraphBindingShape>();
            inner = planner.Plan(body, _parameters, token: token, normalizedParameters: true, resultShapes: shapes,
                inputShapes: imports, tokens: tokens, callImports: imports);
            // Preserve literal metadata even when OPTIONAL emits only NULL, as composites do.
            for (int i = 0; i < shapes.Count; i++)
            {
                if (inner.Projections[i].Expression is not GqlLiteralExpression literal) { continue; }
                shapes[i] = shapes[i] with { Type = GraphCompositeMetadata.Type(literal.Value),
                    ResultNullable = literal.Value is null };
            }
            outputShapes = shapes;
        }
        var exported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projection in inner.Projections)
        {
            if (projection.BindingName is null)
            { throw new GraphPlanningException("CG05001", ErrorMessages.CallReturnAliasRequired, projection.Expression.Location ?? call.Location); }
            if (!exported.Add(projection.BindingName))
            { throw new GraphPlanningException("CG01015", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                ErrorMessages.DuplicateResultColumn, projection.BindingName), projection.Expression.Location ?? call.Location); }
            if (outer.ContainsKey(projection.BindingName))
            { throw new GraphPlanningException("CG01024", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                ErrorMessages.CallReturnShadow, projection.BindingName), projection.Expression.Location ?? call.Location); }
        }
        return new(call, inner, composite, imports.Keys.ToArray());

        GqlQueryExpression NormalizeImport(GqlQueryExpression query)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            token.ThrowIfCancellationRequested();
            if (query.Composite is { } pair)
            { return new GqlQueryExpression(new GqlCompositeQuery(NormalizeImport(pair.Left), pair.Operator, NormalizeImport(pair.Right)), query.Location); }
            var stages = query.Stages;
            if (stages.FirstOrDefault() is GqlCompositeStage nested)
            { return new GqlQueryExpression(new GqlStage[] { new GqlCompositeStage(NormalizeImport(new GqlQueryExpression(nested.Query)).Composite!, nested.Location) }.Concat(stages.Skip(1)).ToArray(), query.Location); }
            bool all = false;
            IReadOnlyList<string> imported = [];
            if (stages.FirstOrDefault() is GqlReturnStage { IsWith: true } leading)
            {
                if (leading.Projections is null)
                { throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, leading.Location); }
                if (leading.SetQuantifier != GqlSetQuantifier.All || leading.GroupBy is not null || leading.OrderBy is null or { Count: > 0 } ||
                    leading.Offset is not null || leading.Limit is not null || leading.Predicate is not null ||
                    leading.Projections.Any(item => item.Alias is not null || item.Expression is not GqlVariableExpression))
                { throw new GraphPlanningException("CK00001", ErrorMessages.CallImportWithUnsupported, leading.Location); }
                all = leading.ReturnStar;
                imported = leading.Projections.Select(item => ((GqlVariableExpression)item.Expression).Variable).ToArray();
                if (stages.Skip(1).Any(stage => stage is GqlReturnStage { IsWith: true } or GqlNextStage or GqlCompositeStage))
                { throw new GraphPlanningException("CK00001", ErrorMessages.CallImportWithUnsupported, leading.Location); }
                query = new GqlQueryExpression(stages.Skip(1).ToArray(), query.Location);
            }
            // Translate only when no later scope boundary changes importing WITH visibility.
            if (all) { imported = outer.Keys.ToArray(); }
            if (legacyImports is not null && !legacyImports.Order(StringComparer.Ordinal).SequenceEqual(imported.Order(StringComparer.Ordinal)))
            { throw new GraphPlanningException("CK00001", ErrorMessages.CallImportWithUnsupported, query.Location); }
            legacyImports ??= imported;
            return query;
        }
    }

    // GQ-N2 lifts this single admission boundary for writes and unit subqueries.
    private static void ValidateCallReadOnly(GqlQueryExpression body, CancellationToken token)
    {
        var pending = new Stack<GqlQueryExpression>();
        pending.Push(body);
        while (pending.TryPop(out var query))
        {
            token.ThrowIfCancellationRequested();
            if (query is null) { throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, body.Location); }
            if (query.CatalogSurface is not null || query.SchemaCommand is not null || query.TransactionCommand != GqlTransactionCommand.None || query.IsExplain)
            { throw new GraphPlanningException("CK00001", ErrorMessages.CallReadOnlyReturnRequired, query.Location); }
            if (query.Composite is { } composite) { pending.Push(composite.Right); pending.Push(composite.Left); continue; }
            var stages = query.Stages;
            if (stages.Any(stage => stage is GqlInsertStage or GqlMergeStage or GqlDeleteStage or GqlSetStage or GqlRemoveStage) ||
                stages.LastOrDefault() is not (GqlReturnStage { IsWith: false } or GqlCompositeStage))
            { throw new GraphPlanningException("CK00001", ErrorMessages.CallReadOnlyReturnRequired, query.Location); }
            foreach (var stage in stages)
            {
                if (stage is GqlCallStage call) { pending.Push(call.Body); }
                if (stage is GqlCompositeStage nested) { pending.Push(new GqlQueryExpression(nested.Query, nested.Location)); }
            }
        }
    }
}
