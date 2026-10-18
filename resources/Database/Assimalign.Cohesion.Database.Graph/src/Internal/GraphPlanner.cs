using System;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Execution.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed record GraphAnchor(int NodeIndex, string? Label, string? Property, object? Value);
internal sealed record GraphPathPlan(GqlPathPattern Pattern, GraphAnchor Anchor);
internal sealed record GraphMatchClausePlan(IReadOnlyList<GraphPathPlan> Paths, GqlExpression? Predicate,
    bool Optional, IReadOnlyList<string> NewVariables, bool MatchesNothing);
internal enum GraphProjectionKind { Scalar, Node, Relationship, Path, EntityValue }
internal sealed record GraphProjectionPlan(GqlExpression Expression, string Name, GraphProjectionKind Kind);
internal sealed record GraphDistinctPlan(IReadOnlyList<GraphProjectionPlan> Keys);

/// <summary>A validated statement with each MATCH pattern's anchor.</summary>
/// <param name="Query">The validated statement.</param>
/// <param name="MatchClauses">The clause-ordered joins, filters and null-extension bindings.</param>
internal sealed record GraphPlan(GqlQueryExpression Query, IReadOnlyList<GraphMatchClausePlan> MatchClauses)
{
    internal IReadOnlyList<GraphProjectionPlan> Projections { get; init; } = [];
    internal GraphDistinctPlan? Distinct { get; init; }
    /// <summary>
    /// Gets whether the statement requires a label or relationship type the database does not
    /// have: a MATCH node pattern's conjunction (<c>:A</c>, <c>:A&amp;B</c>), a MATCH relationship
    /// pattern's type, or a non-negated labeled predicate with a pure conjunction among the
    /// <c>WHERE</c> clause's top-level <c>AND</c> operands (<c>WHERE n:A</c>,
    /// <c>WHERE n.k = 1 AND n IS LABELED A</c>). No element can satisfy it, so the statement
    /// matches no row and the executor reads nothing.
    /// </summary>
    /// <remarks>
    /// Only a mandatory clause may set this flag. An unsatisfiable optional clause records its
    /// own flag and null-extends its input without reading candidates.
    /// </remarks>
    internal bool MatchesNothing { get; init; }

    /// <summary>The normalized, execution-local snapshot of named scalar bindings.</summary>
    internal IReadOnlyDictionary<string, object?>? Parameters { get; init; }

    /// <summary>
    /// Gets the <c>COHDBG010</c>/<c>COHDBG011</c> warnings of a read-only statement that names a
    /// label or relationship type the database does not have, in first-mention order; empty for a
    /// statement that writes (#1228).
    /// </summary>
    internal IReadOnlyList<Diagnostic> Warnings { get; init; } = [];
}

internal sealed partial class GraphPlanner
{
    private readonly GraphDatabase _database;
    private readonly TransactionSnapshot _snapshot;
    private IReadOnlyDictionary<string, object?>? _parameters;

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
    /// statement reports it as a <c>COHDBG010</c>/<c>COHDBG011</c> warning. Only a read-only
    /// statement warns, as Neo4j's <c>CheckForUnresolvedTokens</c> runs only when
    /// <c>query.readOnly</c> (<c>cypher-planner/.../compiler/planner/CheckForUnresolvedTokens.scala:53</c>).
    /// </summary>
    /// <param name="query">The statement to plan.</param>
    /// <param name="parameters">The named bindings; normalization and validation precede execution.</param>
    /// <param name="paths">Whether path-request projection shape takes precedence over duplicate column names.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="DatabaseException">The statement is invalid (<c>COHDBG001</c>, <c>COHDBG003</c>, <c>COHDBG015</c>).</exception>
    internal GraphPlan Plan(GqlQueryExpression query, IReadOnlyDictionary<string, object?>? parameters = null, bool paths = false)
    {
        try
        {
            _parameters = ParameterBindings.Normalize(parameters, name => new GraphPlanningException(
                "COHDBG014", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ParameterCollision, name), query.Location));
            return PlanCore(query, paths);
        }
        catch (GraphPlanningException exception) when (exception.Diagnostics[0].Start is null && query.Location is not null)
        {
            throw exception.At(query.Location);
        }
    }

    private GraphPlan PlanCore(GqlQueryExpression query, bool pathsRequest)
    {
        if (_parameters is not null) { _parameters = new Dictionary<string, object?>(_parameters, StringComparer.Ordinal); }
        var snapshotted = new HashSet<string>(StringComparer.Ordinal);
        var variables = new Dictionary<string, BindingKind>(StringComparer.Ordinal);
        var declarations = new List<string>();
        var mandatoryVariables = new HashSet<string>(StringComparer.Ordinal);
        var boundVariables = new HashSet<string>(StringComparer.Ordinal);
        var clauses = new List<GraphMatchClausePlan>();
        var tokens = new GraphTokenResolver(_database.Catalog, _snapshot);
        bool matchesNothing = false;
        bool optional = false;
        if (query.MatchClauses is null)
        { throw new GraphPlanningException("COHDBG001", ErrorMessages.MatchClausePathsRequired, query.Location); }
        foreach (var clause in query.MatchClauses)
        {
            if (clause?.Paths is null || clause.Paths.Count == 0)
            { throw new GraphPlanningException("COHDBG001", ErrorMessages.MatchClausePathsRequired, query.Location); }
            optional = clause.Optional;
            int firstDeclaration = declarations.Count;
            bool clauseMatchesNothing = false;
            foreach (var path in clause.Paths)
            { if (!Validate(path, creating: false)) { clauseMatchesNothing = true; } }
            ValidateExpression(clause.Predicate);
            clauseMatchesNothing |= RequiresUnknownName(clause.Predicate);
            var anchors = new AnchorSources(_database, _snapshot, clause.Predicate, _parameters);
            var paths = new List<GraphPathPlan>();
            foreach (var path in clause.Paths)
            {
                paths.Add(new GraphPathPlan(path, ChooseAnchor(path, anchors, boundVariables)));
                foreach (var node in path.Nodes)
                { if (node.Variable is { } variable) { boundVariables.Add(variable); } }
                foreach (var relationship in path.Relationships)
                { if (relationship.Variable is { } variable) { boundVariables.Add(variable); } }
            }
            clauses.Add(new GraphMatchClausePlan(paths, clause.Predicate, optional,
                declarations.Skip(firstDeclaration).ToArray(), clauseMatchesNothing));
            matchesNothing |= !optional && clauseMatchesNothing;
        }
        optional = false;
        foreach (var path in query.Creates) { Validate(path, creating: true); }
        foreach (var variable in query.DeleteVariables) { RequireVariable(variable, entity: true); }
        if (!Enum.IsDefined(query.SetQuantifier) || query.ReturnStar && (query.Projections.Count != 0 || declarations.Count == 0) ||
            query.SetQuantifier == GqlSetQuantifier.Distinct && !query.ReturnStar && query.Projections.Count == 0)
        { throw new GraphPlanningException("COHDBG001", ErrorMessages.ReturnShapeInvalid, query.Location); }
        var projections = new List<GraphProjectionPlan>();
        var items = query.ReturnStar
            ? declarations.Select(variable => new GqlProjection(new GqlVariableExpression(variable)))
            : query.Projections;
        foreach (var projection in items)
        {
            if (projection?.Expression is null)
            { throw new GraphPlanningException("COHDBG001", ErrorMessages.ProjectionExpressionRequired, query.Location); }
            ValidateExpression(projection.Expression);
            var kind = ProjectionKind(projection.Expression);
            projections.Add(new GraphProjectionPlan(projection.Expression,
                projection.Alias ?? GraphProjectionText.Describe(projection.Expression), kind));
        }
        if (query.MatchClauses.Count == 0 && query.Creates.Count == 0 && projections.Count == 0)
        { throw new GraphPlanningException("COHDBG001", ErrorMessages.PatternOrReturnRequired, query.Location); }
        if (query.Creates.Count != 0 && query.DeleteVariables.Count != 0)
        { throw new GraphPlanningException("COHDBG001", "Insertion and deletion cannot share a statement.", query.Location); }
        if (query.DeleteVariables.Count != 0 && projections.Count != 0)
        { throw new GraphPlanningException("COHDBG001", "Deletion cannot be followed by projection in this subset.", query.Location); }
        if (pathsRequest && (projections.Count != 1 || projections[0].Kind == GraphProjectionKind.Scalar ||
            projections[0].Expression is not GqlVariableExpression))
        { throw new GraphPlanningException("COHDBG001", ErrorMessages.PathProjectionRequired, query.Location); }
        if (pathsRequest && projections[0].Expression is GqlVariableExpression projected && !mandatoryVariables.Contains(projected.Variable))
        { throw new GraphPlanningException("COHDBG001", ErrorMessages.OptionalPathProjection, projected.Location ?? query.Location); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projection in projections)
        {
            if (!names.Add(projection.Name))
            {
                throw new GraphPlanningException("COHDBG015", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.DuplicateResultColumn, projection.Name), projection.Expression.Location ?? query.Location);
            }
        }
        bool readOnly = query.Creates.Count == 0 && query.DeleteVariables.Count == 0;
        return new GraphPlan(query, clauses.AsReadOnly())
        {
            MatchesNothing = matchesNothing,
            Projections = projections.AsReadOnly(),
            Distinct = query.SetQuantifier == GqlSetQuantifier.Distinct ? new GraphDistinctPlan(projections.AsReadOnly()) : null,
            Parameters = _parameters,
            Warnings = readOnly ? tokens.Warnings : [],
        };

        // Composite constructors and conditional result branches propagate possible entities
        // before Execute can admit them, independently of which branch the data would select.
        GraphProjectionKind ProjectionKind(GqlExpression expression) => expression switch
        {
            GqlVariableExpression variable => RequireVariable(variable.Variable) switch
            {
                BindingKind.Node => GraphProjectionKind.Node,
                BindingKind.Relationship => GraphProjectionKind.Relationship,
                _ => GraphProjectionKind.Path,
            },
            GqlListExpression or GqlMapExpression or GqlCaseExpression or GqlCoalesceExpression or GqlNullIfExpression or
                GqlMemberExpression or GqlSubscriptExpression => GraphValueExpressions.MayReturn(expression, _parameters, entity: true) ? GraphProjectionKind.EntityValue : GraphProjectionKind.Scalar,
            GqlLiteralExpression or GqlParameterExpression or GqlPropertyExpression or
                GqlBinaryExpression or GqlLogicalExpression or GqlUnaryExpression or
                GqlIsNullExpression or GqlTruthTestExpression or GqlInExpression or
                GqlStringPredicateExpression or GqlLabeledPredicate => GraphProjectionKind.Scalar,
            _ => throw new GraphPlanningException("COHDBG001", ErrorMessages.ProjectionUnsupported, expression.Location),
        };

        // Returns false when a MATCH pattern requires a name the database does not have.
        bool Validate(GqlPathPattern path, bool creating)
        {
            if (path?.Nodes is null || path.Relationships is null || path.Nodes.Count == 0 ||
                path.Relationships.Count != path.Nodes.Count - 1 || path.Relationships.Count > 64 ||
                path.Nodes.Any(node => node is null) || path.Relationships.Any(relationship => relationship is null))
            { throw new GraphPlanningException("COHDBG001", "A finite path requires one more node than relationships and at most 64 hops.", query.Location); }
            bool satisfiable = true;
            if (path.Variable is { } variable)
            {
                if (creating || !variables.TryAdd(variable, BindingKind.Path))
                { throw new GraphPlanningException("COHDBG003", $"Path variable '{variable}' must be a new MATCH binding.", query.Location); }
                declarations.Add(variable);
                if (!optional) { mandatoryVariables.Add(variable); }
            }
            for (int i = 0; i < path.Nodes.Count; i++)
            {
                var node = path.Nodes[i];
                Bind(node.Variable, BindingKind.Node);
                satisfiable &= ValidateNodeLabels(node, creating, tokens);
                ValidateProperties(node.Properties, creating);
                if (i == path.Relationships.Count) { continue; }
                var relationship = path.Relationships[i];
                Bind(relationship.Variable, BindingKind.Relationship);
                satisfiable &= ValidateRelationship(relationship, creating, tokens);
                ValidateProperties(relationship.Properties, creating);
            }
            return satisfiable;
        }
        void Bind(string? variable, BindingKind kind)
        {
            if (variable is null) { return; }
            if (variables.TryGetValue(variable, out var previous) && previous != kind)
            { throw new GraphPlanningException("COHDBG003", $"Variable '{variable}' has incompatible graph bindings.", query.Location); }
            if (!variables.ContainsKey(variable)) { declarations.Add(variable); }
            variables[variable] = kind;
            if (!optional) { mandatoryVariables.Add(variable); }
        }
        BindingKind RequireVariable(string variable, bool entity = false)
        {
            if (variable is null || !variables.TryGetValue(variable, out var kind)) { throw new GraphPlanningException("COHDBG001", $"Variable '{variable}' is not bound.", query.Location); }
            if (entity && kind == BindingKind.Path)
            { throw new GraphPlanningException("COHDBG003", $"Path variable '{variable}' cannot be deleted or used as a property owner.", query.Location); }
            return kind;
        }
        // Walks the predicate with an explicit stack, in source order: an AND chain of any length
        // and parentheses of any depth validate without recursion, as Neo4j's n-ary Ands carry
        // any number of predicates. Only evaluation recurses, and it checks the stack (COHDBG008).
        void ValidateExpression(GqlExpression? root)
        {
            if (root is null) { return; }
            var pending = new Stack<GqlExpression?>();
            pending.Push(root);
            while (pending.TryPop(out var expression))
            {
                switch (expression)
                {
                    case GqlLiteralExpression literal:
                        try { GraphDatabase.TypeOf(literal.Value); }
                        catch (DatabaseException error)
                        { throw new GraphPlanningException("COHDBG003", ErrorMessages.LiteralScalarRequired, literal.Location, error); }
                        if (literal.Value is double number && !double.IsFinite(number) || literal.Value is float single && !float.IsFinite(single))
                        { throw new GraphPlanningException("COHDBG003", ErrorMessages.LiteralScalarRequired, literal.Location); }
                        break;
                    case GqlVariableExpression variable:
                        RequireVariable(variable.Variable);
                        break;
                    case GqlParameterExpression parameter:
                        object? bound = GraphExpressionEvaluator.Parameter(parameter, _parameters);
                        if (snapshotted.Add(parameter.Name))
                        { ((Dictionary<string, object?>)_parameters!)[parameter.Name] = GraphCompositeValues.Snapshot(bound, parameter.Location); }
                        break;
                    case GqlPropertyExpression property:
                        RequireVariable(property.Variable, entity: true);
                        break;
                    case GqlBinaryExpression { Operator: "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" or "+" or "-" or "*" or "/" or "%" or "||" } binary:
                        pending.Push(binary.Right);
                        pending.Push(binary.Left);
                        break;
                    case GqlBinaryExpression { Operator: "AND" }:
                        throw new GraphPlanningException("COHDBG001", ErrorMessages.LogicalNodeRequired, expression?.Location);
                    case GqlLogicalExpression logical:
                        if (!Enum.IsDefined(logical.Operator))
                        { throw new GraphPlanningException("COHDBG001", $"Logical operator {(int)logical.Operator} is not defined.", expression?.Location); }
                        if (logical.Operands is null || logical.Operands.Count < 2)
                        { throw new GraphPlanningException("COHDBG001", "A logical predicate needs at least two operands.", expression?.Location); }
                        for (int i = logical.Operands.Count - 1; i >= 0; i--) { pending.Push(logical.Operands[i]); }
                        break;
                    case GqlUnaryExpression { Operator: "NOT" or "+" or "-" } unary:
                        pending.Push(unary.Operand);
                        break;
                    case GqlIsNullExpression test:
                        pending.Push(test.Operand);
                        break;
                    case GqlTruthTestExpression test:
                        pending.Push(test.Operand);
                        break;
                    case GqlInExpression membership:
                        pending.Push(membership.List);
                        pending.Push(membership.Operand);
                        break;
                    case GqlListExpression or GqlMapExpression or GqlCaseExpression or GqlCoalesceExpression or
                        GqlNullIfExpression or GqlMemberExpression or GqlSubscriptExpression:
                        foreach (var child in GraphValueExpressions.Children(expression).Reverse()) { pending.Push(child); }
                        break;
                    case GqlStringPredicateExpression predicate when Enum.IsDefined(predicate.Operator):
                        pending.Push(predicate.Right);
                        pending.Push(predicate.Left);
                        break;
                    case GqlLabeledPredicate labeled:
                        ValidateLabeledPredicate(labeled);
                        break;
                    case null:
                        throw new GraphPlanningException("COHDBG001", "A predicate has a missing operand.", expression?.Location);
                    default:
                        throw new GraphPlanningException("COHDBG001", "Unsupported graph predicate.", expression?.Location);
                }
            }
        }
        // A labeled predicate tests a node's labels or a relationship's type; a path has neither.
        // Its names resolve against the catalog of the variable's kind, as a pattern's do. It is a
        // Boolean primary that IS NOT LABELED, !, | (and NOT or OR once WHERE has them) can invert,
        // so an unknown name never empties the plan; it evaluates as a label no node carries.
        void ValidateLabeledPredicate(GqlLabeledPredicate labeled)
        {
            if (labeled.Variable is null) { throw new GraphPlanningException("COHDBG001", "A labeled predicate requires a variable.", labeled.Location); }
            var kind = RequireVariable(labeled.Variable);
            if (kind == BindingKind.Path)
            { throw new GraphPlanningException("COHDBG003", $"Path variable '{labeled.Variable}' has no labels; a labeled predicate tests a node or relationship.", labeled.Location); }
            if (labeled.LabelExpression is null) { throw new GraphPlanningException("COHDBG001", "A labeled predicate requires a label expression.", labeled.Location); }
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
        if (node.Labels is null) { throw new GraphPlanningException("COHDBG001", "A node pattern requires a label list.", null); }
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
                throw new GraphPlanningException("COHDBG001", $"A node pattern's labels must be empty or name exactly the conjunction of its label expression '{GraphLabelEvaluator.Describe(expression)}'.", null);
            }
            if (creating && conjunction is null)
            {
                throw new GraphPlanningException("COHDBG001", $"An inserted node takes a label conjunction such as :A&B; '{GraphLabelEvaluator.Describe(expression)}' selects nodes and cannot label a new one.", null);
            }
            required = conjunction ?? [];
        }
        // A chain may name a label many times; each is resolved once.
        foreach (string label in GraphLabelEvaluator.Distinct(names))
        {
            if (label is null) { throw new GraphPlanningException("COHDBG001", "A label name cannot be null.", null); }
            if (!creating) { tokens.HasLabel(label); }
            else if (string.IsNullOrWhiteSpace(label))
            {
                throw new GraphPlanningException("COHDBG001", "An inserted label cannot be empty or only whitespace.", null);
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
        if (properties is null) { throw new GraphPlanningException("COHDBG001", "A pattern requires a property map.", null); }
        foreach (var expression in properties.Values)
        {
            if (expression is GqlParameterExpression parameter)
            { GraphCompositeValues.Snapshot(GraphExpressionEvaluator.Parameter(parameter, _parameters), parameter.Location); }
            GraphExpressionEvaluator.Scalar(expression, _parameters);
        }
        if (!creating) { return; }
        foreach (string key in properties.Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new GraphPlanningException("COHDBG001", "An inserted property key cannot be empty or only whitespace.", null);
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
        { throw new GraphPlanningException("COHDBG001", $"Relationship direction {(int)relationship.Direction} is not defined.", null); }
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
                throw new GraphPlanningException("COHDBG001", $"A relationship pattern's type '{type}' must equal its label expression '{GraphLabelEvaluator.Describe(expression)}'.", null);
            }
            type = single;
            required = GraphLabelEvaluator.Conjunction(expression) ?? [];
        }
        if (creating)
        {
            if (type is null || relationship.Direction is not (GqlPatternDirection.Outgoing or GqlPatternDirection.Incoming))
            { throw new GraphPlanningException("COHDBG001", "Inserted relationships require a type and a directed pattern.", null); }
            if (string.IsNullOrWhiteSpace(type))
            { throw new GraphPlanningException("COHDBG001", "An inserted relationship type cannot be empty or only whitespace.", null); }
            return true;
        }
        foreach (string name in GraphLabelEvaluator.Distinct(names)) { tokens.HasRelationshipType(name); }
        foreach (string name in required)
        {
            if (!tokens.HasRelationshipType(name)) { return false; }
        }
        return true;
    }

    private enum BindingKind { Node, Relationship, Path }

    // Earlier paths and clauses supply argument bindings before any scan or seek, as Neo4j's
    // argumentLeafPlanner.scala:33-37 does (community/cypher/cypher-planner/.../steps/leafplanner).
    // A bound relationship supplies its adjacent endpoint when no node is already bound.
    // Fresh anchors come from Labels only, which holds a node's labels only when every match must carry
    // them all: a pure conjunction. A disjunction, negation or wildcard leaves Labels empty, and a
    // labeled predicate is never an equality, so neither can narrow the candidates and drop rows;
    // such a node scans every node and the executor evaluates the expression on each.
    //
    // The choice is the first distinct label, in Labels order, that has an index on a key the node
    // has a value for, and of that label's indexed keys the one whose first non-null value comes
    // first, inline properties before WHERE equalities. Selection costs the node's labels and values
    // plus the indexes its labels own, never their product: Neo4j's leaf planner likewise groups the
    // predicates once and visits only each label's own index descriptors
    // (cypher-planner .../leafplanner/index/NodeIndexLeafPlanner.scala:184, :201, :248-261, :279-296).
    private GraphAnchor ChooseAnchor(GqlPathPattern path, AnchorSources sources, IReadOnlySet<string> boundVariables)
    {
        for (int i = 0; i < path.Nodes.Count; i++)
        {
            if (path.Nodes[i].Variable is { } variable && boundVariables.Contains(variable))
            { return new GraphAnchor(i, path.Nodes[i].Labels.FirstOrDefault(), null, null); }
        }
        for (int i = 0; i < path.Relationships.Count; i++)
        {
            if (path.Relationships[i].Variable is { } variable && boundVariables.Contains(variable))
            { return new GraphAnchor(i, path.Nodes[i].Labels.FirstOrDefault(), null, null); }
        }
        for (int i = 0; i < path.Nodes.Count; i++)
        {
            var node = path.Nodes[i];
            if (node.Labels.Count == 0) { continue; }
            var inline = FirstValues(node.Properties);
            var equalities = sources.GetEqualities(node.Variable);
            if (inline.Count == 0 && equalities.Count == 0) { continue; }
            var indexes = sources.GetIndexedKeys();
            if (indexes.Count == 0) { break; }
            var tried = new HashSet<string>(StringComparer.Ordinal);
            foreach (string label in node.Labels)
            {
                if (!tried.Add(label) || !indexes.TryGetValue(label, out var keys)) { continue; }
                string? bestKey = null;
                FirstValue best = new(int.MaxValue, null!);
                foreach (string key in keys)
                {
                    // Inline positions precede every equality position, as the properties precede
                    // the WHERE clause.
                    FirstValue candidate;
                    if (inline.TryGetValue(key, out var property)) { candidate = property; }
                    else if (equalities.TryGetValue(key, out var equality)) { candidate = new(inline.Count + equality.Position, equality.Value); }
                    else { continue; }
                    if (candidate.Position < best.Position) { best = candidate; bestKey = key; }
                }
                if (bestKey is not null) { return new GraphAnchor(i, label, bestKey, best.Value); }
            }
        }
        return new GraphAnchor(0, path.Nodes[0].Labels.FirstOrDefault(), null, null);
    }

    /// <summary>Each key's first non-null inline value and its position among the node's non-null values.</summary>
    private Dictionary<string, FirstValue> FirstValues(IReadOnlyDictionary<string, GqlExpression> properties)
    {
        var values = new Dictionary<string, FirstValue>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
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
    /// equalities from the <c>WHERE</c> chain.
    /// </summary>
    private sealed class AnchorSources
    {
        private static readonly Dictionary<string, FirstValue> _none = new(StringComparer.Ordinal);
        private readonly GraphDatabase _database;
        private readonly TransactionSnapshot _snapshot;
        private readonly IReadOnlyDictionary<string, object?>? _parameters;
        private readonly GqlExpression? _predicate;
        private Dictionary<string, List<string>>? _indexedKeys;
        private Dictionary<string, Dictionary<string, FirstValue>>? _equalities;

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
        internal Dictionary<string, List<string>> GetIndexedKeys()
        {
            if (_indexedKeys is not null) { return _indexedKeys; }
            var indexed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var index in _database.Store.GetIndexes(_snapshot))
            {
                if (!indexed.TryGetValue(index.Label, out var keys)) { indexed.Add(index.Label, keys = []); }
                keys.Add(index.PropertyKey);
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

        /// <summary>
        /// Walks the predicate's <c>AND</c> chains once, through nested chains of any depth, with an
        /// explicit stack. Anchors are chosen before the predicate is validated, so a malformed
        /// hand-built chain or a null name is skipped here and rejected by validation.
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
    }
}
