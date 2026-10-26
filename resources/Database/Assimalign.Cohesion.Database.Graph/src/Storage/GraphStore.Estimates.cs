using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage;

public sealed partial class GraphStore
{
    private readonly Dictionary<string, long> _relationshipTypeCardinalities = new(StringComparer.Ordinal);
    internal Action? OnIndexDefinitionRead { get; set; }

    // Publication updates and maintenance reconciliation keep this a cached retained population.
    // Planning never reads records; neither catalog nor record visibility depends on the count.
    internal long EstimateRelationshipTypeCount(string type)
    {
        lock (_sync) { return _relationshipTypeCardinalities.GetValueOrDefault(type); }
    }

    private void RefreshRelationshipTypeCounts(CancellationToken token)
    {
        // Called during the coordinator's maintenance apply gate, after record reclamation.
        // Validate references by writer/kind/identity: a freed slot may already be reused.
        // Only relationship headers are read; properties and nodes are never decoded here.
        lock (_sync)
        {
            var counts = new Dictionary<string, long>(_relationshipTypeCardinalities.Count, StringComparer.Ordinal);
            foreach (var (key, versions) in _records)
            {
                token.ThrowIfCancellationRequested();
                if (key.Kind != 2 || versions.RelationshipType is not { } type) { continue; }
                long count = TryRead(versions.Head, key.Kind, key.Id, out _) ? 1 : 0;
                for (var older = versions.Older; older is not null; older = older.Next)
                {
                    token.ThrowIfCancellationRequested();
                    if (TryRead(older.Reference, key.Kind, key.Id, out _)) { count++; }
                }
                if (count != 0) { counts[type] = counts.GetValueOrDefault(type) + count; }
            }
            // Publish a complete refresh only: cancellation or a damaged record leaves
            // the previous advisory counts intact for the next maintenance attempt.
            _relationshipTypeCardinalities.Clear();
            foreach (var (type, count) in counts) { _relationshipTypeCardinalities.Add(type, count); }
        }
    }

    internal IReadOnlyList<(string Label, string Property, ulong Id)> GetIndexIdentities(TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Definitions(snapshot).Select(definition => (definition.Label!, definition.PropertyKey!, definition.Id)).ToArray();
    }

    internal long EstimateIndexCount(ulong indexId, string label, object value, TransactionSnapshot snapshot, CancellationToken token)
    {
        if (!GraphRecordCodec.TryKey(value, out var key)) { return 0; }
        var count = ResolveIndex(indexId).CountRange(snapshot,
            new IndexKeyRange(Composite(key, 0), Composite(key, ulong.MaxValue), true, true),
            entryLimit: 512, pageLimit: 32, cancellationToken: token);
        // An incomplete prefix must not masquerade as a selective exact count. The retained
        // label population is a conservative fallback, including numeric projection collisions.
        return count.IsComplete ? count.Count : Math.Max(count.Count + 1, EstimateLabelCount(label));
    }
}
