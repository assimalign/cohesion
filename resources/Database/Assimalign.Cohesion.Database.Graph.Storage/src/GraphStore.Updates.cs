using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Storage.Internal;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage;

public sealed partial class GraphStore
{
    /// <summary>Replaces a node's labels and scalar properties with a new MVCC version.</summary>
    /// <param name="id">The existing node identity.</param>
    /// <param name="labels">The complete resulting label set.</param>
    /// <param name="properties">The complete resulting property map.</param>
    /// <param name="context">The owning transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new version, the unchanged node for an identical replacement, or null if absent.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A name or scalar value is invalid.</exception>
    /// <exception cref="InvalidOperationException">The transaction is not active.</exception>
    /// <exception cref="TransactionAbortedException">The node changed since the snapshot.</exception>
    /// <exception cref="GraphElementTooLargeException">The record or an index key is too large.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async ValueTask<StoredGraphNode?> UpdateNodeAsync(ulong id, IReadOnlyList<string> labels,
        IReadOnlyDictionary<string, object?> properties, TransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(properties);
        foreach (string label in labels) { ArgumentException.ThrowIfNullOrWhiteSpace(label); }
        await LockAsync(context, cancellationToken).ConfigureAwait(false);
        EnsureUnchanged(1, id, context);
        if (Find(1, id, context.Snapshot) is not { } prior) { return null; }
        var record = new GraphRecord(1, id, new StoredGraphNode(id,
            labels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), properties), Revision: checked(prior.Record.Revision + 1));
        byte[] bytes = GraphRecordCodec.Encode(record, context.Sequence);
        record = GraphRecordCodec.Decode(bytes);
        var node = record.Node!.Value;
        var old = prior.Record.Node!.Value;
        if (old.Labels.SequenceEqual(node.Labels) && PropertiesIdentical(old.Properties, node.Properties)) { return old; }
        var definitions = Definitions(Latest(context));
        // Validate every new key before tombstoning either the record or an index entry.
        var changes = definitions.Where(index => node.Labels.Contains(index.Label!, StringComparer.Ordinal)
            && node.Properties.ContainsKey(index.PropertyKey!))
            .Select(index => (index.Id, Key: Composite(GraphRecordCodec.Key(node.Properties[index.PropertyKey!]), id))).ToArray();
        var inserted = await ApplyAsync(context, async bracket =>
        {
            var location = Insert(bracket, bytes, context);
            foreach (var definition in definitions)
            {
                if (old.Labels.Contains(definition.Label!, StringComparer.Ordinal) && old.Properties.TryGetValue(definition.PropertyKey!, out var value))
                { await DeleteIndexEntryAsync(definition.Id, Composite(GraphRecordCodec.Key(value), id), prior.Reference.Location, context, cancellationToken).ConfigureAwait(false); }
            }
            foreach (var change in changes)
            { await InsertIndexAsync(change.Id, change.Key, location, context, cancellationToken).ConfigureAwait(false); }
            Tombstone(bracket, prior.Reference, context);
            SaveRegistrations(bracket);
            return location;
        }, cancellationToken).ConfigureAwait(false);
        Add(record, inserted, context.Sequence);
        return node;
    }

    /// <summary>Replaces a relationship's properties, preserving its identity, endpoints and type.</summary>
    /// <param name="id">The existing relationship identity.</param>
    /// <param name="properties">The complete resulting scalar property map.</param>
    /// <param name="context">The owning transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new or identical version, or null if absent.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A property name or scalar value is invalid.</exception>
    /// <exception cref="InvalidOperationException">The transaction is not active.</exception>
    /// <exception cref="TransactionAbortedException">The relationship changed since the snapshot.</exception>
    /// <exception cref="GraphElementTooLargeException">The record is too large.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async ValueTask<StoredGraphRelationship?> UpdateRelationshipAsync(ulong id,
        IReadOnlyDictionary<string, object?> properties, TransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(properties);
        await LockAsync(context, cancellationToken).ConfigureAwait(false);
        EnsureUnchanged(2, id, context);
        if (Find(2, id, context.Snapshot) is not { } prior) { return null; }
        var old = prior.Record.Relationship!.Value;
        var record = new GraphRecord(2, id, Relationship: new StoredGraphRelationship(id, old.SourceId, old.TargetId, old.Type, properties),
            Revision: checked(prior.Record.Revision + 1));
        byte[] bytes = GraphRecordCodec.Encode(record, context.Sequence);
        record = GraphRecordCodec.Decode(bytes);
        if (PropertiesIdentical(old.Properties, record.Relationship!.Value.Properties)) { return old; }
        var inserted = await ApplyAsync(context, async bracket =>
        {
            var location = Insert(bracket, bytes, context);
            await RemoveRelationshipAsync(prior, bracket, context, cancellationToken).ConfigureAwait(false);
            await InsertIndexAsync(adjacencyId, Composite(IndexKey.FromUInt64(old.SourceId), id), location, context, cancellationToken).ConfigureAwait(false);
            if (old.TargetId != old.SourceId)
            { await InsertIndexAsync(adjacencyId, Composite(IndexKey.FromUInt64(old.TargetId), id), location, context, cancellationToken).ConfigureAwait(false); }
            SaveRegistrations(bracket);
            return location;
        }, cancellationToken).ConfigureAwait(false);
        Add(record, inserted, context.Sequence);
        return record.Relationship;
    }

    private static bool PropertiesIdentical(IReadOnlyDictionary<string, object?> left, IReadOnlyDictionary<string, object?> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && Equals(pair.Value, value));
}
