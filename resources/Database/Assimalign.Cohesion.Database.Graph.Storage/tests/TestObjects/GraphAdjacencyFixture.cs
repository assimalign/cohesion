using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests.TestObjects;

internal sealed class GraphAdjacencyFixture : IAsyncDisposable
{
    internal GraphStorage Storage { get; } = GraphStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "adjacency-race");
    internal TransactionCoordinator Coordinator { get; }
    internal GraphStore Store { get; }
    internal Guid Type { get; } = Guid.NewGuid();

    internal GraphAdjacencyFixture()
    {
        Coordinator = new TransactionCoordinator(Storage, Storage.WriteAheadJournal, Storage.Records);
        Store = GraphStore.Open(Storage, Coordinator);
    }

    internal ValueTask<StoredGraphNode> NodeAsync(TransactionContext context, CancellationToken token)
        => Store.CreateNodeAsync(new StoredGraphNode((ulong)Storage.ReserveTransactionSequence(), [], new Dictionary<string, object?>()), context, token);

    internal ValueTask<StoredGraphRelationship> EdgeAsync(ulong from, ulong to, int value, TransactionContext context, CancellationToken token)
        => Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)Storage.ReserveTransactionSequence(), from, to, "T",
            new Dictionary<string, object?> { ["x"] = value }), Type, context, token);

    internal async Task<List<StoredGraphRelationship>> ReadAsync(ulong node, TransactionSnapshot snapshot, CancellationToken token)
    {
        var result = new List<StoredGraphRelationship>();
        await foreach (var edge in Store.GetIncidentAsync(node, GraphIncidentDirection.Both, [Type], snapshot, token)) { result.Add(edge); }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync();
        Storage.Dispose();
    }
}
