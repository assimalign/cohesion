using System;
using System.Collections;
using System.Collections.Generic;
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
    private readonly HashSet<(bool Node, ulong Id)> _created = [];

    internal GraphUpdates(GraphDatabase database, GraphOperation operation) { _database = database; _operation = operation; }

    internal void Created(GraphNode node) => _created.Add((true, node.Id.Value));
    internal void Created(GraphRelationship relationship) => _created.Add((false, relationship.Id.Value));
    internal void DeletedNode(ulong id) => _nodes.Remove(id);
    internal void DeletedRelationship(ulong id) => _relationships.Remove(id);

    internal StoredGraphNode? FindNode(ulong id) => _nodes.TryGetValue(id, out var node)
        ? new StoredGraphNode(id, node.Labels, node.Properties) : _database.Store.FindNode(id, _operation.Context.Snapshot);

    internal IReadOnlyList<StoredGraphNode> GetNodes(string? label) => _nodes.Count == 0
        ? _database.Store.GetNodes(label, _operation.Context.Snapshot)
        : _database.Store.GetNodes(null, _operation.Context.Snapshot)
            .Select(node => FindNode(node.Id)!.Value).Where(node => label is null || node.Labels.Contains(label, StringComparer.Ordinal)).ToArray();

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
                ? new StoredGraphRelationship(edge.Id, updated.From.Value, updated.To.Value, updated.Type, updated.Properties) : edge).ToArray();

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
                    RefreshBinding(binding);
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
                    RefreshBinding(binding);
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
                if (value is GraphNode node)
                {
                    if (FindNode(node.Id.Value) is null)
                    { throw new GraphPlanningException("COHDBG003", ErrorMessages.EntityBindingRequired, location); }
                    _nodes[node.Id.Value] = (GraphNode)update(value);
                    touchedNodes.Add(node.Id.Value);
                }
                else if (value is GraphRelationship relationship)
                {
                    if (_database.Store.FindRelationship(relationship.Id.Value, _operation.Context.Snapshot) is null)
                    { throw new GraphPlanningException("COHDBG003", ErrorMessages.EntityBindingRequired, location); }
                    _relationships[relationship.Id.Value] = (GraphRelationship)update(value);
                    touchedRelationships.Add(relationship.Id.Value);
                }
                else { throw new GraphPlanningException("COHDBG003", ErrorMessages.EntityBindingRequired, location); }
            }
        }
        // Validate the complete resulting entity, reusing INSERT's catalog discovery and schema rules.
        foreach (ulong id in touchedNodes)
        { await _database.ValidateNodeAsync(_operation, _nodes[id].Labels, _nodes[id].Properties, token).ConfigureAwait(false); }
        foreach (ulong id in touchedRelationships)
        { await _database.ValidateRelationshipAsync(_operation, _relationships[id].Type, _relationships[id].Properties, token).ConfigureAwait(false); }
        foreach (var binding in bindings) { RefreshBinding(binding); }
    }

    internal async ValueTask<long> PublishAsync(CancellationToken token)
    {
        long count = 0;
        foreach (var node in _nodes.Values)
        {
            var prior = _database.Store.FindNode(node.Id.Value, _operation.Context.Snapshot);
            bool changed = prior is { } old && (!old.Labels.SequenceEqual(node.Labels) || !Identical(old.Properties, node.Properties));
            await _database.Store.UpdateNodeAsync(node.Id.Value, node.Labels, node.Properties, _operation.Context, token).ConfigureAwait(false);
            if (changed && !_created.Contains((true, node.Id.Value))) { count++; }
        }
        foreach (var edge in _relationships.Values)
        {
            var prior = _database.Store.FindRelationship(edge.Id.Value, _operation.Context.Snapshot);
            bool changed = prior is { } old && !Identical(old.Properties, edge.Properties);
            await _database.Store.UpdateRelationshipAsync(edge.Id.Value, edge.Properties, _operation.Context, token).ConfigureAwait(false);
            if (changed && !_created.Contains((false, edge.Id.Value))) { count++; }
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

    private static object WithProperties(object entity, IReadOnlyDictionary<string, object?> properties) => entity switch
    { GraphNode node => node with { Properties = properties }, GraphRelationship edge => edge with { Properties = properties },
        _ => throw new GraphPlanningException("COHDBG003", ErrorMessages.EntityBindingRequired) };

    private static object SetLabels(object entity, IReadOnlyList<string> labels, bool remove)
    {
        if (entity is not GraphNode node) { throw new GraphPlanningException("COHDBG003", ErrorMessages.EntityBindingRequired); }
        return node with { Labels = (remove ? node.Labels.Except(labels, StringComparer.Ordinal) : node.Labels.Concat(labels).Distinct(StringComparer.Ordinal))
            .Order(StringComparer.Ordinal).ToArray() };
    }

    private void RefreshBinding(Dictionary<string, object?> binding)
    { foreach (string name in binding.Keys.ToArray()) { binding[name] = Refresh(binding[name]); } }

    private object? Refresh(object? value)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return value switch
        {
            GraphNode node when _nodes.TryGetValue(node.Id.Value, out var updated) => updated,
            GraphRelationship edge when _relationships.TryGetValue(edge.Id.Value, out var updated) => updated,
            GraphPath path => new GraphPath(path.Nodes.Select(node => (GraphNode)Refresh(node)!).ToArray(),
                path.Relationships.Select(edge => (GraphRelationship)Refresh(edge)!).ToArray()),
            IReadOnlyDictionary<string, object?> map => map.ToDictionary(pair => pair.Key, pair => Refresh(pair.Value), StringComparer.Ordinal),
            IList list when value is not byte[] => list.Cast<object?>().Select(Refresh).ToArray(),
            _ => value,
        };
    }
}
