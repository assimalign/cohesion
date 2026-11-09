using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Database.Indexing.Internal;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

public sealed partial class DocumentCatalog
{
    // Apply gate -> registry -> catalog monitor, as in Graph. Failure rebuilds only
    // take registry/catalog monitors and never wait for apply admission under them.
    private readonly object _indexRegistrySync = new();
    private readonly Dictionary<ulong, Versions> _indexOwners = new();
    private readonly Queue<ulong> _retiringIndexes = new();
    private readonly HashSet<ulong> _retiringIndexIds = new();
    internal Func<bool>? ReclamationCheckpointDue { get; set; }
    internal Action<string>? AfterIndexRetirementMutation { get; set; }
    internal long IndexOwnerPagesReclaimed { get; private set; }

    private void CollectAbandonedIndexOwners()
    {
        bool detached = false;
        foreach (var pair in _registrations.ToArray())
        {
            if (pair.Value.Root < 0 || pair.Key >= _coordinator.PruneBound.Value || _indexOwners.ContainsKey(pair.Key)) { continue; }
            var bytes = _storage.ReadEntry(pair.Value.Page, pair.Value.Slot).ToArray();
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(26), -pair.Value.Root);
            using var bracket = _storage.BeginTransaction();
            _storage.UpdateEntry(bracket, pair.Value.Page, pair.Value.Slot, bytes);
            bracket.Commit();
            detached = true;
        }
        if (detached) { OpenIndexes(); }
        // Open only, after recovered liveness has passed and before admitting operations.
        ulong afterOwner = 0;
        while (true)
        {
            var owners = _storage.GetIndexOwners(afterOwner);
            if (owners.Count == 0) { break; }
            foreach (var (owner, page) in owners)
            {
                afterOwner = owner;
                if (owner <= 1 || _registrations.ContainsKey(owner) || _indexOwners.ContainsKey(owner)) { continue; }
                // Directory owner tags come from headers. Verify every unknown
                // owner's page before trusting that tag in a durable receipt.
                foreach (var unknown in _storage.GetOwnerIndexPages(owner))
                { using var verified = _storage.PageManager.GetPage(unknown); }
                RecordIndexRetirementReceipt(owner, page);
                ScheduleIndexRetirement(owner);
            }
        }
        foreach (ulong owner in _indexOwners.Keys) { if (!_registrations.ContainsKey(owner)) { ScheduleIndexRetirement(owner); } }
    }

    private (PageId Page, int Slot, long Root) RecordIndexRetirementReceipt(ulong owner, PageId page)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(0UL); writer.Write(0UL); writer.Write((byte)5); writer.Write(DocumentCatalogCodec.IndexRecordVersion);
        writer.Write(owner); writer.Write(-(long)page);
        DocumentCatalogCodec.WriteString(writer, "retired"); writer.Write(false);
        using var bracket = _storage.BeginTransaction();
        var location = _storage.InsertIndexRegistration(bracket, stream.ToArray());
        bracket.Commit();
        var registration = (location.PageId, location.SlotIndex, -(long)page);
        _registrations.Add(owner, registration);
        return registration;
    }

    private void ScheduleIndexRetirement(ulong owner)
    {
        lock (_sync)
        {
            if (_retiringIndexIds.Add(owner)) { _retiringIndexes.Enqueue(owner); }
        }
    }

    // 0 live, 1 pinned, 2 unreachable. A receipt cannot override recovered liveness.
    private int IndexRetirementStatus(ulong owner, TransactionSequence bound, bool receipt)
    {
        lock (_sync)
        {
            if (owner >= bound.Value) { return 1; }
            if (!_indexOwners.TryGetValue(owner, out var versions)) { return 2; }
            int status = 2;
            for (var reference = versions.First; reference is not null; reference = reference.Next)
            {
                if (Read(reference, versions.Matches) is not { } found || found.Record.Index?.ObjectId != owner) { continue; }
                if (receipt && found.Deleter == TransactionSequence.None) { return 0; }
                if (reference.Writer >= bound || found.Deleter >= bound) { status = Math.Min(status, 1); }
                else if (found.Deleter == TransactionSequence.None) { return 0; }
            }
            return status;
        }
    }

    private void ReclaimIndexes(TransactionSequence bound, CancellationToken token)
    {
        lock (_indexRegistrySync)
        {
            int checks;
            lock (_sync) { checks = Math.Min(IndexRetirement.PageBudget, _retiringIndexes.Count); }
            int remaining = IndexRetirement.PageBudget;
            bool rebuild = false;
            try
            {
                for (int check = 0; check < checks && remaining > 0; check++)
                {
                    token.ThrowIfCancellationRequested();
                    if (IndexRetirement.MustYield(_storage) || ReclamationCheckpointDue?.Invoke() == true) { return; }
                    ulong owner;
                    lock (_sync) { owner = _retiringIndexes.Dequeue(); }
                    bool finished = false;
                    try
                    {
                        if (!_registrations.TryGetValue(owner, out var registration))
                        {
                            int unregistered = IndexRetirementStatus(owner, bound, false);
                            if (unregistered == 0) { finished = true; continue; }
                            if (unregistered == 1) { continue; }
                            var pages = _storage.GetOwnerIndexPages(owner);
                            if (pages.Count == 0)
                            { lock (_sync) { _indexOwners.Remove(owner); } finished = true; continue; }
                            registration = RecordIndexRetirementReceipt(owner, pages[0]);
                        }
                        int status = IndexRetirementStatus(owner, bound, registration.Root < 0);
                        if (status == 0)
                        {
                            if (registration.Root < 0) { throw new DocumentCatalogException("CD06105", ErrorMessages.PhysicalRegistrationInvalid); }
                            finished = true; // DROP schedules the owner again.
                            continue;
                        }
                        if (status == 1) { continue; }
                        if (registration.Root > 0)
                        {
                            var bytes = _storage.ReadEntry(registration.Page, registration.Slot).ToArray();
                            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(26), -registration.Root);
                            using (var bracket = _storage.BeginTransaction())
                            {
                                _storage.UpdateEntry(bracket, registration.Page, registration.Slot, bytes);
                                bracket.Commit();
                            }
                            _registrations[owner] = (registration.Page, registration.Slot, -registration.Root);
                            rebuild = true;
                            AfterIndexRetirementMutation?.Invoke("retire-registration");
                        }
                        if (!IndexRetirement.TryFreePages(_storage, owner, remaining, token, out int freed,
                            () => AfterIndexRetirementMutation?.Invoke("reclaim-pages"))) { return; }
                        remaining -= freed;
                        IndexOwnerPagesReclaimed += freed;
                        IndexPagesReclaimed?.Invoke(owner, freed);
                        AfterIndexRetirementMutation?.Invoke("reclaim-batch");
                        if (_storage.GetOwnerIndexPageCount(owner) == 0)
                        {
                            using (var bracket = _storage.BeginTransaction())
                            {
                                _storage.DeleteEntry(bracket, registration.Page, registration.Slot);
                                AfterIndexRetirementMutation?.Invoke("reclaim-registration");
                                bracket.Commit();
                            }
                            _registrations.Remove(owner);
                            lock (_sync) { _indexOwners.Remove(owner); }
                            finished = true;
                            AfterIndexRetirementMutation?.Invoke("reclaimed");
                        }
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            if (finished) { _retiringIndexIds.Remove(owner); }
                            else { _retiringIndexes.Enqueue(owner); }
                        }
                    }
                }
            }
            finally { if (rebuild) { OpenIndexes(scheduleReclamation: false); } }
        }
    }
}
