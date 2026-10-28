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
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Statement-local entity versions, published once after all stages have run.</summary>
[global::System.Diagnostics.DebuggerDisplay("GraphUpdates")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphUpdates.DebugView))]
internal sealed class GraphUpdates : IDisposable
{
    private readonly GraphDatabase _database;
    private readonly GraphOperation _operation;
    private readonly Dictionary<ulong, GraphNode> _nodes = [];
    private readonly Dictionary<string, HashSet<ulong>> _nodeLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, GraphRelationship> _relationships = [];
    private readonly Dictionary<(ulong Node, GraphIncidentDirection Direction), Dictionary<Guid, List<ulong>>> _createdIncident = [];
    private readonly Dictionary<ulong, Guid> _createdTypes = [];
    private readonly Dictionary<string, List<ulong>> _createdByType = new(StringComparer.Ordinal);
    private readonly Dictionary<GqlRelationshipPattern, Guid[]?> _typeSelections = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(bool Node, ulong Id)> _created = [];
    private readonly HashSet<(bool Node, ulong Id)> _changed = [];
    private readonly Dictionary<(bool Node, ulong Id), object> _entityValues = [];
    private GraphMemoryOwner? _memory;
    // Replace the MERGE-only read-view field with a lazy index cache; ordinary writes keep
    // the same object size and allocate no index state. The read override lives on MERGE.
    private Dictionary<(string Label, string Key), GraphOverlayIndex>? _nodeIndexes;

    // The MERGE seam owns this scope; every overlay-aware read uses the selected store view.
    internal TransactionSnapshot ReadSnapshot => _operation.MergeReadSnapshot ?? _operation.Context.Snapshot;
    internal TransactionSnapshot? OverrideReadSnapshot(TransactionSnapshot? snapshot)
    {
        var previous = _operation.OverrideMergeReadSnapshot(snapshot);
        // Type selections are view-dependent, including cached absence.
        _typeSelections.Clear();
        return previous;
    }

    internal void RefreshReadSnapshot() => _typeSelections.Clear();

    internal GraphUpdates(GraphDatabase database, GraphOperation operation) { _database = database; _operation = operation; }

    internal async ValueTask<GraphNode> CreateNodeAsync(IReadOnlyList<string> labels,
        IReadOnlyDictionary<string, object?> properties, CancellationToken token, bool deferRequired = false)
    {
        await _database.ValidateNodeAsync(_operation, labels, properties, token, deferRequired).ConfigureAwait(false);
        var node = new GraphNode(new GraphNodeId((ulong)_database.DataStorage.ReserveTransactionSequence()),
            Array.AsReadOnly(labels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(properties, StringComparer.Ordinal)));
        _created.Add((true, node.Id.Value));
        RememberNode(node, token);
        return node;
    }

    internal async ValueTask<GraphRelationship> CreateRelationshipAsync(GraphNodeId from, GraphNodeId to, string type,
        IReadOnlyDictionary<string, object?> properties, CancellationToken token, bool deferRequired = false)
    {
        var typeId = await _database.ValidateRelationshipAsync(_operation, type, properties, token, deferRequired).ConfigureAwait(false);
        _typeSelections.Clear();
        var edge = new GraphRelationship(new GraphRelationshipId((ulong)_database.DataStorage.ReserveTransactionSequence()), type, from, to,
            new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(properties, StringComparer.Ordinal)));
        _created.Add((false, edge.Id.Value));
        AccountEntity(false, edge.Id.Value, edge, token);
        Charge(640); // Created identity, type, global type list and the two incident-list entries.
        _relationships.Add(edge.Id.Value, edge);
        _createdTypes.Add(edge.Id.Value, typeId);
        if (!_createdByType.TryGetValue(type, out var typed)) { _createdByType.Add(type, typed = []); }
        typed.Add(edge.Id.Value);
        AddIncident(from.Value, GraphIncidentDirection.Outgoing);
        AddIncident(to.Value, GraphIncidentDirection.Incoming);
        return edge;

        void AddIncident(ulong id, GraphIncidentDirection direction)
        {
            if (!_createdIncident.TryGetValue((id, direction), out var types))
            { _createdIncident.Add((id, direction), types = []); }
            if (!types.TryGetValue(typeId, out var incident)) { types.Add(typeId, incident = []); }
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
        : _database.Store.FindRelationship(id, ReadSnapshot);
    internal void DeletedNode(ulong id)
    {
        if (_nodes.Remove(id, out var node)) { UpdateIndexes(node, null); ForgetLabels(node); }
    }
    internal void DeletedRelationship(ulong id) => _relationships.Remove(id);

    internal StoredGraphNode? FindNode(ulong id) => _nodes.TryGetValue(id, out var node)
        ? new StoredGraphNode(id, node.Labels, node.Properties) : _database.Store.FindNode(id, ReadSnapshot);

    private void RememberNode(GraphNode node, CancellationToken token = default)
    {
        AccountEntity(true, node.Id.Value, node, token);
        GraphNode? previous = _nodes.TryGetValue(node.Id.Value, out var remembered) ? remembered : null;
        UpdateIndexes(previous, node);
        if (previous is { } prior) { ForgetLabels(prior); }
        _nodes[node.Id.Value] = node;
        foreach (string label in node.Labels)
        {
            if (!_nodeLabels.TryGetValue(label, out var ids)) { _nodeLabels.Add(label, ids = []); }
            ids.Add(node.Id.Value);
        }
    }

    private void Charge(long bytes)
    {
        if (_operation.QueryBudget is not { } budget) { return; }
        (_memory ??= new GraphMemoryOwner(budget.Memory)).Add(bytes);
    }

    private void AccountEntity(bool node, ulong id, object value, CancellationToken token)
    {
        if (_operation.QueryBudget is null) { return; }
        if (_entityValues.TryGetValue((node, id), out var previous)) { _memory!.ReleaseValue(previous); }
        else { Charge(256); }
        _memory!.RetainValue(value, token);
        _entityValues[(node, id)] = value;
    }

    public void Dispose()
    {
        _nodes.Clear(); _nodeLabels.Clear(); _relationships.Clear(); _createdIncident.Clear();
        _createdTypes.Clear(); _createdByType.Clear(); _typeSelections.Clear(); _created.Clear(); _changed.Clear(); _entityValues.Clear();
        _memory?.Dispose();
        _memory = null;
        _nodeIndexes = null;
    }

    private void ForgetLabels(GraphNode node)
    {
        foreach (string label in node.Labels)
        {
            if (_nodeLabels.TryGetValue(label, out var ids))
            {
                ids.Remove(node.Id.Value);
                if (ids.Count == 0) { _nodeLabels.Remove(label); }
            }
        }
    }

    internal IReadOnlyList<StoredGraphNode> GetNodes(string? label, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var stored = label is null ? _database.Store.GetNodes(null, ReadSnapshot)
            : _database.Store.ScanLabels([label], ReadSnapshot, token);
        return MergeNodes(stored, label is null ? _nodes.Values : PendingNodes([label]));
    }

    internal IReadOnlyList<StoredGraphNode> GetUnionNodes(IReadOnlyList<string> labels, CancellationToken token)
        => MergeNodes(_database.Store.ScanLabels(labels, ReadSnapshot, token), PendingNodes(labels));

    internal async IAsyncEnumerable<StoredGraphNode> StreamAnchorAsync(GraphAnchor anchor,
        GraphPlanExecutor.ExpansionBudget budget, [EnumeratorCancellation] CancellationToken token)
    {
        using var memory = new GraphMemoryOwner(budget.Memory);
        var index = anchor.Property is { } indexedKey && anchor.Label is { } indexedLabel
            ? OverlayIndex(indexedLabel, indexedKey, budget, token) : null;
        // Select the access path's postings before charging comparisons. Only a NodeScan
        // visits unrelated labels; property residuals still charge every selected candidate.
        IEnumerable<GraphNode> candidates = index is not null
            ? (index.Added(anchor.Value) ?? []).Select(id => _nodes[id])
            : _nodes.Count == 0 ? _nodes.Values
            : anchor.UnionLabels is { } union ? PendingNodes(union, memory)
            : anchor.Label is { } label ? (_nodeLabels.TryGetValue(label, out var ids) ? ids : []).Select(id => _nodes[id])
            : _nodes.Values;
        var selected = new List<GraphNode>();
        memory.Add(32);
        foreach (var node in candidates)
        {
            token.ThrowIfCancellationRequested();
            budget.Consume();
            if (
            (anchor.UnionLabels is { } labels ? node.Labels.Any(label => labels.Contains(label, StringComparer.Ordinal))
                : anchor.Label is null || node.Labels.Contains(anchor.Label, StringComparer.Ordinal)) &&
            (anchor.Property is null || node.Properties.TryGetValue(anchor.Property, out var value) &&
                GraphValueComparer.Instance.Equals(value, anchor.Value)))
            { memory.Add(16); selected.Add(node); }
        }
        selected.Sort(static (left, right) => left.Id.Value.CompareTo(right.Id.Value));
        var pending = selected;
        var snapshot = ReadSnapshot;
        // A Snapshot plan can retain an index dropped before the latest-view MERGE probe.
        // Fall back under the same writer/pattern locks; the matcher retains all equalities.
        bool useIndex = anchor.Property is not null && HasReadIndex(anchor.Label!, anchor.Property);
        IAsyncEnumerable<StoredGraphNode> persisted = useIndex && anchor.Property is { } property
            ? _database.Store.StreamIndexAsync(anchor.Label!, property, anchor.Value, snapshot, token, budget.Consume, memory.Add)
            : StreamStored();
        int position = 0;
        await foreach (var stored in persisted.ConfigureAwait(false))
        {
            while (position < pending.Count && pending[position].Id.Value <= stored.Id)
            {
                var node = pending[position++];
                yield return new(node.Id.Value, node.Labels, node.Properties);
            }
            if (!_nodes.ContainsKey(stored.Id)) { yield return stored; }
        }
        while (position < pending.Count)
        {
            token.ThrowIfCancellationRequested();
            var node = pending[position++];
            yield return new(node.Id.Value, node.Labels, node.Properties);
        }

        async IAsyncEnumerable<StoredGraphNode> StreamStored()
        {
            IEnumerable<StoredGraphNode> nodes = anchor.UnionLabels is { } labels
                ? _database.Store.StreamLabels(labels, snapshot, token, budget.Consume, memory.Add)
                : anchor.Label is { } label ? _database.Store.StreamLabels([label], snapshot, token, budget.Consume, memory.Add)
                : _database.Store.StreamAllNodes(snapshot, token, budget.Consume, memory.Add);
            foreach (var node in nodes) { yield return node; }
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    internal async IAsyncEnumerable<StoredGraphRelationship> StreamTypeAnchorAsync(IReadOnlyList<string> types,
        GraphPlanExecutor.ExpansionBudget budget, [EnumeratorCancellation] CancellationToken token)
    {
        using var memory = new GraphMemoryOwner(budget.Memory);
        memory.Add(128);
        var pending = new List<ulong>();
        foreach (string type in types)
        {
            token.ThrowIfCancellationRequested();
            if (!_createdByType.TryGetValue(type, out var ids)) { continue; }
            memory.Add(32 + 16L * ids.Count);
            pending.AddRange(ids);
        }
        pending.Sort();
        int position = 0;
        foreach (var stored in _database.Store.StreamRelationshipTypes(types, ReadSnapshot,
            token, Visit, memory.Add))
        {
            while (position < pending.Count && pending[position] <= stored.Id)
            {
                if (Created(pending[position++]) is not { } created) { continue; }
                yield return created;
            }
            var edge = _relationships.TryGetValue(stored.Id, out var updated)
                ? new StoredGraphRelationship(stored.Id, updated.From.Value, updated.To.Value, updated.Type, updated.Properties) : stored;
            memory.RetainValue(edge.Properties, token);
            try { yield return edge; }
            finally { memory.ReleaseValue(edge.Properties); }
        }
        while (position < pending.Count)
        {
            if (Created(pending[position++]) is { } created) { yield return created; }
        }
        await Task.CompletedTask.ConfigureAwait(false);

        void Visit()
        {
            token.ThrowIfCancellationRequested();
            budget.EnsureAvailable();
            budget.VisitCandidate();
            budget.Consume();
            token.ThrowIfCancellationRequested();
        }
        StoredGraphRelationship? Created(ulong id)
        {
            token.ThrowIfCancellationRequested();
            if (!_relationships.ContainsKey(id)) { return null; }
            Visit();
            return FindRelationship(id);
        }
    }

    private IEnumerable<GraphNode> PendingNodes(IReadOnlyList<string> labels, GraphMemoryOwner? memory = null)
    {
        var selected = new HashSet<ulong>();
        memory?.Add(128);
        foreach (string label in labels)
        {
            if (_nodeLabels.TryGetValue(label, out var ids))
            {
                foreach (ulong id in ids) { if (selected.Add(id)) { memory?.Add(64); } }
            }
        }
        return selected.Select(id => _nodes[id]);
    }

    private IReadOnlyList<StoredGraphNode> MergeNodes(IReadOnlyList<StoredGraphNode> stored, IEnumerable<GraphNode> pending)
        => _nodes.Count == 0 ? stored : stored.Where(node => !_nodes.ContainsKey(node.Id))
            .Concat(pending.Select(node => new StoredGraphNode(node.Id.Value, node.Labels, node.Properties)))
            .OrderBy(node => node.Id).ToArray();

    private bool HasReadIndex(string label, string key) => _operation.MergeReadSnapshot is null ||
        _database.Store.HasIndex(label, key, ReadSnapshot);

    private GraphOverlayIndex? OverlayIndex(string label, string key, GraphPlanExecutor.ExpansionBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // No overlay means no value lookup or extra work for ordinary indexed MATCH.
        if (_nodes.Count == 0 && _nodeIndexes is null) { return null; }
        budget.Consume(); // One lookup per probe once the statement has index changes.
        if (_nodeIndexes is not null && _nodeIndexes.TryGetValue((label, key), out var existing)) { return existing; }
        _nodeIndexes ??= [];
        Charge(256);
        var index = new GraphOverlayIndex();
        foreach (var node in _nodes.Values)
        {
            token.ThrowIfCancellationRequested();
            budget.Consume(); // Build once from earlier statement changes, never once per row.
            index.Replace(label, key, null, node, Charge);
        }
        _nodeIndexes.Add((label, key), index);
        return index;
    }

    private void UpdateIndexes(GraphNode? previous, GraphNode? current)
    {
        if (_nodeIndexes is null) { return; }
        foreach (var entry in _nodeIndexes)
        {
            _operation.FunctionCancellation.ThrowIfCancellationRequested();
            _operation.QueryBudget?.Consume();
            entry.Value.Replace(entry.Key.Label, entry.Key.Key, previous, current, Charge);
        }
    }

    internal Guid[]? SelectTypes(GqlRelationshipPattern pattern)
    {
        if (!_typeSelections.TryGetValue(pattern, out var types))
        {
            string? name = pattern.LabelExpression is GqlLabelName label ? label.Name : pattern.Type;
            types = pattern.LabelExpression is GqlLabelWildcard || pattern.LabelExpression is null && name is null ? null
                : name is not null ? _database.Catalog.FindRelationshipType(name, ReadSnapshot) is { } type ? [type.Id] : []
                : _database.Catalog.GetRelationshipTypes(ReadSnapshot)
                    .Where(type => GraphLabelEvaluator.Accepts(pattern, type.Name)).Select(type => type.Id).ToArray();
            _typeSelections.Add(pattern, types);
        }
        return types;
    }

    internal ValueTask<IReadOnlyList<StoredGraphRelationship>> GetIncidentAsync(ulong id, CancellationToken token)
        => GetIncidentAsync(id, GraphIncidentDirection.Both, null, token);

    internal async ValueTask<IReadOnlyList<StoredGraphRelationship>> GetIncidentAsync(ulong id, GraphIncidentDirection direction,
        IReadOnlyCollection<Guid>? typeIds, CancellationToken token)
    {
        var result = new List<StoredGraphRelationship>();
        await foreach (var edge in _database.Store.GetIncidentAsync(id, direction, typeIds, ReadSnapshot, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            result.Add(_relationships.TryGetValue(edge.Id, out var updated)
                ? new StoredGraphRelationship(edge.Id, updated.From.Value, updated.To.Value, updated.Type, updated.Properties) : edge);
        }
        foreach (var selected in direction == GraphIncidentDirection.Both
            ? new[] { GraphIncidentDirection.Outgoing, GraphIncidentDirection.Incoming } : new[] { direction })
        {
            if (!_createdIncident.TryGetValue((id, selected), out var types)) { continue; }
            foreach (var type in typeIds ?? types.Keys)
            {
                if (!types.TryGetValue(type, out var incident)) { continue; }
                foreach (ulong edgeId in incident)
                {
                    token.ThrowIfCancellationRequested();
                    if (!_relationships.TryGetValue(edgeId, out var created) ||
                        direction == GraphIncidentDirection.Both && selected == GraphIncidentDirection.Incoming && created.From.Value == id) { continue; }
                    result.Add(FindRelationship(edgeId)!.Value);
                }
            }
        }
        result.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return result;
    }

    // Each selected type/direction range is already ordered by relationship identity.
    // Merge one head per range and the statement overlay so DFS can stop at its budget
    // without loading the incident list. Directory reads finish before tree reads.
    internal async IAsyncEnumerable<StoredGraphRelationship> StreamIncidentAsync(ulong id, GraphIncidentDirection direction,
        IReadOnlyCollection<Guid>? typeIds, [EnumeratorCancellation] CancellationToken token, GraphPlanExecutor.ExpansionBudget? budget = null)
    {
        token.ThrowIfCancellationRequested();
        using var memory = budget is null ? null : new GraphMemoryOwner(budget.Memory);
        memory?.Add(512);
        var snapshot = ReadSnapshot;
        var selectedTypes = typeIds?.Distinct().ToArray();
        var directions = direction == GraphIncidentDirection.Both
            ? new[] { GraphIncidentDirection.Outgoing, GraphIncidentDirection.Incoming } : new[] { direction };
        var streams = new List<IAsyncEnumerator<StoredGraphRelationship>>();
        var pending = new PriorityQueue<IAsyncEnumerator<StoredGraphRelationship>, ulong>();
        var reserved = new HashSet<ulong>(); // Only merge heads, never the degree-sized visited set.
        ulong? returned = null;
        try
        {
            foreach (var selected in directions)
            {
                if (selectedTypes is not null)
                {
                    foreach (Guid type in selectedTypes) { await PrimeAsync(selected, type).ConfigureAwait(false); }
                }
                else
                {
                    await foreach (var head in _database.Store.GetIncidentTypesAsync(id, selected, snapshot, token).ConfigureAwait(false))
                    {
                        Reserve(head.HeadId); // Discovery and its stream head share one budget charge.
                        await PrimeAsync(selected, head.Type).ConfigureAwait(false);
                    }
                }
            }
            var overlay = CreatedIncident().GetAsyncEnumerator(token);
            streams.Add(overlay);
            if (await overlay.MoveNextAsync().ConfigureAwait(false)) { pending.Enqueue(overlay, overlay.Current.Id); }
            while (pending.TryDequeue(out var stream, out ulong edgeId))
            {
                token.ThrowIfCancellationRequested();
                if (returned != edgeId)
                {
                    var edge = stream.Current;
                    returned = edgeId; // self-loops occur in both direction ranges
                    reserved.Remove(edgeId);
                    yield return _relationships.TryGetValue(edgeId, out var updated)
                        ? new StoredGraphRelationship(edgeId, updated.From.Value, updated.To.Value, updated.Type, updated.Properties) : edge;
                }
                if (await stream.MoveNextAsync().ConfigureAwait(false)) { pending.Enqueue(stream, stream.Current.Id); }
            }
        }
        finally
        {
            await DisposeIncidentStreamsAsync(streams).ConfigureAwait(false);
        }

        void Reserve(ulong edgeId)
        {
            if (returned == edgeId || !reserved.Add(edgeId)) { return; }
            budget?.VisitCandidate();
            budget?.Consume();
            token.ThrowIfCancellationRequested();
        }

        async ValueTask PrimeAsync(GraphIncidentDirection selected, Guid type)
        {
            token.ThrowIfCancellationRequested();
            memory?.Add(8192); // A 64-entry adjacency-key batch and its merge/cursor state.
            var stream = StreamRange(selected, type).GetAsyncEnumerator(token);
            streams.Add(stream);
            if (await stream.MoveNextAsync().ConfigureAwait(false)) { pending.Enqueue(stream, stream.Current.Id); }
        }

        async IAsyncEnumerable<StoredGraphRelationship> StreamRange(GraphIncidentDirection selected, Guid type)
        {
            using var headMemory = budget is null ? null : new GraphMemoryOwner(budget.Memory);
            headMemory?.Add(128);
            await foreach (var edge in _database.Store.StreamIncidentRangeAsync(id, selected, [type], snapshot, Reserve, token).ConfigureAwait(false))
            {
                headMemory?.RetainValue(edge.Properties, token);
                try { yield return edge; }
                finally { headMemory?.ReleaseValue(edge.Properties); }
            }
        }

        async IAsyncEnumerable<StoredGraphRelationship> CreatedIncident()
        {
            // Only the bounded statement-local overlay is sorted; persisted ranges stay streaming.
            var ids = new HashSet<ulong>();
            using var overlayMemory = budget is null ? null : new GraphMemoryOwner(budget.Memory);
            foreach (var selected in directions)
            {
                if (!_createdIncident.TryGetValue((id, selected), out var types)) { continue; }
                foreach (Guid type in selectedTypes ?? (IEnumerable<Guid>)types.Keys)
                {
                    if (types.TryGetValue(type, out var incident))
                    { foreach (ulong edgeId in incident) { if (ids.Add(edgeId)) { overlayMemory?.Add(80); } } }
                }
            }
            await Task.CompletedTask.ConfigureAwait(false);
            foreach (ulong edgeId in ids.Order())
            {
                token.ThrowIfCancellationRequested();
                if (_relationships.ContainsKey(edgeId)) { Reserve(edgeId); yield return FindRelationship(edgeId)!.Value; }
            }
        }
    }

    internal static async ValueTask DisposeIncidentStreamsAsync(IEnumerable<IAsyncEnumerator<StoredGraphRelationship>> streams)
    {
        Exception? first = null;
        foreach (var stream in streams)
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { first ??= exception; } // Cleanup must attempt every owned stream.
        }
        if (first is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw(); }
    }

    internal async ValueTask ApplyAsync(GqlStage stage, List<Dictionary<string, object?>> bindings,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken token, GraphMemoryOwner? bindingMemory = null)
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
                        _ => throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, item.Location),
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
                        _ => throw new GraphPlanningException("CG05001", ErrorMessages.StageUnsupported, item.Location),
                    });
                }
            }

            void Apply(string variable, Assimalign.Cohesion.Database.Language.Location? location, Func<object, object> update)
            {
                if (!binding.TryGetValue(variable, out var value))
                { throw new GraphPlanningException("CG03003", ErrorMessages.EntityBindingRequired, location); }
                if (value is null) { return; }
                value = Current(value);
                if (value is GraphNode node)
                {
                    if (FindNode(node.Id.Value) is null)
                    { throw new GraphPlanningException("CG03003", ErrorMessages.EntityBindingRequired, location); }
                    var replacement = (GraphNode)update(value);
                    if (!node.Labels.SequenceEqual(replacement.Labels) || !Identical(node.Properties, replacement.Properties))
                    {
                        _changed.Add((true, node.Id.Value));
                        RememberNode(replacement, token);
                        touchedNodes.Add(node.Id.Value);
                    }
                }
                else if (value is GraphRelationship relationship)
                {
                    if (FindRelationship(relationship.Id.Value) is null)
                    { throw new GraphPlanningException("CG03003", ErrorMessages.EntityBindingRequired, location); }
                    var replacement = (GraphRelationship)update(value);
                    if (!Identical(relationship.Properties, replacement.Properties))
                    {
                        _changed.Add((false, relationship.Id.Value));
                        AccountEntity(false, relationship.Id.Value, replacement, token);
                        _relationships[relationship.Id.Value] = replacement;
                        touchedRelationships.Add(relationship.Id.Value);
                    }
                }
                else { throw new GraphPlanningException("CG03003", ErrorMessages.EntityBindingRequired, location); }
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
            bindingMemory?.ReleaseValue(binding);
            foreach (string name in binding.Keys.ToArray())
            { binding[name] = Refresh(binding[name], touchedNodes, touchedRelationships, refreshed); }
            bindingMemory?.RetainValue(binding, token);
        }
    }

    // MERGE applies a branch per input row. Refresh the complete output table afterward,
    // so a later row's change also reaches earlier rows' nested aliases, as ordinary SET does.
    internal void RefreshMergeBindings(List<Dictionary<string, object?>> bindings, CancellationToken token,
        GraphMemoryOwner bindingMemory)
    {
        if (bindings.Count == 0 || _nodes.Count == 0 && _relationships.Count == 0) { return; }
        using var memory = _operation.QueryBudget!.Memory.Reserve(128 + 64L * (_nodes.Count + _relationships.Count));
        var nodes = _nodes.Keys.ToHashSet();
        var relationships = _relationships.Keys.ToHashSet();
        var refreshed = new Dictionary<object, object?>(ReferenceEqualityComparer.Instance);
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            bindingMemory.ReleaseValue(binding);
            foreach (string name in binding.Keys.ToArray())
            { binding[name] = Refresh(binding[name], nodes, relationships, refreshed); }
            bindingMemory.RetainValue(binding, token);
        }
    }

    internal async ValueTask<long> PublishAsync(CancellationToken token)
    {
        long count = 0;
        foreach (var node in _nodes.Values)
        {
            if (IsCreated(true, node.Id.Value))
            { await _database.Store.CreateNodeAsync(new StoredGraphNode(node.Id.Value, node.Labels, node.Properties), _database.NodeLabelIds(node.Labels, _operation), _operation.Context, token).ConfigureAwait(false); }
            else
            { await _database.Store.UpdateNodeAsync(node.Id.Value, node.Labels, node.Properties, _database.NodeLabelIds(node.Labels, _operation), _operation.Context, token).ConfigureAwait(false); }
            if (_changed.Contains((true, node.Id.Value)) && !_created.Contains((true, node.Id.Value))) { count++; }
        }
        foreach (var edge in _relationships.Values)
        {
            if (IsCreated(false, edge.Id.Value))
            { await _database.Store.CreateRelationshipAsync(new StoredGraphRelationship(edge.Id.Value, edge.From.Value, edge.To.Value, edge.Type, edge.Properties), _createdTypes[edge.Id.Value], _operation.Context, token).ConfigureAwait(false); }
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
    { GraphNode node => node.Properties, GraphRelationship edge => edge.Properties, _ => throw new GraphPlanningException("CG03003", ErrorMessages.EntityBindingRequired) };

    private static object WithProperties(object entity, Dictionary<string, object?> properties) => entity switch
    { GraphNode node => node with { Properties = new ReadOnlyDictionary<string, object?>(properties) },
        GraphRelationship edge => edge with { Properties = new ReadOnlyDictionary<string, object?>(properties) },
        _ => throw new GraphPlanningException("CG03003", ErrorMessages.EntityBindingRequired) };

    private static object SetLabels(object entity, IReadOnlyList<string> labels, bool remove)
    {
        if (entity is not GraphNode node) { throw new GraphPlanningException("CG03003", ErrorMessages.EntityBindingRequired); }
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

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphUpdates value)
        {
            _ = value;
        }
    }
}
