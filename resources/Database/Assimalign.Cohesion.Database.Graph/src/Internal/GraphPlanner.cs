using System;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed record GraphAnchor(int NodeIndex, string? Label, string? Property, object? Value);
internal sealed record GraphPathPlan(GqlPathPattern Pattern, GraphAnchor Anchor);
internal sealed record GraphPlan(GqlQueryExpression Query, IReadOnlyList<GraphPathPlan> Matches);

internal sealed class GraphPlanner
{
    private readonly GraphDatabaseInstance _database;
    private readonly TransactionSnapshot _snapshot;

    /// <summary>Initializes a new instance of the <see cref="GraphPlanner"/> class.</summary>
    /// <param name="database">The graph database whose catalog and store resolve labels, relationship types, and indexes.</param>
    /// <param name="snapshot">The transaction snapshot that catalog and index lookups observe.</param>
    public GraphPlanner(GraphDatabaseInstance database, TransactionSnapshot snapshot)
    {
        _database = database;
        _snapshot = snapshot;
    }

    internal GraphPlan Plan(GqlQueryExpression query)
    {
        var variables = new Dictionary<string, BindingKind>(StringComparer.Ordinal);
        var paths = new List<GraphPathPlan>();
        foreach (var path in query.Matches)
        {
            Validate(path, creating: false);
            paths.Add(new GraphPathPlan(path, ChooseAnchor(path, query.Predicate)));
        }
        ValidateExpression(query.Predicate);
        foreach (var path in query.Creates) { Validate(path, creating: true); }
        foreach (var variable in query.DeleteVariables) { RequireVariable(variable, entity: true); }
        foreach (var projection in query.Projections) { RequireVariable(projection.Variable, entity: projection.Property is not null); }
        if (query.Matches.Count == 0 && query.Creates.Count == 0)
        { throw new DatabaseException("COHDBG001: A graph statement requires a match or insertion pattern."); }
        if (query.Creates.Count != 0 && query.DeleteVariables.Count != 0)
        { throw new DatabaseException("COHDBG001: Insertion and deletion cannot share a statement."); }
        if (query.DeleteVariables.Count != 0 && query.Projections.Count != 0)
        { throw new DatabaseException("COHDBG001: Deletion cannot be followed by projection in this subset."); }
        return new GraphPlan(query, paths);

        void Validate(GqlPathPattern path, bool creating)
        {
            if (path.Nodes.Count == 0 || path.Relationships.Count != path.Nodes.Count - 1 || path.Relationships.Count > 64)
            { throw new DatabaseException("COHDBG001: A finite path requires one more node than relationships and at most 64 hops."); }
            foreach (var node in path.Nodes)
            {
                Bind(node.Variable, BindingKind.Node);
                ValidateNodeLabels(node, creating);
                ValidateProperties(node.Properties, creating);
            }
            foreach (var relationship in path.Relationships)
            {
                Bind(relationship.Variable, BindingKind.Relationship);
                ValidateRelationship(relationship, creating);
                ValidateProperties(relationship.Properties, creating);
            }
            if (path.Variable is { } variable)
            {
                if (creating || !variables.TryAdd(variable, BindingKind.Path))
                { throw new DatabaseException($"COHDBG003: Path variable '{variable}' must be a new MATCH binding."); }
            }
        }
        void Bind(string? variable, BindingKind kind)
        {
            if (variable is null) { return; }
            if (variables.TryGetValue(variable, out var previous) && previous != kind)
            { throw new DatabaseException($"COHDBG003: Variable '{variable}' has incompatible graph bindings."); }
            variables[variable] = kind;
        }
        BindingKind RequireVariable(string variable, bool entity = false)
        {
            if (!variables.TryGetValue(variable, out var kind)) { throw new DatabaseException($"COHDBG001: Variable '{variable}' is not bound."); }
            if (entity && kind == BindingKind.Path)
            { throw new DatabaseException($"COHDBG003: Path variable '{variable}' cannot be deleted or used as a property owner."); }
            return kind;
        }
        void ValidateExpression(GqlExpression? expression, int depth = 0)
        {
            if (depth > 256) { throw new DatabaseException("COHDBG001: Predicate nesting exceeds 256 levels."); }
            switch (expression)
            {
                case null or GqlLiteralExpression: return;
                case GqlPropertyExpression property: RequireVariable(property.Variable, entity: true); return;
                case GqlBinaryExpression binary when binary.Operator is "AND" or "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=":
                    ValidateExpression(binary.Left, depth + 1); ValidateExpression(binary.Right, depth + 1); return;
                case GqlLabeledPredicate labeled: ValidateLabeledPredicate(labeled); return;
                default: throw new DatabaseException("COHDBG001: Unsupported graph predicate.");
            }
        }
        // A labeled predicate tests a node's labels or a relationship's type; a path has neither.
        // Its names resolve against the catalog of the variable's kind, as a pattern's do.
        void ValidateLabeledPredicate(GqlLabeledPredicate labeled)
        {
            if (labeled.Variable is null) { throw new DatabaseException("COHDBG001: A labeled predicate requires a variable."); }
            var kind = RequireVariable(labeled.Variable);
            if (kind == BindingKind.Path)
            { throw new DatabaseException($"COHDBG003: Path variable '{labeled.Variable}' has no labels; a labeled predicate tests a node or relationship."); }
            if (labeled.LabelExpression is null) { throw new DatabaseException("COHDBG001: A labeled predicate requires a label expression."); }
            foreach (string name in GraphLabelEvaluator.Names(labeled.LabelExpression))
            {
                if (kind == BindingKind.Node) { RequireLabel(name); }
                else { RequireRelationshipType(name); }
            }
        }
    }

    /// <summary>
    /// Validates a node pattern's label requirement. A label expression is the whole requirement,
    /// so <c>Labels</c> must be empty or list its conjunction. Every name must be a catalog label
    /// when matching, including names under <c>!</c> and <c>|</c>; insertion takes a pure
    /// conjunction and may introduce new labels.
    /// </summary>
    private void ValidateNodeLabels(GqlNodePattern node, bool creating)
    {
        if (node.Labels is null) { throw new DatabaseException("COHDBG001: A node pattern requires a label list."); }
        IReadOnlyList<string> names = node.Labels;
        if (node.LabelExpression is { } expression)
        {
            names = GraphLabelEvaluator.Names(expression);
            var conjunction = GraphLabelEvaluator.Conjunction(expression);
            if (node.Labels.Count != 0 && (conjunction is null ||
                !new HashSet<string>(node.Labels, StringComparer.Ordinal).SetEquals(conjunction)))
            {
                throw new DatabaseException($"COHDBG001: A node pattern's labels must be empty or name exactly the conjunction of its label expression '{expression}'.");
            }
            if (creating && conjunction is null)
            {
                throw new DatabaseException($"COHDBG001: An inserted node takes a label conjunction such as :A&B; '{expression}' selects nodes and cannot label a new one.");
            }
        }
        foreach (string label in names)
        {
            if (label is null) { throw new DatabaseException("COHDBG001: A label name cannot be null."); }
            if (!creating) { RequireLabel(label); }
            else if (string.IsNullOrWhiteSpace(label))
            {
                throw new DatabaseException("COHDBG001: An inserted label cannot be empty or only whitespace.");
            }
        }
    }

    /// <summary>
    /// Validates a pattern's literal property map. Storage cannot hold an empty or all-whitespace
    /// key, and an ISO delimited name such as <c>{" ": 1}</c> can spell one, so insertion rejects
    /// it here, before anything is written, instead of letting the store throw.
    /// </summary>
    private static void ValidateProperties(IReadOnlyDictionary<string, object?>? properties, bool creating)
    {
        if (properties is null) { throw new DatabaseException("COHDBG001: A pattern requires a property map."); }
        if (!creating) { return; }
        foreach (string key in properties.Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new DatabaseException("COHDBG001: An inserted property key cannot be empty or only whitespace.");
            }
        }
    }

    /// <summary>
    /// Validates a relationship pattern's direction and type requirement. A label expression is
    /// the whole requirement, so <c>Type</c> must be null or its single name. Every name must be a
    /// catalog type when matching. Insertion needs one type and one direction, so <c>-[]-</c>,
    /// <c>&lt;-[]-&gt;</c> and their abbreviations cannot insert.
    /// </summary>
    private void ValidateRelationship(GqlRelationshipPattern relationship, bool creating)
    {
        if (!Enum.IsDefined(relationship.Direction))
        { throw new DatabaseException($"COHDBG001: Relationship direction {(int)relationship.Direction} is not defined."); }
        string? type = relationship.Type;
        IReadOnlyList<string> names = type is null ? [] : [type];
        if (relationship.LabelExpression is { } expression)
        {
            names = GraphLabelEvaluator.Names(expression);
            string? single = (expression as GqlLabelName)?.Name;
            if (type is not null && !string.Equals(type, single, StringComparison.Ordinal))
            {
                throw new DatabaseException($"COHDBG001: A relationship pattern's type '{type}' must equal its label expression '{expression}'.");
            }
            type = single;
        }
        if (creating)
        {
            if (type is null || relationship.Direction is not (GqlPatternDirection.Outgoing or GqlPatternDirection.Incoming))
            { throw new DatabaseException("COHDBG001: Inserted relationships require a type and a directed pattern."); }
            if (string.IsNullOrWhiteSpace(type))
            { throw new DatabaseException("COHDBG001: An inserted relationship type cannot be empty or only whitespace."); }
            return;
        }
        foreach (string name in names) { RequireRelationshipType(name); }
    }

    private void RequireLabel(string label)
    {
        if (_database.Catalog.FindLabel(label, _snapshot) is null)
        { throw new DatabaseException($"COHDBG002: Unknown label '{label}'."); }
    }

    private void RequireRelationshipType(string type)
    {
        if (_database.Catalog.FindRelationshipType(type, _snapshot) is null)
        { throw new DatabaseException($"COHDBG002: Unknown relationship type '{type}'."); }
    }

    private enum BindingKind { Node, Relationship, Path }

    // Anchors come from Labels only, which holds a node's labels only when every match must carry
    // them all: a pure conjunction. A disjunction, negation or wildcard leaves Labels empty, and a
    // labeled predicate is never an equality, so neither can narrow the candidates and drop rows;
    // such a node scans every node and the executor evaluates the expression on each.
    private GraphAnchor ChooseAnchor(GqlPathPattern path, GqlExpression? predicate)
    {
        for (int i = 0; i < path.Nodes.Count; i++)
        {
            var node = path.Nodes[i];
            foreach (string label in node.Labels)
            {
                var values = node.Properties.Concat(Equalities(predicate, node.Variable));
                foreach (var value in values)
                {
                    if (value.Value is not null && _database.Store.HasIndex(label, value.Key, _snapshot))
                    { return new GraphAnchor(i, label, value.Key, value.Value); }
                }
            }
        }
        return new GraphAnchor(0, path.Nodes[0].Labels.FirstOrDefault(), null, null);
    }

    private static IEnumerable<KeyValuePair<string, object?>> Equalities(GqlExpression? expression, string? variable)
    {
        if (expression is not GqlBinaryExpression binary) { yield break; }
        if (binary.Operator == "AND")
        {
            foreach (var item in Equalities(binary.Left, variable)) { yield return item; }
            foreach (var item in Equalities(binary.Right, variable)) { yield return item; }
        }
        if (binary.Operator == "=")
        {
            if (binary.Left is GqlPropertyExpression left && left.Variable == variable && binary.Right is GqlLiteralExpression right)
            { yield return new(left.Property, right.Value); }
            if (binary.Right is GqlPropertyExpression property && property.Variable == variable && binary.Left is GqlLiteralExpression literal)
            { yield return new(property.Property, literal.Value); }
        }
    }
}
