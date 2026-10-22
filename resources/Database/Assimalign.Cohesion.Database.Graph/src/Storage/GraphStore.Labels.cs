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
    private const ulong labelsId = ulong.MaxValue - 1;
    private readonly Dictionary<string, HashSet<Guid>> _labelTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _labelCardinalities = new(StringComparer.Ordinal);
    private long _nodeRecordsDecoded;
    private long _labelEntriesVisited;
    internal long NodeRecordsDecoded => Interlocked.Read(ref _nodeRecordsDecoded);
    internal long LabelEntriesVisited => Interlocked.Read(ref _labelEntriesVisited);
    internal Action? OnLabelEntryRead { get; set; }
    internal Action? OnLabelIndexChanged { get; set; }

    // An advisory estimate of retained postings, including historical and rolled-back versions.
    // It affects plan cost only. Snapshot visibility and the full predicate remain authoritative.
    internal long EstimateLabelCount(string label)
    {
        lock (_sync) { return _labelCardinalities.GetValueOrDefault(label); }
    }

    private static Guid[] ResolveLabelIds(IReadOnlyList<string> labels, IReadOnlyDictionary<string, Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var result = new Guid[labels.Count];
        for (int i = 0; i < labels.Count; i++)
        {
            if (!ids.TryGetValue(labels[i], out result[i]) || result[i] == Guid.Empty)
            { throw new ArgumentException(ErrorMessages.LabelIdInvalid, nameof(ids)); }
        }
        return result;
    }

    // Called under directory _sync, after publication or while reconstructing on open.
    private void RememberLabels(GraphRecord record)
    {
        if (record.Node is not { } node) { return; }
        for (int i = 0; i < node.Labels.Count; i++)
        {
            string label = node.Labels[i];
            if (!_labelTokens.TryGetValue(label, out var tokens)) { _labelTokens.Add(label, tokens = []); }
            tokens.Add(record.LabelIds![i]);
            _labelCardinalities[label] = _labelCardinalities.GetValueOrDefault(label) + 1;
        }
    }

    private GraphRecord DecodeRead(ReadOnlyMemory<byte> bytes, PageId page, int slot)
    {
        var record = GraphRecordCodec.Decode(bytes, page, slot);
        if (record.Kind == 1) { Interlocked.Increment(ref _nodeRecordsDecoded); }
        return record;
    }

    private async ValueTask EnsureLabelsAsync(TransactionContext context, CancellationToken token)
    {
        if (!_indexes.TryGetIndex(labelsId, treeName, out _))
        { await _indexes.CreateIndexAsync(context, labelsId, new IndexDefinition(treeName), token).ConfigureAwait(false); }
    }

    private static IndexKey LabelKey(Guid label, ulong node)
    {
        var bytes = new byte[24];
        label.TryWriteBytes(bytes.AsSpan(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(16), node);
        return new IndexKey(bytes);
    }

    private async ValueTask InsertLabelsAsync(GraphRecord record, ulong location, TransactionContext context, CancellationToken token)
    {
        foreach (Guid label in record.LabelIds!)
        {
            await InsertIndexAsync(labelsId, LabelKey(label, record.Id), location, context, token).ConfigureAwait(false);
            OnLabelIndexChanged?.Invoke();
            token.ThrowIfCancellationRequested();
        }
    }

    private async ValueTask DeleteLabelsAsync(GraphRecord record, ulong location, TransactionContext context, CancellationToken token)
    {
        foreach (Guid label in record.LabelIds!)
        {
            await DeleteIndexEntryAsync(labelsId, LabelKey(label, record.Id), location, context, token).ConfigureAwait(false);
            OnLabelIndexChanged?.Invoke();
            token.ThrowIfCancellationRequested();
        }
    }

    private IReadOnlyList<StoredGraphNode> ScanLabel(string label, TransactionSnapshot snapshot)
        => ScanLabels([label], snapshot, CancellationToken.None);

    // Sorted union of label ranges, decoding a visible identity once even when it has several
    // selected labels. Conjunctions instead anchor on the cheapest required label and retain
    // the complete expression as a residual, so a common label adds no range work.
    internal IReadOnlyList<StoredGraphNode> ScanLabels(IReadOnlyList<string> labels, TransactionSnapshot snapshot, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        token.ThrowIfCancellationRequested();
        Guid[] tokens;
        lock (_sync)
        {
            tokens = labels.SelectMany(label => _labelTokens.TryGetValue(label, out var found) ? found : [])
                .Distinct().ToArray();
        }
        // Lock order is established in EnterMutationAsync. Release directory _sync before any
        // tree latch; readers acquire only tree -> page latches, never the writer/apply gates.
        if (tokens.Length == 0) { return []; }
        var index = ResolveIndex(labelsId);
        var cursors = new List<BTreeCursor>(tokens.Length);
        var pending = new PriorityQueue<(BTreeCursor Cursor, Guid Label), ulong>();
        var result = new List<StoredGraphNode>();
        ulong? returned = null;
        try
        {
            foreach (Guid label in tokens)
            {
                token.ThrowIfCancellationRequested();
                var cursor = index.OpenCursor(snapshot, new IndexKeyRange(LabelKey(label, 0), LabelKey(label, ulong.MaxValue), true, true), cancellationToken: token);
                cursors.Add(cursor);
                Advance(cursor, label);
            }
            while (pending.TryDequeue(out var entry, out ulong id))
            {
                token.ThrowIfCancellationRequested();
                ulong location = entry.Cursor.CurrentEntryReference;
                Advance(entry.Cursor, entry.Label);
                OnLabelEntryRead?.Invoke();
                token.ThrowIfCancellationRequested();
                if (returned == id) { continue; }
                var found = ReadAt(location, snapshot);
                if (found?.Record.Node is not { } node || node.Id != id ||
                    !found.Value.Record.LabelIds!.Contains(entry.Label) ||
                    !node.Labels.Any(label => labels.Contains(label, StringComparer.Ordinal))) { continue; }
                result.Add(node);
                returned = id;
            }
            return result;
        }
        finally
        {
            foreach (var cursor in cursors) { cursor.DisposeAsync().GetAwaiter().GetResult(); }
        }

        void Advance(BTreeCursor cursor, Guid label)
        {
            // BTreeCursor moves complete synchronously and copy bounded batches. No page or latch
            // survives a move; disposal releases the owned keys on every exit.
            if (!cursor.MoveNextAsync(token).GetAwaiter().GetResult()) { return; }
            var key = cursor.CurrentKey.Encoded.Span;
            if (key.Length != 24 || new Guid(key.Slice(0, 16), bigEndian: true) != label)
            { throw new StorageCorruptionException(ErrorMessages.LabelPostingInvalid); }
            Interlocked.Increment(ref _labelEntriesVisited);
            pending.Enqueue((cursor, label), BinaryPrimitives.ReadUInt64BigEndian(key.Slice(16)));
        }
    }
}
