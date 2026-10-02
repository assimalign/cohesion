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
        var anchors = new AnchorSources(_database, _snapshot, query.Predicate);
        foreach (var path in query.Matches)
        {
            Validate(path, creating: false);
            paths.Add(new GraphPathPlan(path, ChooseAnchor(path, anchors)));
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
                    case GqlLiteralExpression:
                        break;
                    case GqlPropertyExpression property:
                        RequireVariable(property.Variable, entity: true);
                        break;
                    case GqlBinaryExpression { Operator: "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" } binary:
                        pending.Push(binary.Right);
                        pending.Push(binary.Left);
                        break;
                    case GqlBinaryExpression { Operator: "AND" }:
                        throw new DatabaseException("COHDBG001: AND joins predicates in a GqlLogicalExpression; a GqlBinaryExpression holds one comparison.");
                    case GqlLogicalExpression logical:
                        if (!Enum.IsDefined(logical.Operator))
                        { throw new DatabaseException($"COHDBG001: Logical operator {(int)logical.Operator} is not defined."); }
                        if (logical.Operands is null || logical.Operands.Count < 2)
                        { throw new DatabaseException("COHDBG001: A logical predicate needs at least two operands."); }
                        for (int i = logical.Operands.Count - 1; i >= 0; i--) { pending.Push(logical.Operands[i]); }
                        break;
                    case GqlLabeledPredicate labeled:
                        ValidateLabeledPredicate(labeled);
                        break;
                    case null:
                        throw new DatabaseException("COHDBG001: A predicate has a missing operand.");
                    default:
                        throw new DatabaseException("COHDBG001: Unsupported graph predicate.");
                }
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
            foreach (string name in GraphLabelEvaluator.Distinct(GraphLabelEvaluator.Names(labeled.LabelExpression)))
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
                throw new DatabaseException($"COHDBG001: A node pattern's labels must be empty or name exactly the conjunction of its label expression '{GraphLabelEvaluator.Describe(expression)}'.");
            }
            if (creating && conjunction is null)
            {
                throw new DatabaseException($"COHDBG001: An inserted node takes a label conjunction such as :A&B; '{GraphLabelEvaluator.Describe(expression)}' selects nodes and cannot label a new one.");
            }
        }
        // A chain may name a label many times; each is resolved once.
        foreach (string label in GraphLabelEvaluator.Distinct(names))
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
                throw new DatabaseException($"COHDBG001: A relationship pattern's type '{type}' must equal its label expression '{GraphLabelEvaluator.Describe(expression)}'.");
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
        foreach (string name in GraphLabelEvaluator.Distinct(names)) { RequireRelationshipType(name); }
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
    //
    // The choice is the first distinct label, in Labels order, that has an index on a key the node
    // has a value for, and of that label's indexed keys the one whose first non-null value comes
    // first, inline properties before WHERE equalities. Selection costs the node's labels and values
    // plus the indexes its labels own, never their product: Neo4j's leaf planner likewise groups the
    // predicates once and visits only each label's own index descriptors
    // (cypher-planner .../leafplanner/index/NodeIndexLeafPlanner.scala:184, :201, :248-261, :279-296).
    private static GraphAnchor ChooseAnchor(GqlPathPattern path, AnchorSources sources)
    {
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
    private static Dictionary<string, FirstValue> FirstValues(IReadOnlyDictionary<string, object?> properties)
    {
        var values = new Dictionary<string, FirstValue>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (property.Value is not null) { values.TryAdd(property.Key, new FirstValue(values.Count, property.Value)); }
        }
        return values;
    }

    /// <summary>A candidate anchor value and its source position.</summary>
    private readonly record struct FirstValue(int Position, object Value);

    /// <summary>
    /// What anchor selection reads from outside the pattern, each read at most once per plan: the
    /// visible indexes grouped by label, and every variable's <c>variable.key = literal</c>
    /// equalities from the <c>WHERE</c> chain.
    /// </summary>
    private sealed class AnchorSources
    {
        private static readonly Dictionary<string, FirstValue> _none = new(StringComparer.Ordinal);
        private readonly GraphDatabaseInstance _database;
        private readonly TransactionSnapshot _snapshot;
        private readonly GqlExpression? _predicate;
        private Dictionary<string, List<string>>? _indexedKeys;
        private Dictionary<string, Dictionary<string, FirstValue>>? _equalities;

        /// <summary>Initializes a new instance of the <see cref="AnchorSources"/> class.</summary>
        /// <param name="database">The database whose store lists the visible indexes.</param>
        /// <param name="snapshot">The snapshot the index list observes.</param>
        /// <param name="predicate">The statement's <c>WHERE</c> predicate, if any.</param>
        public AnchorSources(GraphDatabaseInstance database, TransactionSnapshot snapshot, GqlExpression? predicate)
        {
            _database = database;
            _snapshot = snapshot;
            _predicate = predicate;
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
        private static Dictionary<string, Dictionary<string, FirstValue>> Collect(GqlExpression? predicate)
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
                if (binary.Left is GqlPropertyExpression left && binary.Right is GqlLiteralExpression right) { Add(left, right.Value); }
                else if (binary.Right is GqlPropertyExpression property && binary.Left is GqlLiteralExpression literal) { Add(property, literal.Value); }
            }
            return collected;

            void Add(GqlPropertyExpression property, object? value)
            {
                if (property.Variable is null || property.Property is null || value is null) { return; }
                if (!collected.TryGetValue(property.Variable, out var values))
                {
                    collected.Add(property.Variable, values = new Dictionary<string, FirstValue>(StringComparer.Ordinal));
                }
                values.TryAdd(property.Property, new FirstValue(values.Count, value));
            }
        }
    }
}
