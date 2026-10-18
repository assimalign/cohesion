using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Storage;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Statement-local entity versions, published once after all stages have run.</summary>
internal sealed class GraphUpdates
{
    private readonly GraphDatabase _database;
    private readonly GraphOperation _operation;
    private readonly Dictionary<ulong, GraphNode> _nodes = [];
    private readonly Dictionary<ulong, GraphRelationship> _relationships = [];
    private readonly Dictionary<ulong, List<ulong>> _createdIncident = [];
    private readonly HashSet<(bool Node, ulong Id)> _created = [];
    private readonly HashSet<(bool Node, ulong Id)> _changed = [];

    internal GraphUpdates(GraphDatabase database, GraphOperation operation) { _database = database; _operation = operation; }

    internal async ValueTask<GraphNode> CreateNodeAsync(IReadOnlyList<string> labels,
        IReadOnlyDictionary<string, object?> properties, CancellationToken token)
    {
        await _database.ValidateNodeAsync(_operation, labels, properties, token).ConfigureAwait(false);
        var node = new GraphNode(new GraphNodeId((ulong)_database.DataStorage.ReserveTransactionSequence()),
            Array.AsReadOnly(labels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(properties, StringComparer.Ordinal)));
        _created.Add((true, node.Id.Value));
        _nodes.Add(node.Id.Value, node);
        return node;
    }

    internal async ValueTask<GraphRelationship> CreateRelationshipAsync(GraphNodeId from, GraphNodeId to, string type,
        IReadOnlyDictionary<string, object?> properties, CancellationToken token)
    {
        await _database.ValidateRelationshipAsync(_operation, type, properties, token).ConfigureAwait(false);
        var edge = new GraphRelationship(new GraphRelationshipId((ulong)_database.DataStorage.ReserveTransactionSequence()), type, from, to,
            new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(properties, StringComparer.Ordinal)));
        _created.Add((false, edge.Id.Value));
        _relationships.Add(edge.Id.Value, edge);
        AddIncident(from.Value);
        if (to != from) { AddIncident(to.Value); }
        return edge;

        void AddIncident(ulong id)
        {
            if (!_createdIncident.TryGetValue(id, out var incident)) { _createdIncident.Add(id, incident = []); }
            incident.Add(edge.Id.Value);
        }
    }

    internal bool IsCreated(bool node, ulong id) => _created.Contains((node, id));
    internal object? Current(object? value) => value switch
    {
        GraphNode node when _nodes.TryGetValue(node.Id.Value, out var updated) && node != updated => updated,
        GraphRelationship edge when _relationships.TryGetValue(edge.Id.Value, out var updated) && edge != updated => updated,
        _ => value,
    };

    internal StoredGraphRelationship? FindRelationship(ulong id) => _relationships.TryGetValue(id, out var edge)
        ? new StoredGraphRelationship(id, edge.From.Value, edge.To.Value, edge.Type, edge.Properties)
        : _database.Store.FindRelationship(id, _operation.Context.Snapshot);
    internal void DeletedNode(ulong id) => _nodes.Remove(id);
    internal void DeletedRelationship(ulong id) => _relationships.Remove(id);

    internal StoredGraphNode? FindNode(ulong id) => _nodes.TryGetValue(id, out var node)
        ? new StoredGraphNode(id, node.Labels, node.Properties) : _database.Store.FindNode(id, _operation.Context.Snapshot);

    internal IReadOnlyList<StoredGraphNode> GetNodes(string? label) => _nodes.Count == 0
        ? _database.Store.GetNodes(label, _operation.Context.Snapshot)
        : _database.Store.GetNodes(null, _operation.Context.Snapshot)
            .Where(node => !_nodes.ContainsKey(node.Id)).Concat(_nodes.Values.Select(node =>
                new StoredGraphNode(node.Id.Value, node.Labels, node.Properties))).Where(node => label is null || node.Labels.Contains(label, StringComparer.Ordinal)).OrderBy(node => node.Id).ToArray();

    internal async ValueTask<IReadOnlyList<StoredGraphNode>> SearchIndexAsync(string label, string key, object? value, CancellationToken token)
    {
        var found = await _database.Store.SearchIndexAsync(label, key, value, _operation.Context.Snapshot, token).ConfigureAwait(false);
        return found.Where(node => !_nodes.ContainsKey(node.Id))
            .Concat(_nodes.Values.Where(node => node.Labels.Contains(label, StringComparer.Ordinal) &&
                node.Properties.TryGetValue(key, out var actual) && GraphValueComparer.Instance.Equals(actual, value))
                .Select(node => new StoredGraphNode(node.Id.Value, node.Labels, node.Properties))).OrderBy(node => node.Id).ToArray();
    }

    internal async ValueTask<IReadOnlyList<StoredGraphRelationship>> GetIncidentAsync(ulong id, CancellationToken token)
        => (await _database.Store.GetIncidentAsync(id, _operation.Context.Snapshot, token).ConfigureAwait(false))
            .Select(edge => _relationships.TryGetValue(edge.Id, out var updated)
                ? new StoredGraphRelationship(edge.Id, updated.From.Value, updated.To.Value, updated.Type, updated.Properties) : edge)
            .Concat((_createdIncident.TryGetValue(id, out var incident) ? incident : Enumerable.Empty<ulong>())
                .Where(edgeId => _relationships.ContainsKey(edgeId)).Select(edgeId => FindRelationship(edgeId)!.Value))
            .OrderBy(edge => edge.Id).ToArray();

    internal async ValueTask ApplyAsync(GqlStage stage, List<Dictionary<string, object?>> bindings,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        var touchedNodes = new HashSet<ulong>();
        var touchedRelationships = new HashSet<ulong>();
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            if (stage is GqlSetStage set)
            {
                foreach (var item in set.Items)
                {
                    Apply(item.Variable, item.Location, value => item switch
                    {
                        GqlSetPropertyItem property => SetProperty(value, property.Property,
                            GraphExpressionEvaluator.PropertyScalar(property.Value, binding, _operation.EvaluationContext, parameters)),
                        GqlSetPropertiesItem map => SetProperties(value, map, binding, parameters),
                        GqlSetLabelsItem labels => SetLabels(value, labels.Labels, remove: false),
                        _ => throw new GraphPlanningException("COHDBG001", ErrorMessages.StageUnsupported, item.Location),
                    });
                }
            }
            else if (stage is GqlRemoveStage remove)
            {
                foreach (var item in remove.Items)
                {
                    Apply(item.Variable, item.Location, value => item switch
                    {
                        GqlRemovePropertyItem property => SetProperty(value, property.Property, null),
                        GqlRemoveLabelsItem labels => SetLabels(value, labels.Labels, remove: true),
                        _ => throw new GraphPlanningException("COHDBG001", ErrorMessages.StageUnsupported, item.Location),
                    });
                }
            }

            void Apply(string variable, Assimalign.Cohesion.Database.Language.Location? location, Func<object, object> update)
            {
                if (!binding.TryGetValue(variable, out var value))
                { throw new GraphPlanningException("COHDBG003", ErrorMessages.EntityBindingRequired, location); }
                if (value is null) { return; }
                value = Current(value);
                if (value is GraphNode node)
                {
                    if (FindNode(node.Id.Value) is null)
                    { throw new GraphPlanningException("COHDBG003", ErrorMessages.EntityBindingRequired, location); }
                    var replacement = (GraphNode)update(value);
                    if (!node.Labels.SequenceEqual(replacement.Labels) || !Identical(node.Properties, replacement.Properties))
                    {
                        _changed.Add((true, node.Id.Value));
                        _nodes[node.Id.Value] = replacement;
                        touchedNodes.Add(node.Id.Value);
                    }
                }
                else if (value is GraphRelationship relationship)
                {
                    if (FindRelationship(relationship.Id.Value) is null)
                    { throw new GraphPlanningException("COHDBG003", ErrorMessages.EntityBindingRequired, location); }
                    var replacement = (GraphRelationship)update(value);
                    if (!Identical(relationship.Properties, replacement.Properties))
                    {
                        _changed.Add((false, relationship.Id.Value));
                        _relationships[relationship.Id.Value] = replacement;
                        touchedRelationships.Add(relationship.Id.Value);
                    }
                }
                else { throw new GraphPlanningException("COHDBG003", ErrorMessages.EntityBindingRequired, location); }
            }
        }
        // Validate the complete resulting entity, reusing INSERT's catalog discovery and schema rules.
        foreach (ulong id in touchedNodes)
        { await _database.ValidateNodeAsync(_operation, _nodes[id].Labels, _nodes[id].Properties, token).ConfigureAwait(false); }
        foreach (ulong id in touchedRelationships)
        { await _database.ValidateRelationshipAsync(_operation, _relationships[id].Type, _relationships[id].Properties, token).ConfigureAwait(false); }
        // Shared composites are traversed once per stage and copied only along changed branches.
        var refreshed = new Dictionary<object, object?>(ReferenceEqualityComparer.Instance);
        foreach (var binding in bindings)
        {
            foreach (string name in binding.Keys.ToArray())
            { binding[name] = Refresh(binding[name], touchedNodes, touchedRelationships, refreshed); }
        }
    }

    internal async ValueTask<long> PublishAsync(CancellationToken token)
    {
        long count = 0;
        foreach (var node in _nodes.Values)
        {
            if (IsCreated(true, node.Id.Value))
            { await _database.Store.CreateNodeAsync(new StoredGraphNode(node.Id.Value, node.Labels, node.Properties), _operation.Context, token).ConfigureAwait(false); }
            else
            { await _database.Store.UpdateNodeAsync(node.Id.Value, node.Labels, node.Properties, _operation.Context, token).ConfigureAwait(false); }
            if (_changed.Contains((true, node.Id.Value)) && !_created.Contains((true, node.Id.Value))) { count++; }
        }
        foreach (var edge in _relationships.Values)
        {
            if (IsCreated(false, edge.Id.Value))
            { await _database.Store.CreateRelationshipAsync(new StoredGraphRelationship(edge.Id.Value, edge.From.Value, edge.To.Value, edge.Type, edge.Properties), _operation.Context, token).ConfigureAwait(false); }
            else
            { await _database.Store.UpdateRelationshipAsync(edge.Id.Value, edge.Properties, _operation.Context, token).ConfigureAwait(false); }
            if (_changed.Contains((false, edge.Id.Value)) && !_created.Contains((false, edge.Id.Value))) { count++; }
        }
        return count;
    }

    private static bool Identical(IReadOnlyDictionary<string, object?> left, IReadOnlyDictionary<string, object?> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && Equals(pair.Value, value));

    private static object SetProperty(object entity, string key, object? value)
    {
        var properties = new Dictionary<string, object?>(Properties(entity), StringComparer.Ordinal);
        if (value is null) { properties.Remove(key); } else { properties[key] = value; }
        return WithProperties(entity, properties);
    }

    private object SetProperties(object entity, GqlSetPropertiesItem map, Dictionary<string, object?> binding, IReadOnlyDictionary<string, object?>? parameters)
    {
        var properties = map.Merge ? new Dictionary<string, object?>(Properties(entity), StringComparer.Ordinal) : new(StringComparer.Ordinal);
        foreach (var pair in map.Properties)
        {
            object? value = GraphExpressionEvaluator.PropertyScalar(pair.Value, binding, _operation.EvaluationContext, parameters);
            if (value is null) { properties.Remove(pair.Key); } else { properties[pair.Key] = value; }
        }
        return WithProperties(entity, properties);
    }

    private static IReadOnlyDictionary<string, object?> Properties(object entity) => entity switch
    { GraphNode node => node.Properties, GraphRelationship edge => edge.Properties, _ => throw new GraphPlanningException("COHDBG003", ErrorMessages.EntityBindingRequired) };

    private static object WithProperties(object entity, Dictionary<string, object?> properties) => entity switch
    { GraphNode node => node with { Properties = new ReadOnlyDictionary<string, object?>(properties) },
        GraphRelationship edge => edge with { Properties = new ReadOnlyDictionary<string, object?>(properties) },
        _ => throw new GraphPlanningException("COHDBG003", ErrorMessages.EntityBindingRequired) };

    private static object SetLabels(object entity, IReadOnlyList<string> labels, bool remove)
    {
        if (entity is not GraphNode node) { throw new GraphPlanningException("COHDBG003", ErrorMessages.EntityBindingRequired); }
        return node with { Labels = Array.AsReadOnly((remove ? node.Labels.Except(labels, StringComparer.Ordinal) : node.Labels.Concat(labels).Distinct(StringComparer.Ordinal))
            .Order(StringComparer.Ordinal).ToArray()) };
    }

    private object? Refresh(object? value, HashSet<ulong> nodes, HashSet<ulong> relationships,
        Dictionary<object, object?> memo)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (value is GraphNode node) { return nodes.Contains(node.Id.Value) ? Current(value) : value; }
        if (value is GraphRelationship edge) { return relationships.Contains(edge.Id.Value) ? Current(value) : value; }
        if (value is null || value is not (GraphPath or IReadOnlyDictionary<string, object?> or IList) || value is byte[]) { return value; }
        if (memo.TryGetValue(value, out var prior)) { return prior; }
        object result = value;
        if (value is IReadOnlyList<GraphNode> nodeList) { result = RefreshList(nodeList); }
        else if (value is IReadOnlyList<GraphRelationship> edgeList) { result = RefreshList(edgeList); }
        else if (value is GraphPath path)
        {
            var newNodes = RefreshList(path.Nodes);
            var newEdges = RefreshList(path.Relationships);
            if (!ReferenceEquals(newNodes, path.Nodes) || !ReferenceEquals(newEdges, path.Relationships))
            { result = new GraphPath(newNodes, newEdges); }
        }
        else if (value is IReadOnlyDictionary<string, object?> map)
        {
            Dictionary<string, object?>? copy = null;
            foreach (var pair in map)
            {
                var next = Refresh(pair.Value, nodes, relationships, memo);
                if (!ReferenceEquals(next, pair.Value))
                { copy ??= new Dictionary<string, object?>(map, StringComparer.Ordinal); copy[pair.Key] = next; }
            }
            if (copy is not null) { result = new ReadOnlyDictionary<string, object?>(copy); }
        }
        else if (value is IList list)
        {
            object?[]? copy = null;
            for (int i = 0; i < list.Count; i++)
            {
                var original = list[i];
                var next = Refresh(original, nodes, relationships, memo);
                if (!ReferenceEquals(next, original))
                { copy ??= list.Cast<object?>().ToArray(); copy[i] = next; }
            }
            if (copy is not null) { result = copy; }
        }
        memo.TryAdd(value, result);
        return result;

        IReadOnlyList<T> RefreshList<T>(IReadOnlyList<T> list) where T : struct
        {
            if (memo.TryGetValue(list, out var cached)) { return (IReadOnlyList<T>)cached!; }
            T[]? copy = null;
            for (int i = 0; i < list.Count; i++)
            {
                var next = (T)Refresh(list[i], nodes, relationships, memo)!;
                if (!EqualityComparer<T>.Default.Equals(next, list[i])) { copy ??= list.ToArray(); copy[i] = next; }
            }
            var refreshed = copy ?? list;
            memo.Add(list, refreshed);
            return refreshed;
        }
    }
}
