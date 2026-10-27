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
    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamMergeAsync(
        GraphDatabase database, GraphOperation operation, GqlMergeStage stage, GraphPathPlan sourcePlan,
        IReadOnlyList<Dictionary<string, object?>> input, ExpansionBudget budget,
        IReadOnlyDictionary<string, object?>? parameters, StrongBox<long> created,
        [EnumeratorCancellation] CancellationToken token)
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
                nodeKeys.Add(new(Bound(node.Variable, true), GraphLabelEvaluator.InsertLabels(node), properties));
                return node with { Variable = node.Variable ?? Hidden(), Properties = Literals(properties) };
            }).ToArray();
            var edges = stage.Path.Relationships.Select((edge, index) =>
            {
                var properties = Values(edge.Properties);
                ulong? boundId = Bound(edge.Variable, false);
                bool incomingEdge = edge.Direction == GqlPatternDirection.Incoming;
                edgeKeys.Add(new(incomingEdge ? index + 1 : index, incomingEdge ? index : index + 1,
                    (edge.Type is null && edge.LabelExpression is null && edge.Variable is { } boundName && binding.GetValueOrDefault(boundName) is GraphRelationship boundRelationship
                        ? boundRelationship.Type : GraphLabelEvaluator.InsertType(edge)), edge.Direction is GqlPatternDirection.Undirected or GqlPatternDirection.LeftOrRight,
                    boundId, properties));
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
                else
                {
                    var previous = canonicalNodes[slot];
                    var properties = new Dictionary<string, object?>(previous.Properties, StringComparer.Ordinal);
                    foreach (var property in nodeKeys[i].Properties)
                    {
                        if (properties.TryGetValue(property.Key, out var value) && !GraphValueComparer.Instance.Equals(value, property.Value))
                        { throw new GraphPlanningException("CG03005", ErrorMessages.MergeBoundInvalid, stage.Location); }
                        properties[property.Key] = property.Value;
                    }
                    canonicalNodes[slot] = previous with { Labels = previous.Labels.Concat(nodeKeys[i].Labels).Distinct(StringComparer.Ordinal).ToArray(), Properties = properties };
                }
                nodeSlots[i] = slot;
            }
            var identity = new GraphMergePatternIdentity(canonicalNodes,
                edgeKeys.Select(edge => edge with { From = nodeSlots[edge.From], To = nodeSlots[edge.To] }).ToArray());
            var plan = new GraphPathPlan(path, sourcePlan.Anchor) { Shape = GraphPathShape.Create(path) };
            var read = await GraphMergeConcurrency.MatchAsync(database, operation, identity, ProbeAsync, token).ConfigureAwait(false);
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
                long count = await CreateMergeAsync(operation, path, row, parameters, budget, token).ConfigureAwait(false);
                created.Value += count;
                using var rowMemory = new GraphMemoryOwner(budget.Memory);
                rowMemory.RetainValue(row, token);
                await ApplyAsync(row, stage.OnCreate, rowMemory).ConfigureAwait(false);
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
            ulong? Bound(string? name, bool node)
            {
                if (name is null || !binding.TryGetValue(name, out var value)) { return null; }
                return value switch
                {
                    GraphNode item when node => item.Id.Value,
                    GraphRelationship item when !node => item.Id.Value,
                    _ => throw new GraphPlanningException("CG03005", ErrorMessages.MergeBoundInvalid, stage.Location),
                };
            }
            IReadOnlyDictionary<string, object?> Values(IReadOnlyDictionary<string, GqlExpression> properties)
            {
                var values = EvaluateProperties(properties, binding, operation.EvaluationContext, parameters);
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
                    foreach (var edge in edges)
                    {
                        if (binding.GetValueOrDefault(edge.Variable!) is GraphRelationship bound)
                        { Add(true, bound.From.Value); Add(true, bound.To.Value); }
                    }
                    await foreach (var match in MatchCoreStreamAsync(database, operation, plan,
                        new(binding, StringComparer.Ordinal), budget, parameters, ct).ConfigureAwait(false))
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
        Dictionary<string, object?> row, IReadOnlyDictionary<string, object?>? parameters, ExpansionBudget budget, CancellationToken token)
    {
        long created = 0;
        var nodes = new GraphNode[path.Nodes.Count];
        // Bound edges imply endpoint identities. Solve their orientations as one constraint
        // set, with an explicit undo stack: two undirected edges may share a node only after
        // reversing an earlier choice. Budget every attempt; no recursion or partial creation.
        var boundEdges = new List<(int Index, GraphRelationship Edge)>();
        for (int i = 0; i < path.Relationships.Count; i++)
        {
            var pattern = path.Relationships[i];
            if (!row.TryGetValue(pattern.Variable!, out var value)) { continue; }
            if (value is not GraphRelationship bound || operation.Updates.FindRelationship(bound.Id.Value) is not { } stored) { throw Invalid(); }
            var edge = GraphDatabase.Materialize(stored);
            if (!GraphLabelEvaluator.Accepts(pattern, edge.Type) ||
                !PropertiesMatch(pattern.Properties, edge.Properties, row, operation.EvaluationContext, parameters)) { throw Invalid(); }
            row[pattern.Variable!] = edge;
            boundEdges.Add((i, edge));
        }
        var choices = new int[boundEdges.Count];
        var addedNames = new List<string>?[boundEdges.Count];
        int depth = 0;
        while (depth < boundEdges.Count)
        {
            token.ThrowIfCancellationRequested();
            budget.Consume();
            var (index, edge) = boundEdges[depth];
            var pattern = path.Relationships[index];
            bool undirected = pattern.Direction is GqlPatternDirection.Undirected or GqlPatternDirection.LeftOrRight;
            if (choices[depth] == (undirected && edge.From != edge.To ? 2 : 1))
            {
                choices[depth] = 0;
                if (depth == 0) { throw Invalid(); }
                Undo(--depth);
                continue;
            }
            bool reverse = choices[depth]++ == 1 || pattern.Direction == GqlPatternDirection.Incoming;
            var added = addedNames[depth] = new List<string>(2);
            if (BindNode(index, reverse ? edge.To.Value : edge.From.Value, added) &&
                BindNode(index + 1, reverse ? edge.From.Value : edge.To.Value, added)) { depth++; }
            else { Undo(depth); }
        }
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
                var occurrences = path.Nodes.Where(node => node.Variable == pattern.Variable).ToArray();
                var labels = occurrences.SelectMany(GraphLabelEvaluator.InsertLabels).Distinct(StringComparer.Ordinal).ToArray();
                var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var occurrence in occurrences)
                {
                    foreach (var property in EvaluateProperties(occurrence.Properties, row, operation.EvaluationContext, parameters))
                    {
                        if (properties.TryGetValue(property.Key, out var previous) && !GraphValueComparer.Instance.Equals(previous, property.Value)) { throw Invalid(); }
                        properties[property.Key] = property.Value;
                    }
                }
                nodes[i] = await operation.Updates.CreateNodeAsync(labels, properties, token).ConfigureAwait(false);
                row.Add(pattern.Variable!, nodes[i]);
                created++;
            }
        }
        for (int i = 0; i < path.Relationships.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var pattern = path.Relationships[i];
            if (row.ContainsKey(pattern.Variable!)) { continue; }
            bool reverse = pattern.Direction == GqlPatternDirection.Incoming;
            var edge = await operation.Updates.CreateRelationshipAsync(nodes[reverse ? i + 1 : i].Id,
                nodes[reverse ? i : i + 1].Id, GraphLabelEvaluator.InsertType(pattern),
                EvaluateProperties(pattern.Properties, row, operation.EvaluationContext, parameters), token).ConfigureAwait(false);
            row.Add(pattern.Variable!, edge);
            created++;
        }
        return created;

        GraphPlanningException Invalid() => new("CG03005", ErrorMessages.MergeBoundInvalid);
        void Undo(int index)
        {
            foreach (string name in addedNames[index]!) { row.Remove(name); }
            addedNames[index] = null;
        }
        bool BindNode(int index, ulong id, List<string> added)
        {
            var pattern = path.Nodes[index];
            if (row.TryGetValue(pattern.Variable!, out var value) && (value is not GraphNode bound || bound.Id.Value != id) ||
                operation.Updates.FindNode(id) is not { } stored) { return false; }
            var node = GraphDatabase.Materialize(stored);
            foreach (var occurrence in path.Nodes)
            {
                if (occurrence.Variable != pattern.Variable) { continue; }
                token.ThrowIfCancellationRequested();
                budget.Consume();
                if (!GraphLabelEvaluator.Accepts(occurrence, node.Labels) ||
                    !PropertiesMatch(occurrence.Properties, node.Properties, row, operation.EvaluationContext, parameters)) { return false; }
            }
            if (!row.ContainsKey(pattern.Variable!)) { row.Add(pattern.Variable!, node); added.Add(pattern.Variable!); }
            return true;
        }
    }
}
