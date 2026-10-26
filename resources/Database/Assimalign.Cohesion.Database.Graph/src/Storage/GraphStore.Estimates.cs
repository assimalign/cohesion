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

    // Like label estimates, these retain historical and rolled-back publications until reopen.
    // They select an access path only; neither catalog nor record visibility depends on them.
    internal long EstimateRelationshipTypeCount(string type)
    {
        lock (_sync) { return _relationshipTypeCardinalities.GetValueOrDefault(type); }
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
