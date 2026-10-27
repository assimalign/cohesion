using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage;

public sealed partial class GraphStore
{
    private const ulong relationshipTypesId = ulong.MaxValue - 2;
    private readonly Dictionary<string, HashSet<Guid>> _relationshipTypeTokens = new(StringComparer.Ordinal);
    private long _relationshipTypeEntriesVisited;
    internal long RelationshipTypeEntriesVisited => Interlocked.Read(ref _relationshipTypeEntriesVisited);
    internal Action? OnRelationshipTypeEntryRead { get; set; }
    internal Action? OnRelationshipTypeIndexChanged { get; set; }

    // Retain every catalog identity seen for a name, including historical versions. The
    // directory selects ranges only; posting and record visibility decide the result.
    private void RememberRelationshipType(GraphRecord record)
    {
        if (record.Relationship is not { } relationship) { return; }
        if (!_relationshipTypeTokens.TryGetValue(relationship.Type, out var tokens))
        { _relationshipTypeTokens.Add(relationship.Type, tokens = []); }
        tokens.Add(record.TypeId);
        _relationshipTypeCardinalities[relationship.Type] = _relationshipTypeCardinalities.GetValueOrDefault(relationship.Type) + 1;
    }

    private async ValueTask EnsureRelationshipTypesAsync(TransactionContext context, CancellationToken token)
    {
        if (!_indexes.TryGetIndex(relationshipTypesId, treeName, out _))
        { await _indexes.CreateIndexAsync(context, relationshipTypesId, new IndexDefinition(treeName), token).ConfigureAwait(false); }
    }

    private static IndexKey RelationshipTypeKey(Guid type, ulong relationship)
    {
        var bytes = new byte[24];
        type.TryWriteBytes(bytes.AsSpan(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(16), relationship);
        return new IndexKey(bytes);
    }

    private async ValueTask InsertRelationshipTypeAsync(GraphRecord record, ulong location, TransactionContext context, CancellationToken token)
    {
        await InsertIndexAsync(relationshipTypesId, RelationshipTypeKey(record.TypeId, record.Id), location, context, token).ConfigureAwait(false);
        OnRelationshipTypeIndexChanged?.Invoke();
        token.ThrowIfCancellationRequested();
    }

    private async ValueTask DeleteRelationshipTypeAsync(GraphRecord record, ulong location, TransactionContext context, CancellationToken token)
    {
        await DeleteIndexEntryAsync(relationshipTypesId, RelationshipTypeKey(record.TypeId, record.Id), location, context, token).ConfigureAwait(false);
        OnRelationshipTypeIndexChanged?.Invoke();
        token.ThrowIfCancellationRequested();
    }

    internal IReadOnlyList<StoredGraphRelationship> ScanRelationshipTypes(IReadOnlyList<string> types, TransactionSnapshot snapshot, CancellationToken token)
        => StreamRelationshipTypes(types, snapshot, token).ToArray();

    // Merge selected name/token ranges in identity order. A self-loop has exactly one type
    // posting; historical versions and recreated names cannot duplicate a visible identity.
    internal IEnumerable<StoredGraphRelationship> StreamRelationshipTypes(IReadOnlyList<string> types, TransactionSnapshot snapshot,
        CancellationToken token, Action? visit = null, Action<long>? reserve = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        token.ThrowIfCancellationRequested();
        Guid[] tokens;
        lock (_sync)
        {
            tokens = types.SelectMany(type => _relationshipTypeTokens.TryGetValue(type, out var found) ? found : [])
                .Distinct().ToArray();
        }
        // Lock order is established in EnterMutationAsync. Release directory _sync before any
        // tree latch; readers acquire only tree -> page latches, never the writer/apply gates.
        if (tokens.Length == 0) { yield break; }
        var index = ResolveIndex(relationshipTypesId);
        var cursors = new List<BTreeCursor>(tokens.Length);
        var pending = new PriorityQueue<(BTreeCursor Cursor, Guid Type), ulong>();
        ulong? returned = null;
        try
        {
            foreach (Guid type in tokens)
            {
                token.ThrowIfCancellationRequested();
                // 64 owned 24-byte keys plus cursor, heap heads and entry-array overhead.
                reserve?.Invoke(8192);
                var cursor = index.OpenCursor(snapshot, new IndexKeyRange(RelationshipTypeKey(type, 0), RelationshipTypeKey(type, ulong.MaxValue), true, true), cancellationToken: token);
                cursors.Add(cursor);
                Advance(cursor, type);
            }
            while (pending.TryDequeue(out var entry, out ulong id))
            {
                token.ThrowIfCancellationRequested();
                ulong location = entry.Cursor.CurrentEntryReference;
                if (returned != id)
                {
                    var found = ReadAt(location, snapshot);
                    if (found?.Record.Relationship is { } relationship && relationship.Id == id &&
                        found.Value.Record.TypeId == entry.Type &&
                        types.Contains(relationship.Type, StringComparer.Ordinal))
                    {
                        returned = id;
                        yield return relationship;
                    }
                }
                // Resume only when the consumer asks for another relationship. LIMIT and
                // either undirected orientation can stop without reading the next head.
                Advance(entry.Cursor, entry.Type);
            }
        }
        finally
        {
            foreach (var cursor in cursors) { cursor.DisposeAsync().GetAwaiter().GetResult(); }
        }

        void Advance(BTreeCursor cursor, Guid type)
        {
            // BTreeCursor moves complete synchronously and copy bounded batches. No page or latch
            // survives a move; disposal releases the owned keys on every exit.
            if (!cursor.MoveNextAsync(token).GetAwaiter().GetResult()) { return; }
            // A cursor stages a head (possibly from its bounded owned batch). Admit the
            // posting before inspecting that head, including every range's initial head.
            visit?.Invoke();
            var key = cursor.CurrentKey.Encoded.Span;
            if (key.Length != 24 || new Guid(key.Slice(0, 16), bigEndian: true) != type)
            { throw new StorageCorruptionException(ErrorMessages.RelationshipTypePostingInvalid); }
            Interlocked.Increment(ref _relationshipTypeEntriesVisited);
            OnRelationshipTypeEntryRead?.Invoke();
            token.ThrowIfCancellationRequested();
            pending.Enqueue((cursor, type), BinaryPrimitives.ReadUInt64BigEndian(key.Slice(16)));
        }
    }
}
