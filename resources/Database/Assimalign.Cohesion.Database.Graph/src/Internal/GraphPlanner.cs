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
                foreach (string label in node.Labels)
                {
                    if (!creating && _database.Catalog.FindLabel(label, _snapshot) is null)
                    { throw new DatabaseException($"COHDBG002: Unknown label '{label}'."); }
                }
            }
            foreach (var relationship in path.Relationships)
            {
                Bind(relationship.Variable, BindingKind.Relationship);
                if (!Enum.IsDefined(relationship.Direction) || creating && (relationship.Type is null || relationship.Direction == GqlPatternDirection.Undirected))
                { throw new DatabaseException("COHDBG001: Inserted relationships require a type and a directed pattern."); }
                if (!creating && relationship.Type is { } type && _database.Catalog.FindRelationshipType(type, _snapshot) is null)
                { throw new DatabaseException($"COHDBG002: Unknown relationship type '{type}'."); }
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
        void RequireVariable(string variable, bool entity = false)
        {
            if (!variables.TryGetValue(variable, out var kind)) { throw new DatabaseException($"COHDBG001: Variable '{variable}' is not bound."); }
            if (entity && kind == BindingKind.Path)
            { throw new DatabaseException($"COHDBG003: Path variable '{variable}' cannot be deleted or used as a property owner."); }
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
                default: throw new DatabaseException("COHDBG001: Unsupported graph predicate.");
            }
        }
    }

    private enum BindingKind { Node, Relationship, Path }

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
