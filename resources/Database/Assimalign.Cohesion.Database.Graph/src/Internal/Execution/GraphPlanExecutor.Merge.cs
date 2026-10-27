using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphPlanExecutor
{
    internal static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamMergeAsync(
        GraphDatabase database, GraphOperation operation, GqlMergeStage stage, GraphPathPlan sourcePlan,
        IReadOnlyList<Dictionary<string, object?>> input, ExpansionBudget budget,
        IReadOnlyDictionary<string, object?>? parameters, StrongBox<long> created,
        [EnumeratorCancellation] CancellationToken token, Action<bool>? onPatternWait = null)
    {
        foreach (var incoming in input)
        {
            token.ThrowIfCancellationRequested();
            operation.EnsureActive();
            budget.Consume();
            using var memory = new GraphMemoryOwner(budget.Memory);
            memory.Add(512 + 128L * incoming.Count + 256L * (stage.Path.Nodes.Count + stage.Path.Relationships.Count) +
                320L * (stage.Path.Nodes.Sum(node => (long)node.Properties.Count) + stage.Path.Relationships.Sum(edge => (long)edge.Properties.Count)));
            var binding = incoming.ToDictionary(pair => pair.Key, pair => operation.Updates.Current(pair.Value), StringComparer.Ordinal);
            var names = new HashSet<string>(binding.Keys, StringComparer.Ordinal);
            foreach (var node in stage.Path.Nodes) { if (node.Variable is { } name) { names.Add(name); } }
            foreach (var edge in stage.Path.Relationships) { if (edge.Variable is { } name) { names.Add(name); } }
            var hidden = new List<string>();
            int hiddenId = 0;
            var nodeKeys = new List<GraphMergeNodeIdentity>();
            var edgeKeys = new List<GraphMergeRelationshipIdentity>();
            var nodes = stage.Path.Nodes.Select(node =>
            {
                var properties = Values(node.Properties);
                nodeKeys.Add(new(Bound(node.Variable), GraphLabelEvaluator.InsertLabels(node), properties));
                return node with { Variable = node.Variable ?? Hidden(), Properties = Literals(properties) };
            }).ToArray();
            var edges = stage.Path.Relationships.Select((edge, index) =>
            {
                var properties = Values(edge.Properties);
                bool incomingEdge = edge.Direction == GqlPatternDirection.Incoming;
                edgeKeys.Add(new(incomingEdge ? index + 1 : index, incomingEdge ? index : index + 1,
                    GraphLabelEvaluator.InsertType(edge), edge.Direction is GqlPatternDirection.Undirected or GqlPatternDirection.LeftOrRight,
                    null, properties));
                return edge with { Variable = edge.Variable ?? Hidden(), Properties = Literals(properties) };
            }).ToArray();
            // Evaluate equalities once per input. Both probes honor the seam's view and include
            // overlays. Synthetic bindings make anonymous entities part of the freshness read set.
            var path = new GqlPathPattern(nodes, edges);
            var slots = new Dictionary<string, int>(StringComparer.Ordinal);
            var canonicalNodes = new List<GraphMergeNodeIdentity>();
            var nodeSlots = new int[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                string name = nodes[i].Variable!;
                if (!slots.TryGetValue(name, out int slot))
                { slot = canonicalNodes.Count; slots.Add(name, slot); canonicalNodes.Add(nodeKeys[i]); }
                nodeSlots[i] = slot;
            }
            var identity = new GraphMergePatternIdentity(canonicalNodes,
                edgeKeys.Select(edge => edge with { From = nodeSlots[edge.From], To = nodeSlots[edge.To] }).ToArray());
            var anchor = sourcePlan.Anchor;
            if (anchor.Value is GqlExpression)
            { anchor = anchor with { Value = nodeKeys[anchor.NodeIndex].Properties[anchor.Property!] }; }
            var plan = new GraphPathPlan(path, anchor) { Shape = GraphPathShape.Create(path) };
            var read = await GraphMergeConcurrency.MatchAsync(database, operation, identity, ProbeAsync, token,
                onPatternWait: onPatternWait).ConfigureAwait(false);
            if (read.Matches.Count != 0)
            {
                foreach (var match in read.Matches)
                {
                    var matched = new Dictionary<string, object?>(match, StringComparer.Ordinal);
                    using var rowMemory = new GraphMemoryOwner(budget.Memory);
                    rowMemory.RetainValue(matched, token);
                    await ApplyAsync(matched, stage.OnMatch, rowMemory).ConfigureAwait(false);
                    rowMemory.ReleaseValue(matched);
                    foreach (string name in hidden) { matched.Remove(name); }
                    rowMemory.RetainValue(matched, token);
                    yield return matched;
                }
            }
            else
            {
                var row = new Dictionary<string, object?>(binding, StringComparer.Ordinal);
                long count = await CreateMergeAsync(operation, path, row, parameters, token).ConfigureAwait(false);
                created.Value += count;
                using var rowMemory = new GraphMemoryOwner(budget.Memory);
                rowMemory.RetainValue(row, token);
                await ApplyAsync(row, stage.OnCreate, rowMemory).ConfigureAwait(false);
                // Existence constraints see the finished ON CREATE branch. Discovery and type
                // checks ran during creation; untouched new entities must be checked here too.
                foreach (var name in path.Nodes.Select(node => node.Variable!).Distinct(StringComparer.Ordinal))
                {
                    var node = (GraphNode)operation.Updates.Current(row[name])!;
                    if (operation.Updates.IsCreated(true, node.Id.Value))
                    { await database.ValidateNodeAsync(operation, node.Labels, node.Properties, token).ConfigureAwait(false); }
                }
                foreach (var edgePattern in path.Relationships)
                {
                    var edge = (GraphRelationship)operation.Updates.Current(row[edgePattern.Variable!])!;
                    await database.ValidateRelationshipAsync(operation, edge.Type, edge.Properties, token).ConfigureAwait(false);
                }
                rowMemory.ReleaseValue(row);
                foreach (string name in hidden) { row.Remove(name); }
                rowMemory.RetainValue(row, token);
                yield return row;
            }

            string Hidden()
            {
                string name;
                do { name = "$merge" + (hiddenId++).ToString(System.Globalization.CultureInfo.InvariantCulture); }
                while (!names.Add(name));
                hidden.Add(name);
                return name;
            }
            ulong? Bound(string? name)
            {
                if (name is null || !binding.TryGetValue(name, out var value)) { return null; }
                return value switch
                {
                    GraphNode item => item.Id.Value,
                    _ => throw new GraphPlanningException("CG03005", ErrorMessages.MergeBoundInvalid, stage.Location),
                };
            }
            IReadOnlyDictionary<string, object?> Values(IReadOnlyDictionary<string, GqlExpression> properties)
            {
                var values = properties.ToDictionary(pair => pair.Key,
                    pair => GraphExpressionEvaluator.Evaluate(pair.Value, binding, operation.EvaluationContext, parameters), StringComparer.Ordinal);
                foreach (var value in values.Values)
                { token.ThrowIfCancellationRequested(); budget.Consume(); ValidateMergeProperty(value, stage.Location); }
                memory.RetainValue(values, token);
                return values;
            }
            static IReadOnlyDictionary<string, GqlExpression> Literals(IReadOnlyDictionary<string, object?> values)
                => values.ToDictionary(pair => pair.Key, pair => (GqlExpression)new GqlLiteralExpression(pair.Value), StringComparer.Ordinal);
            async ValueTask<GraphMergeRead<Dictionary<string, object?>>> ProbeAsync(TransactionSnapshot view, CancellationToken ct)
            {
                // Snapshot override scopes are owned by the seam. The initial probe uses Context;
                // the latest probe uses OverrideReadSnapshot. Never bypass either with store reads.
                var previous = operation.Updates.OverrideReadSnapshot(view);
                try
                {
                    var matches = new List<Dictionary<string, object?>>();
                    var entities = new HashSet<GraphMergeEntity>();
                    // A ReadCommitted refresh can follow a logical pattern wait. Preserve
                    // incoming identities but re-read their values at this probe's view.
                    var probeBinding = new Dictionary<string, object?>(binding, StringComparer.Ordinal);
                    foreach (string name in probeBinding.Keys.ToArray())
                    {
                        probeBinding[name] = probeBinding[name] switch
                        {
                            GraphNode node when operation.Updates.FindNode(node.Id.Value) is { } stored => GraphDatabase.Materialize(stored),
                            GraphRelationship edge when operation.Updates.FindRelationship(edge.Id.Value) is { } stored => GraphDatabase.Materialize(stored),
                            var value => value,
                        };
                        // The create branch also carries incoming aliases that are outside
                        // this pattern. Keep them at the refreshed ReadCommitted view.
                        if (operation.Context.IsolationLevel == IsolationLevel.ReadCommitted)
                        { binding[name] = probeBinding[name]; }
                    }
                    await foreach (var match in MatchCoreStreamAsync(database, operation, plan,
                        probeBinding, budget, parameters, ct).ConfigureAwait(false))
                    {
                        var copy = new Dictionary<string, object?>(match.Bindings, StringComparer.Ordinal);
                        memory.Add(128);
                        memory.RetainValue(copy, ct);
                        matches.Add(copy);
                        foreach (var node in nodes) { Add(true, ((GraphNode)copy[node.Variable!]!).Id.Value); }
                        foreach (var edge in edges) { Add(false, ((GraphRelationship)copy[edge.Variable!]!).Id.Value); }
                    }
                    return new(matches, entities.ToArray());

                    void Add(bool node, ulong id)
                    { if (entities.Add(new(node, id))) { memory.Add(64); } }
                }
                finally { operation.Updates.OverrideReadSnapshot(previous); }
            }
            async ValueTask ApplyAsync(Dictionary<string, object?> row, IReadOnlyList<GqlSetItem> items, GraphMemoryOwner rowMemory)
            {
                if (items.Count != 0)
                { await operation.Updates.ApplyAsync(new GqlSetStage(items, stage.Location), [row], parameters, token, rowMemory).ConfigureAwait(false); }
            }
        }
    }

    internal static void ValidateMergeProperty(object? value, Assimalign.Cohesion.Database.Language.Location? location)
    {
        if (value is not (bool or string or byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double) ||
            value is double number && !double.IsFinite(number) || value is float single && !float.IsFinite(single))
        { throw new GraphPlanningException("CG03004", ErrorMessages.MergePropertyInvalid, location); }
    }

    private static async ValueTask<long> CreateMergeAsync(GraphOperation operation, GqlPathPattern path,
        Dictionary<string, object?> row, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        long created = 0;
        var nodes = new GraphNode[path.Nodes.Count];
        for (int i = 0; i < nodes.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var pattern = path.Nodes[i];
            if (row.TryGetValue(pattern.Variable!, out var bound))
            {
                if (bound is not GraphNode node || operation.Updates.FindNode(node.Id.Value) is not { } stored) { throw Invalid(); }
                nodes[i] = GraphDatabase.Materialize(stored);
                if (!AcceptNode(pattern, nodes[i], row, operation.EvaluationContext, parameters)) { throw Invalid(); }
                row[pattern.Variable!] = nodes[i];
            }
            else
            {
                nodes[i] = await operation.Updates.CreateNodeAsync(GraphLabelEvaluator.InsertLabels(pattern),
                    EvaluateProperties(pattern.Properties, row, operation.EvaluationContext, parameters), token, deferRequired: true).ConfigureAwait(false);
                row.Add(pattern.Variable!, nodes[i]);
                created++;
            }
        }
        for (int i = 0; i < path.Relationships.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var pattern = path.Relationships[i];
            bool reverse = pattern.Direction == GqlPatternDirection.Incoming;
            var edge = await operation.Updates.CreateRelationshipAsync(nodes[reverse ? i + 1 : i].Id,
                nodes[reverse ? i : i + 1].Id, GraphLabelEvaluator.InsertType(pattern),
                EvaluateProperties(pattern.Properties, row, operation.EvaluationContext, parameters), token, deferRequired: true).ConfigureAwait(false);
            row.Add(pattern.Variable!, edge);
            created++;
        }
        return created;

        GraphPlanningException Invalid() => new("CG03005", ErrorMessages.MergeBoundInvalid);
    }
}
