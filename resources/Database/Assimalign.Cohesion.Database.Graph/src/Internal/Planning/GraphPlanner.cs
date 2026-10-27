using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Threading;
using Assimalign.Cohesion.Database.Execution.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[global::System.Diagnostics.DebuggerDisplay("GraphPlanner")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlanner.DebugView))]
internal sealed partial class GraphPlanner
{
    private readonly GraphDatabase _database;
    private readonly TransactionSnapshot _snapshot;
    private IReadOnlyDictionary<string, object?>? _parameters;
    private Dictionary<GqlNamedFunctionCallExpression, GraphFunction>? _functions;
    private Dictionary<GqlNamedFunctionCallExpression, GqlExpression?>? _nativeExpressions;

    /// <summary>Initializes a new instance of the <see cref="GraphPlanner"/> class.</summary>
    /// <param name="database">The graph database whose catalog and store resolve labels, relationship types, and indexes.</param>
    /// <param name="snapshot">The transaction snapshot that catalog and index lookups observe.</param>
    public GraphPlanner(GraphDatabase database, TransactionSnapshot snapshot)
    {
        _database = database;
        _snapshot = snapshot;
    }

    /// <summary>
    /// Validates a statement and chooses each MATCH pattern's anchor. A label or relationship type
    /// the database does not have is not an error (#1228): it matches nothing, and a read-only
    /// statement reports it as a <c>CG01010</c>/<c>CG01011</c> warning. Only a read-only
    /// statement warns, as Neo4j's <c>CheckForUnresolvedTokens</c> runs only when
    /// <c>query.readOnly</c> (<c>cypher-planner/.../compiler/planner/CheckForUnresolvedTokens.scala:53</c>).
    /// </summary>
    /// <param name="query">The statement to plan.</param>
    /// <param name="parameters">The named bindings; normalization and validation precede execution.</param>
    /// <param name="paths">Whether path-request projection shape takes precedence over duplicate column names.</param>
    /// <param name="token">Cancellation observed while validating and indexing expression structure.</param>
    /// <param name="normalizedParameters">Whether these are the statement's already captured bindings for replanning.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="DatabaseException">The statement is invalid (<c>CG05001</c>, <c>CG03003</c>, <c>CG01015</c>).</exception>
    internal GraphPlan Plan(GqlQueryExpression query, IReadOnlyDictionary<string, object?>? parameters = null, bool paths = false, CancellationToken token = default, bool normalizedParameters = false)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            _parameters = normalizedParameters ? parameters : ParameterBindings.Normalize(parameters, name => new GraphPlanningException(
                "CG01014", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ParameterCollision, name), query.Location));
            return PlanCore(query, paths, token);
        }
        catch (GraphPlanningException exception) when (exception.Diagnostics[0].Start is null && query.Location is not null)
        {
            throw exception.At(query.Location);
        }
    }

    private GraphPlan PlanCore(GqlQueryExpression statement, bool pathsRequest, CancellationToken token)
    {
        if (_parameters is not null) { _parameters = new Dictionary<string, object?>(_parameters, StringComparer.Ordinal); }
        var query = statement;
        var stages = statement.Stages;
        var planned = new List<GraphStagePlan>();
        var deletedVariables = new HashSet<string>(StringComparer.Ordinal);
        var shapes = new Dictionary<string, GraphBindingShape>(StringComparer.Ordinal);
        bool writes = stages.Any(stage => stage is GqlInsertStage or GqlDeleteStage or GqlSetStage or GqlRemoveStage);
        if (pathsRequest && writes)
        { throw new GraphPlanningException("CG05001", ErrorMessages.PathProjectionRequired, statement.Location); }
        var snapshotted = new HashSet<string>(StringComparer.Ordinal);
        var variables = new Dictionary<string, BindingKind>(StringComparer.Ordinal);
        var declarations = new List<string>();
        var mandatoryVariables = new HashSet<string>(StringComparer.Ordinal);
        var boundVariables = new HashSet<string>(StringComparer.Ordinal);
        var groupVariables = new HashSet<string>(StringComparer.Ordinal);
        var pathShapes = new Dictionary<GqlPathPattern, GraphPathShape>(ReferenceEqualityComparer.Instance);
        Dictionary<string, int>? orderAliases = null;
        Dictionary<GqlExpression, int>? orderProjections = null;
        GraphGroupingPlan? grouping = null;
        var projectedEntities = new HashSet<string>(StringComparer.Ordinal);
        var projections = new List<GraphProjectionPlan>();
        var clauses = new List<GraphMatchClausePlan>();
        var tokens = new GraphTokenResolver(_database.Catalog, _snapshot);
        bool matchesNothing = false;
        bool optional = false;
        bool forValue = false;
        if (statement.MatchClauses is null)
        { throw new GraphPlanningException("CG05001", ErrorMessages.MatchClausePathsRequired, statement.Location); }
        if (stages.Count == 0 || statement.OrderBy is null || stages[^1] is GqlReturnStage { IsWith: true })
        { throw new GraphPlanningException("CG05001", ErrorMessages.PatternOrReturnRequired, statement.Location); }
        for (int stageIndex = 0; stageIndex < stages.Count; stageIndex++)
        {
            token.ThrowIfCancellationRequested();
            var stage = stages[stageIndex];
            if (stage is GqlInsertStage { Paths: null or { Count: 0 } } or
                GqlDeleteStage { Variables: null or { Count: 0 } } or
                GqlSetStage { Items: null or { Count: 0 } } or GqlRemoveStage { Items: null or { Count: 0 } } or
                GqlReturnStage { Projections: null } or GqlReturnStage { IsWith: false, Predicate: not null })
            { throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, stage.Location ?? statement.Location); }
            query = StageQuery(stage, statement.Location);
            orderAliases = null;
            orderProjections = null;
            grouping = null;
            projections = [];
            projectedEntities = new(StringComparer.Ordinal);
            clauses = [];
            matchesNothing = false;
            optional = false;
            switch (stage)
            {
                case GqlSetStage set:
                    foreach (var item in set.Items)
                    {
                        if (item is null || string.IsNullOrWhiteSpace(item.Variable))
                        { throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, stage.Location); }
                        RequireVariable(item.Variable, entity: true);
                        switch (item)
                        {
                            case GqlSetPropertyItem property when !string.IsNullOrWhiteSpace(property.Property) && property.Value is not null:
                                ValidateExpression(property.Value);
                                break;
                            case GqlSetPropertiesItem map when map.Properties is not null:
                                foreach (var pair in map.Properties)
                                {
                                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                                    { throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, item.Location); }
                                    ValidateExpression(pair.Value);
                                }
                                break;
                            case GqlSetLabelsItem labels when labels.Labels is { Count: > 0 } && labels.Labels.All(name => !string.IsNullOrWhiteSpace(name)):
                                if (variables[item.Variable] != BindingKind.Node)
                                { throw new GraphPlanningException("CG03003", ErrorMessages.EntityBindingRequired, item.Location); }
                                foreach (string name in labels.Labels) { tokens.IntroduceLabel(name); }
                                break;
                            default: throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, item.Location);
                        }
                    }
                    break;
                case GqlRemoveStage remove:
                    foreach (var item in remove.Items)
                    {
                        if (item is null || string.IsNullOrWhiteSpace(item.Variable))
                        { throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, stage.Location); }
                        RequireVariable(item.Variable, entity: true);
                        if (item is GqlRemoveLabelsItem labels && labels.Labels is { Count: > 0 } && labels.Labels.All(name => !string.IsNullOrWhiteSpace(name)))
                        {
                            if (variables[item.Variable] != BindingKind.Node)
                            { throw new GraphPlanningException("CG03003", ErrorMessages.EntityBindingRequired, item.Location); }
                        }
                        else if (item is not GqlRemovePropertyItem property || string.IsNullOrWhiteSpace(property.Property))
                        { throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, item.Location); }
                    }
                    break;
                case GqlLetStage let:
                    if (let.Bindings is null || let.Bindings.Count == 0 || let.Bindings.Any(item => item?.Expression is null || string.IsNullOrEmpty(item.Alias)))
                    { throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, stage.Location ?? statement.Location); }
                    foreach (var binding in let.Bindings)
                    {
                        ValidateExpression(binding.Expression);
                        AddValue(binding.Alias!, binding.Expression);
                    }
                    break;
                case GqlFilterStage filter:
                    if (filter.Predicate is null) { throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, stage.Location ?? statement.Location); }
                    ValidateExpression(filter.Predicate);
                    break;
                case GqlForStage loop:
                    if (loop.List is null || string.IsNullOrEmpty(loop.Variable)) { throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, stage.Location ?? statement.Location); }
                    forValue = true;
                    ValidateExpression(loop.List);
                    forValue = false;
                    AddValue(loop.Variable, loop.List, element: true);
                    break;
                case GqlNextStage:
                    if (stageIndex == 0 || stageIndex == stages.Count - 1 ||
                        stages[stageIndex - 1] is GqlReturnStage { IsWith: true } ||
                        stages[stageIndex - 1] is not GqlReturnStage && !stages.Take(stageIndex).Reverse().TakeWhile(item => item is not GqlNextStage).Any(item => item is GqlInsertStage or GqlDeleteStage or GqlSetStage or GqlRemoveStage))
                    { throw new GraphPlanningException("CG05001", ErrorMessages.NextSegmentInvalid, stage.Location); }
                    if (stages[stageIndex - 1] is not GqlReturnStage) { ClearScope(); }
                    break;
            }
            foreach (var clause in query.MatchClauses)
            {
                token.ThrowIfCancellationRequested();
                if (clause?.Paths is null || clause.Paths.Count == 0)
                { throw new GraphPlanningException("CG05001", ErrorMessages.MatchClausePathsRequired, query.Location); }
                optional = clause.Optional;
                int firstDeclaration = declarations.Count;
                bool clauseMatchesNothing = false;
                var incomingPrefilterScope = clause.Paths.Any(path => path?.Selector is not null)
                    ? new HashSet<string>(variables.Keys, StringComparer.Ordinal) : null;
                // A MATCH declares every pattern part before checking any properties
                // (Neo4j SemanticPatternCheck.scala:101-103).
                foreach (var path in clause.Paths) { Declare(path, creating: false); }
                if (incomingPrefilterScope is null)
                {
                    for (int pathIndex = 0; pathIndex < clause.Paths.Count; pathIndex++)
                    {
                        if (!pathShapes[clause.Paths[pathIndex]].HasPrefilters) { continue; }
                        incomingPrefilterScope = new HashSet<string>(variables.Keys, StringComparer.Ordinal);
                        incomingPrefilterScope.ExceptWith(declarations.Skip(firstDeclaration));
                        break;
                    }
                }
                foreach (var path in clause.Paths)
                { if (!Validate(path, creating: false, declared: true, incomingPrefilterScope: incomingPrefilterScope)) { clauseMatchesNothing = true; } }
                ValidateExpression(clause.Predicate);
                bool selective = clause.Paths.Any(path => path.Selector is not null);
                // A post-selection WHERE cannot constrain the search, including through
                // an index anchor or a catalog-based matches-nothing shortcut.
                if (!selective) { clauseMatchesNothing |= RequiresUnknownName(clause.Predicate); }
                var anchors = new AnchorSources(_database, _snapshot, selective ? null : clause.Predicate, _parameters);
                var paths = new List<GraphPathPlan>();
                foreach (var path in clause.Paths)
                {
                    var shape = pathShapes[path];
                    GraphPathSelectionPlan? selection = null;
                    if (path.Selector is { } selector)
                    {
                        var startVariables = shape.Nodes[0].Where(node => node.Variable is not null).Select(node => node.Variable!).ToArray();
                        var endVariables = shape.Nodes[^1].Where(node => node.Variable is not null).Select(node => node.Variable!).ToArray();
                        selection = new GraphPathSelectionPlan(selector, shape, path.Mode ?? GqlPathMode.Trail, startVariables, endVariables,
                            startVariables.Concat(endVariables).Where(boundVariables.Contains).Distinct(StringComparer.Ordinal).ToArray());
                    }
                    paths.Add(new GraphPathPlan(path, ChooseAnchor(shape, anchors, boundVariables, token, sourceOnly: selection is not null)) { Shape = shape, Selection = selection });
                    foreach (var binding in shape.Bindings())
                    { if (!groupVariables.Contains(binding.Name)) { boundVariables.Add(binding.Name); } }
                }
                clauses.Add(new GraphMatchClausePlan(paths, clause.Predicate, optional,
                    declarations.Skip(firstDeclaration).ToArray(), clauseMatchesNothing));
                matchesNothing |= !optional && clauseMatchesNothing;
            }
            optional = false;
            // INSERT maps see only the incoming scope, independently of creation order.
            var insertScope = stage is GqlInsertStage ? new HashSet<string>(variables.Keys, StringComparer.Ordinal) : null;
            foreach (var path in query.Creates) { Validate(path, creating: true, insertScope); }
            foreach (var variable in query.DeleteVariables) { RequireVariable(variable, entity: true); }
            if (stage is GqlInsertStage)
            {
                foreach (var path in query.Creates)
                {
                    foreach (var node in path.Nodes)
                    {
                        foreach (var name in GraphLabelEvaluator.InsertLabels(node)) { tokens.IntroduceLabel(name); }
                        if (node.Variable is { } nodeVariable) { boundVariables.Add(nodeVariable); }
                    }
                    foreach (var edge in path.Relationships)
                    {
                        tokens.IntroduceRelationshipType(GraphLabelEvaluator.InsertType(edge));
                        if (edge.Variable is { } edgeVariable) { boundVariables.Add(edgeVariable); }
                    }
                }
            }
            if (stage is GqlReturnStage && !query.ReturnStar && query.Projections.Count == 0 || !Enum.IsDefined(query.SetQuantifier) || query.ReturnStar && (query.Projections.Count != 0 || declarations.Count == 0) ||
                query.SetQuantifier == GqlSetQuantifier.Distinct && !query.ReturnStar && query.Projections.Count == 0)
            { throw new GraphPlanningException("CG05001", ErrorMessages.ReturnShapeInvalid, query.Location); }
            var items = query.ReturnStar
                ? declarations.Select(variable => new GqlProjection(new GqlVariableExpression(variable)))
                : query.Projections;
            foreach (var projection in items)
            {
                token.ThrowIfCancellationRequested();
                if (projection?.Expression is null)
                { throw new GraphPlanningException("CG05001", ErrorMessages.ProjectionExpressionRequired, query.Location); }
                ValidateExpression(projection.Expression, allowAggregates: true);
                var kind = ProjectionKind(projection.Expression);
                projections.Add(new GraphProjectionPlan(projection.Expression,
                    projection.Alias ?? GraphProjectionText.Describe(projection.Expression), kind)
                {
                    BindingName = projection.Alias ?? (projection.Expression as GqlVariableExpression)?.Variable,
                    MayBeComposite = MayReturn(projection.Expression, entity: false),
                    Type = ExpressionType(projection.Expression),
                    ResultNullable = ResultNullable(projection.Expression),
                    Optional = projection.Expression is GqlNamedFunctionCallExpression call ? NativeExpression(call) is not null ? Shape(call).EntityKinds[0].Optional : ResolveFunction(call).ReturnType.IsNullable
                        : projection.Expression is GqlVariableExpression named && !mandatoryVariables.Contains(named.Variable),
                });
            }
            if (stage is GqlReturnStage && stageIndex > 0 && stages[stageIndex - 1] is GqlDeleteStage)
            { throw new GraphPlanningException("CG05001", ErrorMessages.DeleteProjectionUnsupported, query.Location); }
            if (pathsRequest && stageIndex == stages.Count - 1)
            {
                if (projections.Count != 1 || projections[0].Kind is not (GraphProjectionKind.Node or GraphProjectionKind.Relationship or GraphProjectionKind.Path) || projections[0].Expression is not GqlVariableExpression)
                { throw new GraphPlanningException("CG05001", ErrorMessages.PathProjectionRequired, query.Location); }
                if (projections[0].Optional)
                { throw new GraphPlanningException("CG05001", ErrorMessages.OptionalPathProjection, query.Location); }
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            var carriedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var projection in projections)
            {
                token.ThrowIfCancellationRequested();
                if (!names.Add(projection.Name) || stageIndex != stages.Count - 1 &&
                    projection.BindingName is { } bindingName && !carriedNames.Add(bindingName))
                {
                    throw new GraphPlanningException("CG01015", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        ErrorMessages.DuplicateResultColumn, projection.Name), projection.Expression.Location ?? query.Location);
                }
            }
            if (query.OrderBy is null || projections.Count == 0 && (query.OrderBy.Count != 0 || query.Offset is not null || query.Limit is not null))
            { throw new GraphPlanningException("CG05001", ErrorMessages.SortShapeInvalid, query.Location); }
            grouping = GraphGrouping.Plan(query, projections, token, key => ValidateExpression(key), _database.Engine.Functions);
            orderAliases = new Dictionary<string, int>(StringComparer.Ordinal);
            orderProjections = new Dictionary<GqlExpression, int>(new GraphExpressionShape(token));
            var shadowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var projection in projections)
            {
                if (projection.Expression is not GqlVariableExpression identity || identity.Variable != projection.Name)
                { shadowed.Add(projection.Name); }
            }
            for (int i = 0; i < projections.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                orderAliases.Add(projections[i].Name, i);
                // Neo4j NormalizeWithAndReturnClauses.scala:315-342 also guards expression
                // reuse against redefined variables; RETURN still evaluates in input scope.
                if (!GraphValueExpressions.ReferencesAny(projections[i].Expression, shadowed, token))
                { orderProjections.TryAdd(projections[i].Expression, i); }
                if (projections[i].Expression is GqlVariableExpression entity) { projectedEntities.Add(entity.Variable); }
            }
            var groupedExpressions = new HashSet<GqlExpression>(new GraphExpressionShape(token));
            if (grouping is not null)
            {
                foreach (var expression in grouping.Keys.Concat<GqlExpression>(grouping.Aggregates))
                {
                    if (!GraphValueExpressions.ReferencesAny(expression, shadowed, token)) { groupedExpressions.Add(expression); }
                }
            }
            var sortKeys = new List<GraphSortKeyPlan>();
            foreach (var item in query.OrderBy)
            {
                token.ThrowIfCancellationRequested();
                if (item?.Expression is null || !Enum.IsDefined(item.Direction) || !Enum.IsDefined(item.NullOrdering))
                { throw new GraphPlanningException("CG05001", ErrorMessages.SortShapeInvalid, query.Location); }
                ValidateExpression(item.Expression, allowAggregates: grouping is not null, ordering: true);
                if (grouping is not null) { grouping.Scope.Validate(item.Expression, orderAliases, groupedExpressions); }
                int projectionIndex = item.Expression is GqlVariableExpression alias && orderAliases.TryGetValue(alias.Variable, out int aliased)
                    ? aliased : orderProjections.TryGetValue(item.Expression, out int projectedIndex) ? projectedIndex : -1;
                sortKeys.Add(new GraphSortKeyPlan(item, projectionIndex));
            }
            long offset = PageValue(query.Offset) ?? 0;
            long? limit = PageValue(query.Limit);
            if (stage is GqlReturnStage { Predicate: { } predicate })
            {
                // Neo4j Clause.scala:1965,1998-2030: DISTINCT/grouping hide incoming
                // names from WITH WHERE. ORDER BY expression reuse does not apply here.
                var incomingVariables = variables;
                var incomingShapes = shapes;
                var projectedShapes = projections.Select(projection => Shape(projection.Expression)).ToArray();
                bool restricted = grouping is not null || query.SetQuantifier == GqlSetQuantifier.Distinct;
                variables = restricted ? new(StringComparer.Ordinal) : new(variables, StringComparer.Ordinal);
                shapes = restricted ? new(StringComparer.Ordinal) : new(shapes, StringComparer.Ordinal);
                for (int i = 0; i < projections.Count; i++)
                {
                    if (projections[i].BindingName is not { } name) { continue; }
                    variables[name] = ToBindingKind(projections[i].Kind);
                    shapes[name] = projectedShapes[i];
                }
                try { ValidateExpression(predicate); }
                finally { variables = incomingVariables; shapes = incomingShapes; }
            }
            var stagePlan = new GraphPlan(query, clauses.AsReadOnly())
            {
                MatchesNothing = matchesNothing,
                Projections = projections.AsReadOnly(),
                Grouping = grouping,
                Distinct = query.SetQuantifier == GqlSetQuantifier.Distinct ? new GraphDistinctPlan(projections.AsReadOnly()) : null,
                SortPage = sortKeys.Count != 0 || query.Offset is not null || limit is not null
                    ? new GraphSortPagePlan(sortKeys.AsReadOnly(), offset, limit, groupedExpressions) : null,
                Parameters = _parameters,
                FunctionBindings = _functions is null ? FrozenDictionary<GqlNamedFunctionCallExpression, GraphFunction>.Empty : _functions,
            };
            planned.Add(new GraphStagePlan(stage, stagePlan)
            {
                ElementKind = stage is GqlForStage forStage ? ProjectionKind(new GqlVariableExpression(forStage.Variable)) : GraphProjectionKind.Scalar,
            });
            if (stage is GqlDeleteStage deletion)
            {
                foreach (string variable in deletion.Variables)
                { deletedVariables.Add(variable); variables.Remove(variable); declarations.Remove(variable); shapes.Remove(variable); mandatoryVariables.Remove(variable); boundVariables.Remove(variable); }
            }
            if (stage is GqlReturnStage && stageIndex != stages.Count - 1)
            {
                var carried = projections.Select(projection =>
                    (Name: projection.BindingName, Projection: projection, Shape: Shape(projection.Expression))).ToArray();
                ClearScope();
                foreach (var item in carried)
                {
                    if (item.Name is null) { continue; }
                    if (!variables.TryAdd(item.Name, ToBindingKind(item.Projection.Kind)))
                    { throw new GraphPlanningException("CG01015", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        ErrorMessages.DuplicateResultColumn, item.Name), item.Projection.Expression.Location ?? query.Location); }
                    declarations.Add(item.Name);
                    boundVariables.Add(item.Name);
                    shapes.Add(item.Name, item.Shape);
                    if (!item.Projection.Optional) { mandatoryVariables.Add(item.Name); }
                }
            }
        }
        var final = planned[^1].Plan;
        return final with
        {
            Query = statement,
            MatchClauses = planned.SelectMany(stage => stage.Plan.MatchClauses).ToArray(),
            MatchesNothing = planned.Any(stage => stage.Plan.MatchesNothing),
            Stages = planned.AsReadOnly(), Writes = writes,
            Parameters = _parameters,
            Warnings = writes ? [] : tokens.Warnings,
        };

        void ClearScope()
        { variables.Clear(); declarations.Clear(); shapes.Clear(); mandatoryVariables.Clear(); boundVariables.Clear(); groupVariables.Clear(); }

        bool MayReturn(GqlExpression expression, bool entity, int accesses = 0) => GraphValueExpressions.MayReturn(expression, _parameters, entity,
            (name, depth, entities) => shapes.TryGetValue(name, out var shape)
                ? depth < shape.Entities.Length && (entities ? shape.Entities[depth] : shape.Composites[depth])
                : entities && depth == 0, accesses, call => ResolveFunction(call).ReturnType, NativeExpression);

        bool IsGroup(GqlExpression expression, int accesses = 0, bool ordering = false) => GraphValueExpressions.IsGroup(expression,
            // ORDER BY aliases shadow incoming names even inside result-preserving wrappers.
            // Resolve each alias expression in the incoming scope, not against other aliases.
            (name, depth) => ordering && orderAliases is not null && orderAliases.TryGetValue(name, out int index)
                ? IsGroup(projections[index].Expression, depth)
                : shapes.TryGetValue(name, out var shape) && depth < shape.Groups.Length && shape.Groups[depth], accesses, NativeExpression);

        GraphBindingShape Shape(GqlExpression expression, int accesses = 0)
        {
            var entities = new bool[GraphValueCodec.MaxDepth + 2];
            var composites = new bool[entities.Length];
            var scalars = new bool[entities.Length];
            var kinds = new GraphEntityShape[entities.Length];
            var groups = new bool[entities.Length];
            for (int i = 0; i < entities.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                groups[i] = IsGroup(expression, i + accesses);
                entities[i] = MayReturn(expression, true, i + accesses);
                composites[i] = MayReturn(expression, false, i + accesses);
                kinds[i] = GraphValueExpressions.EntityShape(expression, (name, depth) =>
                    shapes.TryGetValue(name, out var shape) && depth < shape.EntityKinds.Length ? shape.EntityKinds[depth]
                    : depth == 0 && variables.TryGetValue(name, out var kind) && kind is BindingKind.Node or BindingKind.Relationship
                        ? new GraphEntityShape(kind == BindingKind.Node ? GraphProjectionKind.Node : GraphProjectionKind.Relationship, !mandatoryVariables.Contains(name))
                        : default, i + accesses, token, call => ResolveFunction(call).ReturnType, NativeExpression);
                scalars[i] = GraphValueExpressions.IsKnownScalar(expression, _parameters,
                    (name, depth) => shapes.TryGetValue(name, out var shape) ? depth < shape.Scalars.Length && shape.Scalars[depth]
                        : variables.TryGetValue(name, out var kind) && kind is BindingKind.Node or BindingKind.Relationship && depth == 1,
                    i + accesses, call => ResolveFunction(call).ReturnType, NativeExpression);
                if (!entities[i] && !composites[i]) { break; }
            }
            return new GraphBindingShape(entities, composites, scalars, kinds, accesses == 0 ? ExpressionType(expression) : DatabaseType.Null)
            { Groups = groups, ValueKind = accesses == 0 ? KnownFunctionKind(expression, false) : GraphFunctionKind.Unknown, ResultNullable = accesses == 0 ? ResultNullable(expression) : null };
        }

        bool? ResultNullable(GqlExpression expression) => expression switch
        {
            GqlNamedFunctionCallExpression call => ResolveFunction(call).ReturnType.IsNullable,
            GqlVariableExpression variable when shapes.TryGetValue(variable.Variable, out var shape) => shape.ResultNullable,
            _ => null,
        };

        DatabaseType ExpressionType(GqlExpression expression) => expression switch
        {
            GqlFunctionCallExpression call => GraphScalarFunctions.Type(call.Function),
            GqlNamedFunctionCallExpression call => ResolveFunction(call).ReturnType.DatabaseType,
            GqlCastExpression cast => GraphScalarFunctions.Type(cast.Target),
            GqlAggregateExpression { Function: GqlAggregateFunction.Count } => DatabaseType.Int64,
            GqlVariableExpression variable when shapes.TryGetValue(variable.Variable, out var shape) => shape.Type,
            _ => DatabaseType.Null,
        };

        void AddValue(string variable, GqlExpression expression, bool element = false)
        {
            if (string.IsNullOrEmpty(variable) || variables.ContainsKey(variable))
            { throw new GraphPlanningException("CG03003", ErrorMessages.ValueBindingAlreadyBound, expression.Location); }
            var shape = Shape(expression, element ? 1 : 0);
            // UNWIND declares the list's proven inner type (Neo4j Clause.scala:1723-1724).
            var kind = ToBindingKind(element ? shape.EntityKinds[0].Kind : ProjectionKind(expression));
            deletedVariables.Remove(variable);
            variables.Add(variable, kind);
            shapes.Add(variable, shape);
            declarations.Add(variable);
            if (kind is BindingKind.Node or BindingKind.Relationship or BindingKind.Path) { boundVariables.Add(variable); }
            if (kind is BindingKind.Node or BindingKind.Relationship && shape.EntityKinds[0].Kind is GraphProjectionKind.Node or GraphProjectionKind.Relationship
                ? !shape.EntityKinds[0].Optional
                : expression is not GqlVariableExpression named || mandatoryVariables.Contains(named.Variable)) { mandatoryVariables.Add(variable); }
        }

        long? PageValue(GqlExpression? expression)
        {
            if (expression is null) { return null; }
            if (expression is not (GqlLiteralExpression or GqlParameterExpression))
            { throw new GraphPlanningException("CG05001", ErrorMessages.PageExpressionRequired, expression.Location); }
            ValidateExpression(expression);
            object? value = GraphExpressionEvaluator.ParameterOrLiteral(expression, _parameters);
            return value switch
            {
                byte number => number,
                sbyte number when number >= 0 => number,
                short number when number >= 0 => number,
                ushort number => number,
                int number when number >= 0 => number,
                uint number => number,
                long number when number >= 0 => number,
                ulong number when number <= long.MaxValue => (long)number,
                _ => throw new GraphPlanningException("CG02013", ErrorMessages.PageValueInvalid, expression.Location),
            };
        }

        // Composite constructors and conditional result branches propagate possible entities
        // before Execute can admit them, independently of which branch the data would select.
        GraphProjectionKind ProjectionKind(GqlExpression expression) => expression switch
        {
            GqlVariableExpression variable => RequireVariable(variable.Variable) switch
            {
                BindingKind.Node => GraphProjectionKind.Node,
                BindingKind.Relationship => GraphProjectionKind.Relationship,
                BindingKind.Path => GraphProjectionKind.Path,
                _ => MayReturn(expression, entity: true) ? GraphProjectionKind.EntityValue : GraphProjectionKind.Scalar,
            },
            GqlFunctionCallExpression call => GraphScalarFunctions.Entity(call.Function, 0) is GraphProjectionKind.Node
                ? GraphProjectionKind.Node : MayReturn(expression, entity: true) ? GraphProjectionKind.EntityValue : GraphProjectionKind.Scalar,
            GqlNamedFunctionCallExpression call when NativeExpression(call) is { } native => ProjectionKind(native),
            GqlNamedFunctionCallExpression call => ResolveFunction(call).ReturnType.Kind switch
            {
                GraphFunctionKind.Node => GraphProjectionKind.Node,
                GraphFunctionKind.Relationship => GraphProjectionKind.Relationship,
                GraphFunctionKind.Path => GraphProjectionKind.Path,
                _ => MayReturn(call, entity: true) ? GraphProjectionKind.EntityValue : GraphProjectionKind.Scalar,
            },
            GqlListExpression or GqlMapExpression or GqlCaseExpression or GqlCoalesceExpression or GqlNullIfExpression or
                GqlMemberExpression or GqlPropertyExpression or GqlSubscriptExpression or GqlAggregateExpression => MayReturn(expression, entity: true) ? GraphProjectionKind.EntityValue : GraphProjectionKind.Scalar,
            GqlLiteralExpression or GqlParameterExpression or GqlCastExpression or
                GqlBinaryExpression or GqlLogicalExpression or GqlUnaryExpression or
                GqlIsNullExpression or GqlTruthTestExpression or GqlInExpression or
                GqlStringPredicateExpression or GqlLabeledPredicate => GraphProjectionKind.Scalar,
            _ => throw new GraphPlanningException("CG05001", ErrorMessages.ProjectionUnsupported, expression.Location),
        };

        void Declare(GqlPathPattern path, bool creating)
        {
            token.ThrowIfCancellationRequested();
            var shape = GraphPathShape.Create(path, creating, parsed: statement.Text is not null);
            pathShapes[path] = shape;
            if (path.Variable is { } variable)
            {
                if (creating || !variables.TryAdd(variable, BindingKind.Path))
                { throw new GraphPlanningException("CG03003", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GraphPlannerPlanCoreGraphPlanning, variable), query.Location); }
                declarations.Add(variable);
                if (!optional) { mandatoryVariables.Add(variable); }
            }
            for (int i = 0; i < shape.Nodes.Count; i++)
            {
                foreach (var node in shape.Nodes[i]) { Bind(node.Variable, BindingKind.Node); }
                if (i == shape.Segments.Count) { continue; }
                var segment = shape.Segments[i];
                if (segment.Quantifier is null)
                {
                    foreach (var binding in segment.Body.Bindings()) { Bind(binding.Name, binding.Node ? BindingKind.Node : BindingKind.Relationship); }
                    continue;
                }
                var names = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var binding in segment.Body.Bindings())
                {
                    if (names.TryGetValue(binding.Name, out bool nodeKind))
                    {
                        if (nodeKind != binding.Node) { throw new GraphPlanningException("CG03003", ErrorMessages.GroupBindingConflict, query.Location); }
                        continue;
                    }
                    names.Add(binding.Name, binding.Node);
                    if (variables.ContainsKey(binding.Name)) { throw new GraphPlanningException("CG03003", ErrorMessages.GroupBindingConflict, query.Location); }
                    Bind(binding.Name, BindingKind.Value);
                    groupVariables.Add(binding.Name);
                    shapes[binding.Name] = new GraphBindingShape([segment.Quantifier.Max != 0, segment.Quantifier.Max != 0, false], [true, false, false], [false, false, true],
                        [default, new GraphEntityShape(binding.Node ? GraphProjectionKind.Node : GraphProjectionKind.Relationship, false), default], DatabaseType.Null) { Groups = [true, false, false] };
                }
            }
        }
        // Returns false when a MATCH pattern requires a name the database does not have.
        bool Validate(GqlPathPattern path, bool creating, IReadOnlySet<string>? propertyScope = null, bool declared = false,
            IReadOnlySet<string>? incomingPrefilterScope = null)
        {
            if (!declared) { Declare(path, creating); }
            bool satisfiable = true;
            HashSet<string>? prefilterScope = null;
            if (path.Selector is not null || pathShapes[path].HasPrefilters)
            {
                // Neo4j Clause.scala:1053-1098: selective dependencies belong to this
                // pattern (including quantified singletons) or the scope entering MATCH.
                prefilterScope = new HashSet<string>(incomingPrefilterScope!, StringComparer.Ordinal);
                foreach (var binding in pathShapes[path].Bindings()) { prefilterScope.Add(binding.Name); }
                if (path.Variable is { } variable) { prefilterScope.Add(variable); }
            }
            HashSet<string>? propertyGroups = null;
            var pending = new Stack<(GraphPathShape Shape, bool Required, bool Group, IReadOnlyList<(string Name, bool Node)> Locals)>();
            pending.Push((pathShapes[path], true, false, []));
            while (pending.TryPop(out var item))
            {
                foreach (var node in item.Shape.Nodes.SelectMany(nodes => nodes))
                {
                    bool valid = ValidateNodeLabels(node, creating, tokens);
                    if (item.Required) { satisfiable &= valid; }
                    ValidatePatternProperties(node.Properties, item.Group);
                    ValidatePrefilter(node.Predicate, item.Locals);
                }
                if (item.Shape.Edge is { } edge)
                {
                    bool valid = ValidateRelationship(edge, creating, tokens);
                    if (item.Required) { satisfiable &= valid; }
                    ValidatePatternProperties(edge.Properties, item.Group);
                    ValidatePrefilter(edge.Predicate, item.Locals);
                }
                foreach (var filter in item.Shape.Prefilters) { ValidatePrefilter(filter.Predicate, item.Locals); }
                foreach (var segment in item.Shape.Segments.AsEnumerable().Reverse())
                {
                    var locals = segment.Quantifier is null ? item.Locals : segment.Body.HasPrefilters ? segment.Body.Bindings().Distinct().ToArray() : [];
                    pending.Push((segment.Body, item.Required && segment.Quantifier?.Min != 0, item.Group || segment.Quantifier is not null, locals));
                }
            }
            return satisfiable;

            void ValidatePatternProperties(IReadOnlyDictionary<string, GqlExpression> properties, bool group)
            {
                if (group && properties is { Count: > 0 } && properties.Values.Any(expression => expression is not null && GraphValueExpressions.ReferencesAny(expression,
                    propertyGroups ??= shapes.Where(binding => binding.Value.Groups.Any(value => value)).Select(binding => binding.Key).ToHashSet(StringComparer.Ordinal), token)))
                { throw new GraphPlanningException("CG03003", ErrorMessages.GroupPropertyReference, query.Location); }
                ValidateStageProperties(properties!, creating, propertyScope, prefilterScope);
            }

            void ValidatePrefilter(GqlExpression? predicate, IReadOnlyList<(string Name, bool Node)> locals)
            {
                if (predicate is null) { return; }
                if (locals.Count == 0) { ValidateExpression(predicate, variableScope: prefilterScope); return; }
                // Inside a repeated body its variables denote the current occurrence's
                // singletons. Outside that body they retain their group-list types.
                var saved = locals.Select(local => (local.Name, Kind: variables[local.Name],
                    Shape: shapes.GetValueOrDefault(local.Name))).ToArray();
                try
                {
                    foreach (var local in locals)
                    { variables[local.Name] = local.Node ? BindingKind.Node : BindingKind.Relationship; shapes.Remove(local.Name); }
                    ValidateExpression(predicate, variableScope: prefilterScope);
                }
                finally
                {
                    foreach (var local in saved)
                    { variables[local.Name] = local.Kind; if (local.Shape is not null) { shapes[local.Name] = local.Shape; } }
                }
            }
        }
        void ValidateStageProperties(IReadOnlyDictionary<string, GqlExpression> properties, bool creating, IReadOnlySet<string>? propertyScope,
            IReadOnlySet<string>? variableScope = null)
        {
            if (properties is null) { throw new GraphPlanningException("CG05001", ErrorMessages.PropertyScalarRequired, query.Location); }
            var introduced = creating ? new HashSet<string>(variables.Keys.Where(name => !propertyScope!.Contains(name)), StringComparer.Ordinal) : null;
            foreach (var expression in properties.Values)
            {
                if (expression is null) { throw new GraphPlanningException("CG05001", ErrorMessages.PropertyScalarRequired, query.Location); }
                if (creating && GraphValueExpressions.ReferencesAny(expression, introduced!, token))
                { throw new GraphPlanningException("CG05001", ErrorMessages.InsertPropertyScope, expression.Location); }
                ValidateExpression(expression, variableScope: variableScope);
                if (expression is not (GqlLiteralExpression or GqlParameterExpression))
                {
                    if (MayReturn(expression, true) || MayReturn(expression, false) ||
                        (KnownFunctionKind(expression, false) & (GraphFunctionKind.Date | GraphFunctionKind.Timestamp)) != 0)
                    { throw new GraphPlanningException("CG03003", ErrorMessages.PropertyScalarRequired, expression.Location); }
                }
            }
            ValidateProperties(properties, creating);
        }
        void Bind(string? variable, BindingKind kind)
        {
            if (variable is null) { return; }
            if (groupVariables.Contains(variable))
            { throw new GraphPlanningException("CG03003", ErrorMessages.GroupBindingConflict, query.Location); }
            if (variables.TryGetValue(variable, out var previous) && previous != kind)
            { throw new GraphPlanningException("CG03003", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GraphPlannerPlanCoreGraphPlanning2, variable), query.Location); }
            if (!variables.ContainsKey(variable)) { declarations.Add(variable); }
            deletedVariables.Remove(variable);
            variables[variable] = kind;
            if (!optional) { mandatoryVariables.Add(variable); }
        }
        BindingKind RequireVariable(string variable, bool entity = false)
        {
            if (variable is null || !variables.TryGetValue(variable, out var kind)) { throw new GraphPlanningException(variable is not null && deletedVariables.Contains(variable) ? "CG03003" : "CG05001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GraphPlannerPlanCoreGraphPlanning3, variable), query.Location); }
            if (entity && kind is BindingKind.Path or BindingKind.Value)
            { throw new GraphPlanningException("CG03003", ErrorMessages.EntityBindingRequired, query.Location); }
            return kind;
        }
        // Walks the predicate with an explicit stack, in source order: an AND chain of any length
        // and parentheses of any depth validate without recursion, as Neo4j's n-ary Ands carry
        // any number of predicates. Only evaluation recurses, and it checks the stack (CG05008).
        void ValidateExpression(GqlExpression? root, bool allowAggregates = false, bool ordering = false,
            IReadOnlySet<string>? variableScope = null)
        {
            if (root is null) { return; }
            var pending = new Stack<(GqlExpression? Expression, GqlExpression? Function)>();
            pending.Push((root, null));
            while (pending.TryPop(out var item))
            {
                token.ThrowIfCancellationRequested();
                var (expression, function) = item;
                if (ordering && orderProjections is not null && expression is not null)
                {
                    if (orderProjections.ContainsKey(expression)) { continue; }
                    string? referenced = expression switch
                    {
                        GqlVariableExpression variable => variable.Variable,
                        GqlPropertyExpression property => property.Variable,
                        GqlLabeledPredicate labeled => labeled.Variable,
                        _ => null,
                    };
                    if (referenced is not null && orderAliases!.TryGetValue(referenced, out int aliasIndex))
                    {
                        var alias = projections[aliasIndex];
                        if (expression is GqlPropertyExpression && alias.Kind == GraphProjectionKind.Path)
                        { throw new GraphPlanningException("CG03003", ErrorMessages.SortPathProperty, expression.Location); }
                        if (expression is GqlPropertyExpression && alias.Kind == GraphProjectionKind.Scalar && Shape(alias.Expression).Scalars[0])
                        { throw new GraphPlanningException("CG03003", ErrorMessages.MemberBindingRequired, expression.Location); }
                        if (expression is GqlPropertyExpression && Shape(alias.Expression).Group)
                        { throw new GraphPlanningException("CG03003", ErrorMessages.MemberBindingRequired, expression.Location); }
                        if (expression is GqlLabeledPredicate labeled)
                        {
                            if (labeled.LabelExpression is null || alias.Kind is not (GraphProjectionKind.Node or GraphProjectionKind.Relationship or GraphProjectionKind.EntityValue))
                            { throw new GraphPlanningException("CG03003", ErrorMessages.SortEntityRequired, expression.Location); }
                            foreach (string name in GraphLabelEvaluator.Distinct(GraphLabelEvaluator.Names(labeled.LabelExpression)))
                            {
                                if (alias.Kind == GraphProjectionKind.Node) { tokens.HasLabel(name, labeled.Location); }
                                else if (alias.Kind == GraphProjectionKind.Relationship) { tokens.HasRelationshipType(name, labeled.Location); }
                            }
                        }
                        continue;
                    }
                    if (query.SetQuantifier == GqlSetQuantifier.Distinct && referenced is not null && !projectedEntities.Contains(referenced))
                    { throw new GraphPlanningException("CG05001", ErrorMessages.DistinctSortNotProjected, expression.Location); }
                }
                string? reference = expression switch
                {
                    GqlVariableExpression variable => variable.Variable,
                    GqlPropertyExpression property => property.Variable,
                    GqlMemberExpression { Operand: GqlVariableExpression owner } => owner.Variable,
                    GqlLabeledPredicate labeled => labeled.Variable,
                    _ => null,
                };
                if (reference is not null && variableScope is not null && !variableScope.Contains(reference) && variables.ContainsKey(reference))
                { throw new GraphPlanningException("CG01016", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.SelectivePrefilterPeerBinding, reference), expression!.Location); }
                if (function is not null && reference is not null && !variables.ContainsKey(reference))
                {
                    string name = function is GqlFunctionCallExpression enclosing ? GraphScalarFunctions.Name(enclosing.Function) : function is GqlNamedFunctionCallExpression named ? named.Name.ToString() : "CAST";
                    throw new GraphPlanningException("CG05001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.FunctionUnbound, name, reference), expression!.Location);
                }
                switch (expression)
                {
                    case GqlNamedFunctionCallExpression call:
                        GraphFunction registered = ResolveFunction(call);
                        if (!registered.AcceptsArity(call.Arguments.Count) ||
                            registered is GraphAggregateFunction && !allowAggregates ||
                            registered is GraphScalarFunction && call.Distinct ||
                            registered is GraphNativeAggregateFunction && call.Arguments.Count == 0 && call.Distinct)
                        { throw new GraphPlanningException("CG05001", ErrorMessages.AggregatePlacement, call.Location); }
                        if (!(registered.NullBehavior == GraphNullBehavior.ReturnsNullOnNullInput && call.Arguments.Any(argument => argument is GqlLiteralExpression { Value: null })))
                        {
                            for (int i = 0; i < call.Arguments.Count; i++)
                            {
                                var kind = KnownFunctionKind(call.Arguments[i], ordering);
                                if (kind != GraphFunctionKind.Unknown && (kind & registered.Parameters[i].Kind) == 0)
                                { throw new GraphPlanningException("CG03003", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.FunctionKind, call.Name, i + 1), call.Arguments[i].Location); }
                            }
                        }
                        foreach (var argument in call.Arguments.Reverse()) { pending.Push((argument, call)); }
                        break;
                    case GqlFunctionCallExpression call:
                        ValidateFunction(call, ordering);
                        function = call;
                        foreach (var argument in call.Arguments.Reverse()) { pending.Push((argument, function)); }
                        break;
                    case GqlCastExpression cast:
                        function = cast;
                        if (!Enum.IsDefined(cast.Target)) { throw new GraphPlanningException("CG05001", ErrorMessages.CastValueInvalid, cast.Location); }
                        if ((KnownFunctionKind(cast.Operand, ordering) & (GraphFunctionKind.Node | GraphFunctionKind.Relationship | GraphFunctionKind.Path | GraphFunctionKind.List | GraphFunctionKind.Map)) != 0 ||
                            KnownFunctionKind(cast.Operand, ordering) == GraphFunctionKind.Boolean && cast.Target is GqlCastType.Int32 or GqlCastType.Int64 or GqlCastType.Float32 or GqlCastType.Float64 or GqlCastType.Decimal)
                        { throw new GraphPlanningException("CG03003", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.FunctionKind, "CAST", 1), cast.Location); }
                        pending.Push((cast.Operand, function));
                        break;
                    case GqlAggregateExpression aggregate:
                        if (!allowAggregates || !Enum.IsDefined(aggregate.Function) ||
                            aggregate.Operand is null && (aggregate.Function != GqlAggregateFunction.Count || aggregate.Distinct))
                        { throw new GraphPlanningException("CG05001", ErrorMessages.AggregatePlacement, aggregate.Location); }
                        if (aggregate.Operand is not null) { pending.Push((aggregate.Operand, function)); }
                        break;
                    case GqlLiteralExpression literal:
                        if (forValue && literal.Value is byte[]) { break; }
                        try { GraphDatabase.TypeOf(literal.Value); }
                        catch (DatabaseException error)
                        { throw new GraphPlanningException("CG03003", ErrorMessages.LiteralScalarRequired, literal.Location, error); }
                        if (literal.Value is double number && !double.IsFinite(number) || literal.Value is float single && !float.IsFinite(single))
                        { throw new GraphPlanningException("CG03003", ErrorMessages.LiteralScalarRequired, literal.Location); }
                        break;
                    case GqlVariableExpression variable:
                        RequireVariable(variable.Variable);
                        break;
                    case GqlParameterExpression parameter:
                        object? bound = GraphExpressionEvaluator.Parameter(parameter, _parameters);
                        if (forValue && bound is byte[]) { break; }
                        if (snapshotted.Add(parameter.Name))
                        { ((Dictionary<string, object?>)_parameters!)[parameter.Name] = GraphCompositeValues.Snapshot(bound, parameter.Location); }
                        break;
                    case GqlPropertyExpression property:
                        ValidateMemberOwner(property.Variable, property);
                        break;
                    case GqlMemberExpression { Operand: GqlVariableExpression owner } member:
                        ValidateMemberOwner(owner.Variable, member);
                        break;
                    case GqlBinaryExpression { Operator: "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" or "+" or "-" or "*" or "/" or "%" or "||" } binary:
                        pending.Push((binary.Right, function));
                        pending.Push((binary.Left, function));
                        break;
                    case GqlBinaryExpression { Operator: "AND" }:
                        throw new GraphPlanningException("CG05001", ErrorMessages.LogicalNodeRequired, expression?.Location);
                    case GqlLogicalExpression logical:
                        if (!Enum.IsDefined(logical.Operator))
                        { throw new GraphPlanningException("CG05001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GraphPlannerPlanCoreGraphPlanning4, (int)logical.Operator), expression?.Location); }
                        if (logical.Operands is null || logical.Operands.Count < 2)
                        { throw new GraphPlanningException("CG05001", ErrorMessages.GraphPlannerPlanCoreGraphPlanning5, expression?.Location); }
                        for (int i = logical.Operands.Count - 1; i >= 0; i--) { pending.Push((logical.Operands[i], function)); }
                        break;
                    case GqlUnaryExpression { Operator: "NOT" or "+" or "-" } unary:
                        pending.Push((unary.Operand, function));
                        break;
                    case GqlIsNullExpression test:
                        pending.Push((test.Operand, function));
                        break;
                    case GqlTruthTestExpression test:
                        pending.Push((test.Operand, function));
                        break;
                    case GqlInExpression membership:
                        pending.Push((membership.List, function));
                        pending.Push((membership.Operand, function));
                        break;
                    case GqlMemberExpression member:
                        if (IsGroup(member.Operand, ordering: ordering))
                        { throw new GraphPlanningException("CG03003", ErrorMessages.MemberBindingRequired, member.Location); }
                        foreach (var child in GraphValueExpressions.Children(member).Reverse()) { pending.Push((child, function)); }
                        break;
                    case GqlListExpression or GqlMapExpression or GqlCaseExpression or GqlCoalesceExpression or
                        GqlNullIfExpression or GqlSubscriptExpression:
                        foreach (var child in GraphValueExpressions.Children(expression).Reverse()) { pending.Push((child, function)); }
                        break;
                    case GqlStringPredicateExpression predicate when Enum.IsDefined(predicate.Operator):
                        pending.Push((predicate.Right, function));
                        pending.Push((predicate.Left, function));
                        break;
                    case GqlLabeledPredicate labeled:
                        ValidateLabeledPredicate(labeled);
                        break;
                    case null:
                        throw new GraphPlanningException("CG05001", ErrorMessages.GraphPlannerPlanCoreGraphPlanning6, expression?.Location);
                    default:
                        throw new GraphPlanningException("CG05001", ErrorMessages.GraphPlannerPlanCoreGraphPlanning7, expression?.Location);
                }
            }
        }
        GqlExpression? NativeExpression(GqlNamedFunctionCallExpression call)
        {
            if (_nativeExpressions is not null && _nativeExpressions.TryGetValue(call, out var cached)) { return cached; }
            GqlExpression? expression = ResolveFunction(call) switch
            {
                GraphNativeScalarFunction scalar => new GqlFunctionCallExpression(scalar.Identity, call.Arguments, call.Location),
                GraphNativeAggregateFunction aggregate => new GqlAggregateExpression(aggregate.Identity,
                    call.Arguments.FirstOrDefault(), call.Distinct, call.Location),
                _ => null,
            };
            (_nativeExpressions ??= new(ReferenceEqualityComparer.Instance)).Add(call, expression); return expression;
        }
        GraphFunction ResolveFunction(GqlNamedFunctionCallExpression call)
        {
            if (_functions is not null && _functions.TryGetValue(call, out var cached)) { return cached; }
            var function = _database.Engine.Functions.Resolve(call);
            (_functions ??= new(ReferenceEqualityComparer.Instance)).Add(call, function); return function;
        }
        void ValidateFunction(GqlFunctionCallExpression call, bool ordering)
        {
            if (!GraphScalarFunctions.ValidArity(call))
            { throw new GraphPlanningException("CG05001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.FunctionArity, GraphScalarFunctions.Name(call.Function)), call.Location); }
            // NULL-first applies to statically known NULLs as well as runtime NULLs.
            if (call.Arguments.Any(argument => argument is GqlLiteralExpression { Value: null } || argument is GqlParameterExpression p && GraphExpressionEvaluator.Parameter(p, _parameters) is null)) { return; }
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                var kind = KnownFunctionKind(call.Arguments[i], ordering);
                if (kind != GraphFunctionKind.Unknown && (kind & GraphScalarFunctions.Argument(call.Function, i)) == 0)
                { throw new GraphPlanningException("CG03003", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.FunctionKind, GraphScalarFunctions.Name(call.Function), i + 1), call.Arguments[i].Location); }
            }
        }
        GraphFunctionKind KnownFunctionKind(GqlExpression expression, bool ordering)
        {
            switch (expression)
            {
                case GqlLiteralExpression literal: return GraphScalarFunctions.Kind(literal.Value);
                case GqlParameterExpression parameter: return GraphScalarFunctions.Kind(GraphExpressionEvaluator.Parameter(parameter, _parameters));
                case GqlFunctionCallExpression call: return GraphScalarFunctions.Result(call.Function);
                case GqlNamedFunctionCallExpression call: return NativeExpression(call) is { } native ? KnownFunctionKind(native, ordering) : ResolveFunction(call).ReturnType.Kind;
                case GqlCastExpression cast: return cast.Target switch
                {
                    GqlCastType.String => GraphFunctionKind.String, GqlCastType.Boolean => GraphFunctionKind.Boolean,
                    GqlCastType.Date => GraphFunctionKind.Date, GqlCastType.Timestamp => GraphFunctionKind.Timestamp,
                    GqlCastType.Int32 or GqlCastType.Int64 => GraphFunctionKind.Integral, _ => GraphFunctionKind.NonIntegral,
                };
                case GqlAggregateExpression aggregate:
                    return aggregate.Function == GqlAggregateFunction.CollectList ? GraphFunctionKind.List
                        : aggregate.Function is GqlAggregateFunction.Count or GqlAggregateFunction.Sum or GqlAggregateFunction.Avg ? GraphFunctionKind.Number : GraphFunctionKind.Unknown;
                case GqlLogicalExpression or GqlIsNullExpression or GqlTruthTestExpression or GqlInExpression or GqlStringPredicateExpression or GqlLabeledPredicate:
                    return GraphFunctionKind.Boolean;
                case GqlUnaryExpression unary: return unary.Operator == "NOT" ? GraphFunctionKind.Boolean : GraphFunctionKind.Number;
                case GqlBinaryExpression binary: return binary.Operator switch
                {
                    "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" => GraphFunctionKind.Boolean,
                    "-" or "*" or "/" or "%" => GraphFunctionKind.Number, "||" => GraphFunctionKind.String, _ => GraphFunctionKind.Unknown,
                };
                case GqlListExpression: return GraphFunctionKind.List;
                case GqlMapExpression: return GraphFunctionKind.Map;
                case GqlVariableExpression variable:
                    if (ordering && orderAliases?.TryGetValue(variable.Variable, out int index) == true)
                    { return KnownFunctionKind(projections[index].Expression, false); }
                    if (shapes.TryGetValue(variable.Variable, out var shape))
                    {
                        if (shape.Group) { return GraphFunctionKind.List; }
                        if (shape.ValueKind != GraphFunctionKind.Unknown) { return shape.ValueKind; }
                        if (shape.EntityKinds[0].Kind == GraphProjectionKind.Node) { return GraphFunctionKind.Node; }
                        if (shape.EntityKinds[0].Kind == GraphProjectionKind.Relationship) { return GraphFunctionKind.Relationship; }
                        return shape.Type switch
                        {
                            DatabaseType.String => GraphFunctionKind.String, DatabaseType.Boolean => GraphFunctionKind.Boolean,
                            DatabaseType.Date => GraphFunctionKind.Date, DatabaseType.DateTimeOffset => GraphFunctionKind.Timestamp,
                            DatabaseType.Int64 or DatabaseType.Int32 => GraphFunctionKind.Integral,
                            DatabaseType.Float32 or DatabaseType.Float64 or DatabaseType.Decimal => GraphFunctionKind.NonIntegral,
                            _ => GraphFunctionKind.Unknown,
                        };
                    }
                    return variables.TryGetValue(variable.Variable, out var binding) ? binding switch
                    { BindingKind.Node => GraphFunctionKind.Node, BindingKind.Relationship => GraphFunctionKind.Relationship, BindingKind.Path => GraphFunctionKind.Path, _ => GraphFunctionKind.Unknown } : GraphFunctionKind.Unknown;
                default:
                    if (IsGroup(expression, ordering: ordering)) { return GraphFunctionKind.List; }
                    var entity = GraphValueExpressions.EntityShape(expression, (name, depth) =>
                        shapes.TryGetValue(name, out var shape) && depth < shape.EntityKinds.Length ? shape.EntityKinds[depth]
                        : depth == 0 && variables.TryGetValue(name, out var kind) && kind is BindingKind.Node or BindingKind.Relationship
                            ? new GraphEntityShape(kind == BindingKind.Node ? GraphProjectionKind.Node : GraphProjectionKind.Relationship, !mandatoryVariables.Contains(name))
                            : default, 0, token, call => ResolveFunction(call).ReturnType, NativeExpression).Kind;
                    return entity == GraphProjectionKind.Node ? GraphFunctionKind.Node : entity == GraphProjectionKind.Relationship ? GraphFunctionKind.Relationship : GraphFunctionKind.Unknown;
            }
        }
        void ValidateMemberOwner(string variable, GqlExpression expression)
        {
            var kind = RequireVariable(variable);
            if (kind == BindingKind.Path || kind == BindingKind.Value &&
                shapes.TryGetValue(variable, out var shape) && (shape.Scalars[0] || shape.Group))
            { throw new GraphPlanningException("CG03003", ErrorMessages.MemberBindingRequired, expression.Location); }
        }
        // A labeled predicate tests a node's labels or a relationship's type; a path has neither.
        // Its names resolve against the catalog of the variable's kind, as a pattern's do. It is a
        // Boolean primary that IS NOT LABELED, !, | (and NOT or OR once WHERE has them) can invert,
        // so an unknown name never empties the plan; it evaluates as a label no node carries.
        void ValidateLabeledPredicate(GqlLabeledPredicate labeled)
        {
            if (labeled.Variable is null) { throw new GraphPlanningException("CG05001", ErrorMessages.GraphPlannerPlanCoreGraphPlanning8, labeled.Location); }
            var kind = RequireVariable(labeled.Variable);
            if (kind is BindingKind.Path or BindingKind.Value)
            { throw new GraphPlanningException("CG03003", ErrorMessages.EntityBindingRequired, labeled.Location); }
            if (labeled.LabelExpression is null) { throw new GraphPlanningException("CG05001", ErrorMessages.GraphPlannerPlanCoreGraphPlanning9, labeled.Location); }
            foreach (string name in GraphLabelEvaluator.Distinct(GraphLabelEvaluator.Names(labeled.LabelExpression)))
            {
                if (kind == BindingKind.Node) { tokens.HasLabel(name, labeled.Location); }
                else { tokens.HasRelationshipType(name, labeled.Location); }
            }
        }
        // Whether a top-level AND operand of a validated WHERE clause is a non-negated labeled
        // predicate whose pure conjunction names a label or type the database does not have. Such
        // an operand is false for every bound element (or unknown for an unbound one), so under
        // ISO three-valued AND the clause is never true and the statement keeps no row: the plan
        // can read nothing, as for the pattern form. Neo4j plans the WHERE form the same way, a
        // label scan built from the selections' HasLabels predicates
        // (cypher-planner/.../steps/leafplanner/labelScanLeafPlanner.scala:45), so an unresolved
        // label reads nothing. Only AND nodes are descended, with an explicit stack; a negation,
        // disjunction or wildcard can be true for an element without the name, so it never
        // empties the plan.
        bool RequiresUnknownName(GqlExpression? root)
        {
            if (root is null) { return false; }
            var pending = new Stack<GqlExpression>();
            pending.Push(root);
            while (pending.TryPop(out var expression))
            {
                token.ThrowIfCancellationRequested();
                switch (expression)
                {
                    case GqlLogicalExpression { Operator: GqlLogicalOperator.And } logical:
                        foreach (var operand in logical.Operands) { pending.Push(operand); }
                        break;
                    case GqlLabeledPredicate { IsNegated: false } labeled
                        when GraphLabelEvaluator.Conjunction(labeled.LabelExpression) is { } conjunction:
                        bool node = variables[labeled.Variable] == BindingKind.Node;
                        foreach (string name in conjunction)
                        {
                            // Each name was resolved by validation; the resolver answers from its cache.
                            if (node ? !tokens.HasLabel(name, labeled.Location) : !tokens.HasRelationshipType(name, labeled.Location))
                            { return true; }
                        }
                        break;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Validates a node pattern's label requirement. A label expression is the whole requirement,
    /// so <c>Labels</c> must be empty or list its conjunction. When matching, every name resolves
    /// against the catalog, including names under <c>!</c> and <c>|</c>; a name the database does
    /// not have is carried by no node, so it records a warning and evaluates as such (<c>:!A</c>
    /// then matches every node). Insertion takes a pure conjunction and may introduce new labels.
    /// </summary>
    /// <param name="node">The node pattern.</param>
    /// <param name="creating">Whether the pattern inserts rather than matches.</param>
    /// <param name="tokens">Resolves the names a match reads.</param>
    /// <returns>
    /// <see langword="false"/> when a matched node's conjunction names a label the database does
    /// not have, so no node satisfies the pattern.
    /// </returns>
    private static bool ValidateNodeLabels(GqlNodePattern node, bool creating, GraphTokenResolver tokens)
    {
        if (node.Labels is null) { throw new GraphPlanningException("CG05001", ErrorMessages.ValidateNodeLabelsGraphPlanning, null); }
        IReadOnlyList<string> names = node.Labels;
        // The labels every match carries: a pure conjunction's, or Labels for a pattern built without an expression.
        IReadOnlyList<string> required = node.Labels;
        if (node.LabelExpression is { } expression)
        {
            names = GraphLabelEvaluator.Names(expression);
            var conjunction = GraphLabelEvaluator.Conjunction(expression);
            if (node.Labels.Count != 0 && (conjunction is null ||
                !new HashSet<string>(node.Labels, StringComparer.Ordinal).SetEquals(conjunction)))
            {
                throw new GraphPlanningException("CG05001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidateNodeLabelsGraphPlanning2, GraphLabelEvaluator.Describe(expression)), null);
            }
            if (creating && conjunction is null)
            {
                throw new GraphPlanningException("CG05001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidateNodeLabelsGraphPlanning3, GraphLabelEvaluator.Describe(expression)), null);
            }
            required = conjunction ?? [];
        }
        // A chain may name a label many times; each is resolved once.
        foreach (string label in GraphLabelEvaluator.Distinct(names))
        {
            if (label is null) { throw new GraphPlanningException("CG05001", ErrorMessages.GraphLabelEvaluatorNamesGraphPlanning2, null); }
            if (!creating) { tokens.HasLabel(label); }
            else if (string.IsNullOrWhiteSpace(label))
            {
                throw new GraphPlanningException("CG05001", ErrorMessages.ValidateNodeLabelsGraphPlanning4, null);
            }
        }
        if (creating) { return true; }
        // Each name was resolved above; the resolver answers again from its cache.
        foreach (string label in required)
        {
            if (!tokens.HasLabel(label)) { return false; }
        }
        return true;
    }

    /// <summary>
    /// Validates a pattern's scalar literal/parameter property map. Storage cannot hold an empty or all-whitespace
    /// key, and an ISO delimited name such as <c>{" ": 1}</c> can spell one, so insertion rejects
    /// it here, before anything is written, instead of letting the store throw.
    /// </summary>
    private void ValidateProperties(IReadOnlyDictionary<string, GqlExpression>? properties, bool creating)
    {
        if (properties is null) { throw new GraphPlanningException("CG05001", ErrorMessages.ValidatePropertiesGraphPlanning, null); }
        foreach (var expression in properties.Values)
        {
            if (expression is GqlParameterExpression parameter)
            { GraphCompositeValues.Snapshot(GraphExpressionEvaluator.Parameter(parameter, _parameters), parameter.Location); }
            if (expression is GqlLiteralExpression or GqlParameterExpression) { GraphExpressionEvaluator.Scalar(expression, _parameters); }
        }
        if (!creating) { return; }
        foreach (string key in properties.Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new GraphPlanningException("CG05001", ErrorMessages.ValidatePropertiesGraphPlanning2, null);
            }
        }
    }

    /// <summary>
    /// Validates a relationship pattern's direction and type requirement. A label expression is
    /// the whole requirement, so <c>Type</c> must be null or its single name. When matching, every
    /// name resolves against the catalog; a type the database does not have records a warning and
    /// is the type of no relationship. Insertion needs one type and one direction, so
    /// <c>-[]-</c>, <c>&lt;-[]-&gt;</c> and their abbreviations cannot insert.
    /// </summary>
    /// <param name="relationship">The relationship pattern.</param>
    /// <param name="creating">Whether the pattern inserts rather than matches.</param>
    /// <param name="tokens">Resolves the names a match reads.</param>
    /// <returns>
    /// <see langword="false"/> when a matched relationship's required type (its <c>Type</c>, or a
    /// conjunction's names) is one the database does not have, so no relationship satisfies it.
    /// </returns>
    private static bool ValidateRelationship(GqlRelationshipPattern relationship, bool creating, GraphTokenResolver tokens)
    {
        if (!Enum.IsDefined(relationship.Direction))
        { throw new GraphPlanningException("CG05001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidateRelationshipGraphPlanning, (int)relationship.Direction), null); }
        string? type = relationship.Type;
        IReadOnlyList<string> names = type is null ? [] : [type];
        // The types a match must have: Type, or a conjunction's names (one name is a conjunction).
        IReadOnlyList<string> required = names;
        if (relationship.LabelExpression is { } expression)
        {
            names = GraphLabelEvaluator.Names(expression);
            string? single = (expression as GqlLabelName)?.Name;
            if (type is not null && !string.Equals(type, single, StringComparison.Ordinal))
            {
                throw new GraphPlanningException("CG05001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidateRelationshipGraphPlanning2, type, GraphLabelEvaluator.Describe(expression)), null);
            }
            type = single;
            required = GraphLabelEvaluator.Conjunction(expression) ?? [];
        }
        if (creating)
        {
            if (type is null || relationship.Direction is not (GqlPatternDirection.Outgoing or GqlPatternDirection.Incoming))
            { throw new GraphPlanningException("CG05001", ErrorMessages.ValidateRelationshipGraphPlanning3, null); }
            if (string.IsNullOrWhiteSpace(type))
            { throw new GraphPlanningException("CG05001", ErrorMessages.ValidateRelationshipGraphPlanning4, null); }
            return true;
        }
        foreach (string name in GraphLabelEvaluator.Distinct(names)) { tokens.HasRelationshipType(name); }
        foreach (string name in required)
        {
            if (!tokens.HasRelationshipType(name)) { return false; }
        }
        return true;
    }

    private enum BindingKind { Node, Relationship, Path, Value }

    private static BindingKind ToBindingKind(GraphProjectionKind kind) => kind switch
    { GraphProjectionKind.Node => BindingKind.Node, GraphProjectionKind.Relationship => BindingKind.Relationship, GraphProjectionKind.Path => BindingKind.Path, _ => BindingKind.Value };

    private static GqlQueryExpression StageQuery(GqlStage stage, Assimalign.Cohesion.Database.Language.Location? location) => stage switch
    {
        GqlMatchStage match => new([match.Clause], [], [], false, [], location ?? stage.Location),
        GqlInsertStage insert => new([], null, insert.Paths, [], false, [], location ?? stage.Location),
        GqlDeleteStage delete => new([], null, [], delete.Variables, delete.Detach, [], location ?? stage.Location),
        GqlReturnStage project => new([], null, [], [], false, project.Projections, location ?? stage.Location)
        { SetQuantifier = project.SetQuantifier, ReturnStar = project.ReturnStar, GroupBy = project.GroupBy,
            OrderBy = project.OrderBy, Offset = project.Offset, Limit = project.Limit },
        GqlLetStage or GqlForStage or GqlFilterStage or GqlNextStage or GqlSetStage or GqlRemoveStage => new([], null, [], [], false, [], location ?? stage.Location),
        _ => throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, stage?.Location ?? location),
    };

    // Earlier paths and clauses supply argument bindings before any scan or seek, as Neo4j's
    // argumentLeafPlanner.scala:33-37 does (community/cypher/cypher-planner/.../steps/leafplanner).
    // A bound relationship supplies its adjacent endpoint when no node is already bound.
    // A pattern's pure label conjunction requires every match to carry each label.
    // Positive label conjunctions in the clause's top-level WHERE
    // AND chain supply the same required labels. Other label expressions remain residuals.
    //
    // Within each distinct required label, choose its indexed key whose first non-null value comes
    // first, inline properties before WHERE equalities. Selection costs the node's labels and values
    // plus the indexes its labels own, never their product: Neo4j's leaf planner likewise groups the
    // predicates once and visits only each label's own index descriptors
    // (cypher-planner .../leafplanner/index/NodeIndexLeafPlanner.scala:184, :201, :248-261, :279-296).
    // Compare bounded equality-range estimates with retained label and type-publication estimates
    // at every eligible position. Equality seeks win ties, then source-ordered node/type candidates.
    // Neo4j's labelScanLeafPlanner reads HasLabels selections for pattern nodes
    // (labelScanLeafPlanner.scala:45-46, :73-78).
    private GraphAnchor ChooseAnchor(GraphPathShape shape, AnchorSources sources, IReadOnlySet<string> boundVariables, CancellationToken token, bool sourceOnly = false)
    {
        for (int i = 0; i < (sourceOnly ? 1 : shape.Nodes.Count); i++)
        {
            foreach (var node in shape.Nodes[i])
            {
                if (node.Variable is { } variable && boundVariables.Contains(variable))
                { return new GraphAnchor(i, node.Labels.FirstOrDefault(), null, null); }
            }
        }
        for (int i = 0; !sourceOnly && i < shape.Segments.Count; i++)
        {
            if (shape.Segments[i] is { Quantifier: null, Body.Edge.Variable: { } variable } && boundVariables.Contains(variable))
            { return new GraphAnchor(i, shape.Nodes[i].SelectMany(node => node.Labels).FirstOrDefault(), null, null); }
        }
        GraphAnchor? scan = null;
        long scanCost = long.MaxValue;
        for (int i = 0; i < (sourceOnly ? 1 : shape.Nodes.Count); i++)
        {
            foreach (var node in shape.Nodes[i])
            {
                var labels = sources.GetRequiredLabels(node);
                foreach (string candidate in labels)
                {
                    long cost = _database.Store.EstimateLabelCount(candidate);
                    if (scan is null || cost < scanCost)
                    { scan = new GraphAnchor(i, candidate, null, null); scanCost = cost; }
                }
                if (labels.Count == 0)
                {
                    if (UnionLabels(node.LabelExpression) is { Count: > 0 } union)
                    {
                        long cost = union.Sum(label => _database.Store.EstimateLabelCount(label));
                        if (scan is null || cost < scanCost)
                        { scan = new GraphAnchor(i, union[0], null, null, union); scanCost = cost; }
                    }
                    continue;
                }
                var inline = FirstValues(node.Properties);
                var equalities = sources.GetEqualities(node.Variable);
                if (inline.Count == 0 && equalities.Count == 0) { continue; }
                var indexes = sources.GetIndexedKeys();
                if (indexes.Count == 0) { continue; }
                var tried = new HashSet<string>(StringComparer.Ordinal);
                foreach (string label in labels)
                {
                    if (!tried.Add(label) || !indexes.TryGetValue(label, out var keys)) { continue; }
                    string? bestKey = null;
                    ulong bestIndex = 0;
                    FirstValue best = new(int.MaxValue, null!);
                    foreach (var index in keys)
                    {
                        string key = index.Key;
                        // Inline positions precede every equality position, as the properties precede
                        // the WHERE clause.
                        FirstValue candidate;
                        if (inline.TryGetValue(key, out var property)) { candidate = property; }
                        else if (equalities.TryGetValue(key, out var equality)) { candidate = new(inline.Count + equality.Position, equality.Value); }
                        else { continue; }
                        if (candidate.Position < best.Position) { best = candidate; bestKey = key; bestIndex = index.Id; }
                    }
                    if (bestKey is not null)
                    {
                        long cost = _database.Store.EstimateIndexCount(bestIndex, label, best.Value, _snapshot, token);
                        // Prefer an equality seek on a tie, preserving the established preference
                        // for a seek over its own label scan. Other ties retain source order.
                        if (scan is null || cost < scanCost || cost == scanCost && scan.Property is null)
                        { scan = new GraphAnchor(i, label, bestKey, best.Value); scanCost = cost; }
                    }
                }
            }
        }
        // Only outer singleton edges can seed a path. Repeated bodies may match zero times;
        // anchoring on one of their postings would lose those matches.
        for (int i = 0; !sourceOnly && i < shape.Segments.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (shape.Segments[i] is not { Quantifier: null, Body.Edge: { } edge }) { continue; }
            IReadOnlyList<string>? types = edge.LabelExpression is GqlLabelName name ? [name.Name]
                : UnionLabels(edge.LabelExpression) ?? (edge.Type is { } typeName ? [typeName] : null);
            if (types is not { Count: > 0 }) { continue; }
            long cost = 0;
            foreach (string type in types)
            {
                long count = _database.Store.EstimateRelationshipTypeCount(type);
                cost = count > long.MaxValue - cost ? long.MaxValue : cost + count;
            }
            if (edge.Direction is GqlPatternDirection.Undirected or GqlPatternDirection.LeftOrRight)
            { cost = cost > long.MaxValue / 2 ? long.MaxValue : cost * 2; }
            if (scan is null || cost < scanCost)
            { scan = new GraphAnchor(i, null, null, null) { RelationshipTypes = types }; scanCost = cost; }
        }
        return scan is null ? new GraphAnchor(0, null, null, null) : scan with { EstimatedRows = scanCost };
    }

    private static IReadOnlyList<string>? UnionLabels(GqlLabelExpression? expression)
    {
        if (expression is not GqlLabelDisjunction) { return null; }
        var result = new List<string>();
        var pending = new Stack<GqlLabelExpression>();
        pending.Push(expression);
        while (pending.TryPop(out var current))
        {
            if (current is GqlLabelName name) { result.Add(name.Name); }
            else if (current is GqlLabelDisjunction disjunction)
            {
                for (int i = disjunction.Operands.Count - 1; i >= 0; i--) { pending.Push(disjunction.Operands[i]); }
            }
            else { return null; }
        }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Each key's first non-null inline value and its position among the node's non-null values.</summary>
    private Dictionary<string, FirstValue> FirstValues(IReadOnlyDictionary<string, GqlExpression> properties)
    {
        var values = new Dictionary<string, FirstValue>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (property.Value is not (GqlLiteralExpression or GqlParameterExpression)) { continue; }
            var value = GraphExpressionEvaluator.Scalar(property.Value, _parameters);
            if (value is not null) { values.TryAdd(property.Key, new FirstValue(values.Count, value)); }
        }
        return values;
    }

    /// <summary>A candidate anchor value and its source position.</summary>
    private readonly record struct FirstValue(int Position, object Value);

    /// <summary>
    /// What anchor selection reads from outside the pattern, each read at most once per clause: the
    /// visible indexes grouped by label, and every variable's <c>variable.key = literal-or-parameter</c>
    /// equalities and required label conjunctions from the <c>WHERE</c> chain.
    /// </summary>
    [global::System.Diagnostics.DebuggerDisplay("AnchorSources")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlanner.AnchorSources.DebugView))]
    private sealed class AnchorSources
    {
        private static readonly Dictionary<string, FirstValue> _none = new(StringComparer.Ordinal);
        private readonly GraphDatabase _database;
        private readonly TransactionSnapshot _snapshot;
        private readonly IReadOnlyDictionary<string, object?>? _parameters;
        private readonly GqlExpression? _predicate;
        private Dictionary<string, List<(string Key, ulong Id)>>? _indexedKeys;
        private Dictionary<string, Dictionary<string, FirstValue>>? _equalities;
        private readonly Dictionary<string, List<string>> _labels = new(StringComparer.Ordinal);

        /// <summary>Initializes a new instance of the <see cref="AnchorSources"/> class.</summary>
        /// <param name="database">The database whose store lists the visible indexes.</param>
        /// <param name="snapshot">The snapshot the index list observes.</param>
        /// <param name="predicate">The clause's <c>WHERE</c> predicate, if any.</param>
        /// <param name="parameters">The normalized bindings used for scalar equality values.</param>
        public AnchorSources(GraphDatabase database, TransactionSnapshot snapshot, GqlExpression? predicate, IReadOnlyDictionary<string, object?>? parameters)
        {
            _database = database;
            _snapshot = snapshot;
            _predicate = predicate;
            _parameters = parameters;
        }

        /// <summary>The indexed property keys of each label with at least one visible index.</summary>
        internal Dictionary<string, List<(string Key, ulong Id)>> GetIndexedKeys()
        {
            if (_indexedKeys is not null) { return _indexedKeys; }
            var indexed = new Dictionary<string, List<(string Key, ulong Id)>>(StringComparer.Ordinal);
            foreach (var index in _database.Store.GetIndexIdentities(_snapshot))
            {
                if (!indexed.TryGetValue(index.Label, out var keys)) { indexed.Add(index.Label, keys = []); }
                keys.Add((index.Property, index.Id));
            }
            return _indexedKeys = indexed;
        }

        /// <summary>
        /// Each key's first non-null equality value for <paramref name="variable"/>, with its
        /// position among that variable's non-null equalities, in source order.
        /// </summary>
        internal Dictionary<string, FirstValue> GetEqualities(string? variable)
        {
            if (variable is null) { return _none; }
            _equalities ??= Collect(_predicate);
            return _equalities.TryGetValue(variable, out var values) ? values : _none;
        }

        /// <summary>The pattern's required labels followed by this clause's required WHERE labels.</summary>
        internal IReadOnlyList<string> GetRequiredLabels(GqlNodePattern node)
        {
            var pattern = node.LabelExpression is { } expression ? GraphLabelEvaluator.Conjunction(expression) ?? [] : node.Labels;
            if (node.Variable is not { } variable) { return pattern; }
            _equalities ??= Collect(_predicate);
            if (!_labels.TryGetValue(variable, out var labels)) { return pattern; }
            return pattern.Count == 0 ? labels : pattern.Concat(labels).ToArray();
        }

        /// <summary>
        /// Walks the predicate's <c>AND</c> chains once, through nested chains of any depth, with an
        /// explicit stack. Only positive pure label conjunctions require every match to carry
        /// their labels. Negation, disjunction and truth tests remain residual predicates.
        /// </summary>
        private Dictionary<string, Dictionary<string, FirstValue>> Collect(GqlExpression? predicate)
        {
            var collected = new Dictionary<string, Dictionary<string, FirstValue>>(StringComparer.Ordinal);
            if (predicate is null) { return collected; }
            var pending = new Stack<GqlExpression?>();
            pending.Push(predicate);
            while (pending.TryPop(out var expression))
            {
                if (expression is GqlLogicalExpression { Operator: GqlLogicalOperator.And, Operands: { } operands })
                {
                    for (int i = operands.Count - 1; i >= 0; i--) { pending.Push(operands[i]); }
                    continue;
                }
                if (expression is GqlLabeledPredicate { IsNegated: false } labeled &&
                    GraphLabelEvaluator.Conjunction(labeled.LabelExpression) is { } conjunction)
                {
                    if (!_labels.TryGetValue(labeled.Variable, out var labels)) { _labels.Add(labeled.Variable, labels = []); }
                    labels.AddRange(conjunction);
                    continue;
                }
                if (expression is not GqlBinaryExpression { Operator: "=" } binary) { continue; }
                if (binary.Left is GqlPropertyExpression left && binary.Right is GqlLiteralExpression or GqlParameterExpression)
                { Add(left, GraphExpressionEvaluator.ParameterOrLiteral(binary.Right, _parameters)); }
                else if (binary.Right is GqlPropertyExpression property && binary.Left is GqlLiteralExpression or GqlParameterExpression)
                { Add(property, GraphExpressionEvaluator.ParameterOrLiteral(binary.Left, _parameters)); }
            }
            return collected;

            void Add(GqlPropertyExpression property, object? value)
            {
                if (property.Variable is null || property.Property is null || value is null || GraphCompositeValues.IsComposite(value)) { return; }
                if (!collected.TryGetValue(property.Variable, out var values))
                {
                    collected.Add(property.Variable, values = new Dictionary<string, FirstValue>(StringComparer.Ordinal));
                }
                values.TryAdd(property.Property, new FirstValue(values.Count, value));
            }
        }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlanner.AnchorSources value)
            {
                _ = value;
            }
        }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlanner value)
        {
            _ = value;
        }
    }
}
