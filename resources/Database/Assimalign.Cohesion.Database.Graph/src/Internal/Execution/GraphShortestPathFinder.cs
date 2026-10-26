using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Graph.Storage;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphShortestPathFinder
{
    internal static async ValueTask<(int? Distance, GraphPath? Path)> SearchAsync(GraphDatabase database,
        GraphOperation operation, GraphShortestPathSearch search, GraphPlanExecutor.ExpansionBudget budget,
        bool includePath, CancellationToken token)
    {
        CheckActive();
        using var memory = new GraphMemoryOwner(budget.Memory);
        memory.Add(9216); // Search containers and one bounded adjacency cursor/key batch.
        var snapshot = operation.Context.Snapshot;
        budget.Consume();
        CheckActive();
        bool startVisible = database.Store.FindNode(search.Start.Value, snapshot) is not null;
        CheckActive();
        if (!startVisible) { return (null, null); }
        if (search.Start == search.Target) { return Complete(0, null); }
        budget.Consume();
        CheckActive();
        bool targetVisible = database.Store.FindNode(search.Target.Value, snapshot) is not null;
        CheckActive();
        if (!targetVisible) { return (null, null); }

        IReadOnlyCollection<Guid>? types = search.RelationshipType is { } name
            ? database.Catalog.FindRelationshipType(name, snapshot) is { } type ? new[] { type.Id } : [] : null;
        CheckActive();
        if (types is { Count: 0 }) { return (null, null); }
        var direction = search.Direction switch
        {
            GraphDirection.Outgoing => GraphIncidentDirection.Outgoing,
            GraphDirection.Incoming => GraphIncidentDirection.Incoming,
            _ => GraphIncidentDirection.Both,
        };
        // Scan ranges sequentially, then order unique new neighbors by their smallest edge ID.
        // This avoids one merge cursor per type (unbounded in the number of parallel types).
        // FIFO parents and ordered neighbors visit paths lexicographically at each distance.
        var visited = new Dictionary<ulong, (ulong Parent, ulong Edge, int Distance)>();
        var frontier = new Queue<ulong>();
        var neighbors = new List<ulong>();
        var ordered = new PriorityQueue<ulong, ulong>();
        memory.Add(160); // Conservative dictionary growth/entry and queue capacity per identity.
        visited.Add(search.Start.Value, default);
        frontier.Enqueue(search.Start.Value);
        while (frontier.TryDequeue(out ulong current))
        {
            CheckActive();
            budget.OnFrontier?.Invoke(frontier.Count + 1);
            CheckActive();
            int distance = visited[current].Distance + 1;
            neighbors.Clear();
            // Typed searches start their own read statement. Earlier own writes are already in
            // the store under this snapshot; there are no unpublished statement-overlay writes.
            await foreach (var edge in database.Store.StreamIncidentRangeAsync(current, direction, types,
                snapshot, Reserve, token).ConfigureAwait(false))
            {
                CheckActive();
                using var edgeMemory = new GraphMemoryOwner(budget.Memory);
                edgeMemory.RetainValue(GraphDatabase.Materialize(edge), token);
                ulong next = edge.SourceId == current ? edge.TargetId : edge.SourceId;
                if (visited.TryGetValue(next, out var previous))
                {
                    if (previous.Parent == current && edge.Id < previous.Edge)
                    { visited[next] = (current, edge.Id, distance); }
                    continue;
                }
                memory.Add(160); // Reserve before growing either retained collection.
                visited.Add(next, (current, edge.Id, distance));
                bool nodeVisible = database.Store.FindNode(next, snapshot) is not null;
                CheckActive();
                if (!nodeVisible) { continue; }
                budget.ConsumeViable();
                neighbors.Add(next);
            }
            CheckActive();
            if (visited.ContainsKey(search.Target.Value)) { return Complete(distance, visited); }
            foreach (ulong next in neighbors)
            {
                CheckActive();
                ordered.Enqueue(next, visited[next].Edge);
            }
            while (ordered.TryDequeue(out ulong next, out _))
            {
                CheckActive();
                frontier.Enqueue(next);
            }
        }
        return (null, null);

        void CheckActive()
        {
            token.ThrowIfCancellationRequested();
            operation.EnsureActive();
        }

        void Reserve(ulong edgeId)
        {
            budget.VisitCandidate();
            budget.Consume();
            CheckActive();
        }

        (int?, GraphPath?) Complete(int distance, Dictionary<ulong, (ulong Parent, ulong Edge, int Distance)>? predecessors)
        {
            CheckActive();
            if (!includePath) { return (distance, null); }
            // Arrays, wrappers and returned entity payloads share the statement grant.
            memory.Add(192 + (distance + 1L) * 64 + distance * 80L);
            var nodes = new GraphNode[distance + 1];
            var relationships = new GraphRelationship[distance];
            ulong nodeId = search.Target.Value;
            for (int i = distance; i >= 0; i--)
            {
                CheckActive();
                budget.Consume();
                CheckActive();
                var storedNode = database.Store.FindNode(nodeId, snapshot);
                // Teardown can roll back own writes between the state check and the read.
                // Prefer its lifecycle refusal; an active operation still needs a coded failure.
                CheckActive();
                if (storedNode is not { } foundNode)
                { throw DatabaseException.CreateGraphFailure("CG05022", ErrorMessages.ShortestPathNodeMissing); }
                var node = GraphDatabase.Materialize(foundNode);
                memory.RetainValue(node, token);
                nodes[i] = node;
                if (i == 0) { break; }
                var predecessor = predecessors![nodeId];
                budget.Consume();
                CheckActive();
                var storedRelationship = database.Store.FindRelationship(predecessor.Edge, snapshot);
                CheckActive();
                if (storedRelationship is not { } foundRelationship)
                { throw DatabaseException.CreateGraphFailure("CG05022", ErrorMessages.ShortestPathRelationshipMissing); }
                var relationship = GraphDatabase.Materialize(foundRelationship);
                memory.RetainValue(relationship, token);
                relationships[i - 1] = relationship;
                nodeId = predecessor.Parent;
            }
            return (distance, new GraphPath(Array.AsReadOnly(nodes), Array.AsReadOnly(relationships)));
        }
    }
}
