using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage;

public sealed partial class GraphStore
{
    // Kind-3 definitions already have a persisted revision word. v4 catalog admission fences
    // its text discriminator (1); entity record layout and version remain unchanged.
    private static IEnumerable<IndexKey> PropertyKeys(GraphRecord definition, object? value, ulong node,
        CancellationToken token, Action<long>? reserve = null, Action? visit = null)
    {
        if (definition.Revision == 0) { yield return Composite(GraphRecordCodec.Key(value), node); yield break; }
        if (value is not string text) { yield break; }
        foreach (var key in GraphTextTrigrams.Keys(text, token, reserve, visit)) { yield return Composite(key, node); }
    }

    private static (ulong Id, IndexKey Key)[] TextChanges(GraphRecord[] definitions, StoredGraphNode node,
        CancellationToken token, Action<long>? reserve, Action? visit)
        => definitions.Where(index => node.Labels.Contains(index.Label!, StringComparer.Ordinal)
            && node.Properties.ContainsKey(index.PropertyKey!))
            .SelectMany(index => PropertyKeys(index, node.Properties[index.PropertyKey!], node.Id, token, reserve, visit)
                .Select(key => (index.Id, Key: key))).ToArray();

    internal Action<string>? AfterTextIndexMutation { get; set; }

    internal long IndexOwnerPagesReclaimed { get; private set; }

    internal const int IndexReclaimPageLimit = 64;
    private readonly Queue<ulong> _retirementQueue = new();
    private readonly HashSet<ulong> _retirementCandidates = new();

    private void CollectAbandonedIndexOwners()
    {
        // Open only, before admitting operations. Allocation survives bracket rollback, while
        // the registration may not. Recover only owners with no definition at all; fixed owners
        // and trees with a retained definition must follow their ordinary recovery contracts.
        for (long page = 1; page < _storage.PageManager.PageCount; page++)
        {
            if (!_storage.FreeSpaceMap.IsAllocated((PageId)page)) { continue; }
            using var handle = _storage.PageManager.GetPage((PageId)page);
            ulong id = handle.Page.OwnerId;
            if (handle.Page.Type != PageType.Index ||
                id <= 2 || id == adjacencyId || id == labelsId || id == relationshipTypesId ||
                _registrations.ContainsKey(id) || _records.ContainsKey((3, id))) { continue; }
            var bytes = new byte[34];
            bytes[16] = 4; bytes[17] = GraphRecordCodec.FormatVersion;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(18), id);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(26), -page);
            using var bracket = _storage.BeginTransaction();
            var location = _storage.InsertOwned(bracket, 1, bytes);
            bracket.Commit();
            _registrations.Add(id, (location.PageId, location.SlotIndex, -page));
            ScheduleIndexReclamation(id); // Reclamation still requires id below the safe bound.
        }
    }

    private void ScheduleIndexReclamation(ulong id)
    {
        if (id <= 2 || id == adjacencyId || id == labelsId || id == relationshipTypesId) { return; }
        lock (_sync) { if (_retirementCandidates.Add(id)) { _retirementQueue.Enqueue(id); } }
    }

    private void ReclaimRetiredIndexes(TransactionSequence bound, CancellationToken token)
    {
        BeforeIndexReclamation?.Invoke();
        lock (_indexRegistrySync)
        {
            int checks;
            lock (_sync) { checks = Math.Min(IndexReclaimPageLimit, _retirementQueue.Count); }
            int remaining = IndexReclaimPageLimit;
            bool rebuild = false;
            try
            {
                for (int step = 0; step < checks && remaining > 0; step++)
                {
                    token.ThrowIfCancellationRequested();
                    ulong id;
                    lock (_sync) { id = _retirementQueue.Dequeue(); }
                    bool finished = false;
                    try
                    {
                        if (!_registrations.TryGetValue(id, out var registration)) { finished = true; continue; }
                        if (registration.Root > 0)
                        {
                            int status = RetirementStatus(id, bound);
                            if (status == 0) { finished = true; continue; } // A live definition (or rolled-back DROP).
                            if (status == 1) { continue; } // A pinned creator/deleter.
                            // A negative root is a durable retirement receipt. Detach before freeing
                            // the root so an open never attaches a partly reclaimed tree.
                            byte[] bytes = _storage.ReadEntry(registration.Page, registration.Slot).ToArray();
                            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(26), -registration.Root);
                            using (var bracket = _storage.BeginTransaction())
                            {
                                _storage.UpdateEntry(bracket, registration.Page, registration.Slot, bytes);
                                bracket.Commit();
                            }
                            _registrations[id] = (registration.Page, registration.Slot, -registration.Root);
                            rebuild = true;
                            AfterTextIndexMutation?.Invoke("retire-registration");
                        }
                        if (id >= bound.Value) { continue; }
                        int freed;
                        using (var bracket = _storage.BeginTransaction())
                        {
                            freed = _storage.FreeOwnerPages(bracket, id, remaining);
                            AfterTextIndexMutation?.Invoke("reclaim-pages");
                            bracket.Commit();
                        }
                        IndexOwnerPagesReclaimed += freed;
                        remaining -= freed;
                        if (freed != 0) { AfterTextIndexMutation?.Invoke("reclaim-batch"); }
                        // Directory removal is published at commit. A short batch is not proof of completion.
                        if (_storage.GetOwnerIndexPages(id).Count == 0)
                        {
                            using (var bracket = _storage.BeginTransaction())
                            {
                                _storage.DeleteEntry(bracket, registration.Page, registration.Slot);
                                AfterTextIndexMutation?.Invoke("reclaim-registration");
                                bracket.Commit();
                            }
                            _registrations.Remove(id);
                            finished = true;
                            AfterTextIndexMutation?.Invoke("reclaimed");
                        }
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            if (finished) { _retirementCandidates.Remove(id); }
                            else { _retirementQueue.Enqueue(id); }
                        }
                    }
                }
            }
            finally { if (rebuild) { OpenIndexes(scheduleReclamation: false); } }
        }
    }

    // 0 live, 1 waiting for the safe prune bound, 2 reclaimable. Definition records
    // are immutable, but account for every retained reference rather than assuming one.
    private int RetirementStatus(ulong id, TransactionSequence bound)
    {
        lock (_sync)
        {
            // Tree ids are reserved after their creator sequence. A failed undo can restore
            // the directory before its physical delete commits; the active creator still pins
            // the bound, so an orphan must pass its own conservative sequence floor too.
            if (!_records.TryGetValue((3, id), out var versions)) { return id < bound.Value ? 2 : 1; }
            int status = Status(versions.Head);
            for (var older = versions.Older; older is not null; older = older.Next)
            { status = Math.Min(status, Status(older.Reference)); }
            return status;
        }
        int Status(Reference reference)
        {
            if (!TryRead(reference, 3, id, out var bytes)) { return 2; }
            var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes.Span);
            if (writer >= bound || deleter >= bound) { return 1; }
            return deleter == TransactionSequence.None ? 0 : 2;
        }
    }

    internal bool HasTextIndexes
    {
        get
        {
            // Conservative and allocation-free: dead definitions may enable accounting, but
            // never disable it for a live text tree. No node or catalog read is needed.
            lock (_sync)
            {
                foreach (ulong id in _definitionIds)
                { if (_records.TryGetValue((3, id), out var versions) && versions.Head.Revision == 1) { return true; } }
                return false;
            }
        }
    }

    internal async IAsyncEnumerable<StoredGraphNode> StreamTextIndexAsync(string label, string property, object? value,
        string operation, TransactionSnapshot snapshot, [EnumeratorCancellation] CancellationToken token,
        Action visit, Action<long> reserve)
    {
        if (!GraphTextTrigrams.CanSeek(value) || Definition(label, property, snapshot, true) is not { } definition)
        {
            foreach (var node in StreamLabels([label], snapshot, token, visit, reserve))
            {
                if (node.Properties.TryGetValue(property, out var actual) && GraphTextTrigrams.Matches(actual, value, operation))
                { yield return node; }
            }
            yield break;
        }
        var anchor = GraphTextTrigrams.Keys((string)value!, token).First();
        reserve(8192); // One bounded cursor batch, matching a visible label posting.
        var index = ResolveIndex(definition.Id);
        await using var cursor = index.OpenCursor(snapshot,
            new IndexKeyRange(Composite(anchor, 0), Composite(anchor, ulong.MaxValue), true, true), cancellationToken: token);
        while (await cursor.MoveNextAsync(token).ConfigureAwait(false))
        {
            visit();
            if (ReadAt(cursor.CurrentEntryReference, snapshot)?.Record.Node is not { } node) { continue; }
            // The first trigram never proves equality, position, multiplicity or order.
            // Always recheck the snapshot-visible stored value, including the label and identity.
            if (node.Labels.Contains(label, StringComparer.Ordinal) &&
                cursor.CurrentKey.Equals(Composite(anchor, node.Id)) && node.Properties.TryGetValue(property, out var actual) &&
                GraphTextTrigrams.Matches(actual, value, operation)) { yield return node; }
        }
    }
}
