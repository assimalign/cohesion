using System;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed partial class StorageCompactionTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Compaction: WAL recovers a stolen compacted image (#1161)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Recovery_PageFlushedAtCompactionNotification_ShouldHonorThePhysicalCommit(bool insert, bool commit)
    {
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream();
        var backup = new CrashSimulationStream(writeThrough: true);
        byte[] original = Filled(insert ? 4000 : 1000, 0x11);
        byte[] neighbour = Filled(insert ? 3000 : 5800, 0x33);
        byte[] changed = Filled(insert ? 1500 : 2000, 0x77);
        byte[] crashedData, crashedJournal, crashedBackup;
        (PageId PageId, int SlotIndex) target;
        using (var storage = RecordStorage.Create(data, journal, backup, 16))
        {
            using (var seed = storage.BeginTransaction())
            {
                target = storage.Insert(seed, ownerValue, original);
                storage.Insert(seed, ownerValue, neighbour).PageId.ShouldBe(target.PageId);
                seed.Commit();
            }
            if (insert)
            {
                using var deletion = storage.BeginTransaction();
                storage.Delete(deletion, target.PageId, target.SlotIndex);
                deletion.Commit();
            }
            int intermediateWrites = 0;
            storage.RecordPageCompacted = _ =>
            {
                // Insert notifies after packing; update notifies after replacement.
                // WAL permits fuzzy steals and must recover either image on both outcomes.
                storage.PageManager.FlushAll();
                intermediateWrites++;
            };
            using var bracket = storage.BeginTransaction();
            if (insert) { storage.Insert(bracket, ownerValue, changed).PageId.ShouldBe(target.PageId); }
            else { storage.Update(bracket, target.PageId, target.SlotIndex, changed); }
            using (var pin = storage.PageManager.GetPage(target.PageId)) { pin.Entry.IsDirty.ShouldBe(insert); }
            if (commit) { bracket.Commit(); }
            intermediateWrites.ShouldBe(1);
            crashedData = data.CaptureDurable();
            crashedJournal = journal.CaptureDurable();
            crashedBackup = backup.CaptureDurable();
        }
        using var recovered = RecordStorage.Open(crashedData, crashedJournal, crashedBackup);
        recovered.Read(target.PageId, 1).Span.SequenceEqual(neighbour).ShouldBeTrue();
        if (insert)
        {
            recovered.TryReadRecord(target.PageId, 0, ownerValue, out _).ShouldBeFalse();
            bool found = recovered.TryReadRecord(target.PageId, 2, ownerValue, out var bytes);
            found.ShouldBe(commit);
            if (found) { bytes.Span.SequenceEqual(changed).ShouldBeTrue(); }
        }
        else { recovered.Read(target.PageId, 0).Span.SequenceEqual(commit ? changed : original).ShouldBeTrue(); }
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Compaction: torn writes at every WAL and page write recover a complete layout (#1161)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_PowerLossAtEveryCompactionWrite_ShouldRecoverACompleteLayout(bool insert)
    {
        // A dry run counts only writes after the committed seed, including the final steal.
        var dry = RunPowerLoss(insert, 0, 0, 0);
        dry.Writes.ShouldBeGreaterThan(0);
        for (int write = 1; write <= dry.Writes; write++)
        {
            foreach (var (sectors, offset) in new[] { (0, 0), (1, 0), (8, 7), (16, 0) })
            {
                var run = RunPowerLoss(insert, write, sectors, offset);
                run.Crashed.ShouldBeTrue();
                using var recovered = RecordStorage.Open(run.Data, run.Journal, run.Backup);
                byte[] original = Filled(insert ? 4000 : 1000, 0x11);
                byte[] changed = Filled(insert ? 1500 : 2000, 0x77);
                recovered.Read(run.Page, 1).Span.SequenceEqual(Filled(insert ? 3000 : 5800, 0x33)).ShouldBeTrue();
                bool isNew;
                if (insert)
                {
                    recovered.TryReadRecord(run.Page, 0, ownerValue, out _).ShouldBeFalse();
                    isNew = recovered.TryReadRecord(run.Page, 2, ownerValue, out var bytes);
                    if (isNew) { bytes.Span.SequenceEqual(changed).ShouldBeTrue(); }
                }
                else
                {
                    var bytes = recovered.Read(run.Page, 0);
                    isNew = bytes.Span.SequenceEqual(changed);
                    (isNew || bytes.Span.SequenceEqual(original)).ShouldBeTrue();
                }
                // Only a commit whose acknowledgement was lost may recover either outcome.
                if (run.CommitState != 1) { isNew.ShouldBe(run.CommitState == 2); }
                recovered.GetOwnerPages(ownerValue).ShouldHaveSingleItem().ShouldBe(run.Page);
            }
        }
    }

    private static PowerLossRun RunPowerLoss(bool insert, int write, int sectors, int sectorOffset)
    {
        var point = new CrashPoint { DurableSectors = sectors, DurableSectorOffset = sectorOffset };
        var data = new CrashSimulationStream(writeThrough: true, crashPoint: point, name: "data");
        var journal = new CrashSimulationStream(crashPoint: point, name: "journal");
        var backup = new CrashSimulationStream(writeThrough: true, crashPoint: point, name: "backup");
        var storage = RecordStorage.Create(data, journal, backup, 16);
        (PageId PageId, int SlotIndex) target;
        using (var seed = storage.BeginTransaction())
        {
            target = storage.Insert(seed, ownerValue, Filled(insert ? 4000 : 1000, 0x11));
            storage.Insert(seed, ownerValue, Filled(insert ? 3000 : 5800, 0x33)).PageId.ShouldBe(target.PageId);
            seed.Commit();
        }
        if (insert)
        {
            using var deletion = storage.BeginTransaction();
            storage.Delete(deletion, target.PageId, target.SlotIndex);
            deletion.Commit();
        }
        int seedWrites = point.Writes;
        if (write > 0) { point.CrashAtWrite = seedWrites + write; }
        int commitState = 0;
        try
        {
            using var bracket = storage.BeginTransaction();
            if (insert) { storage.Insert(bracket, ownerValue, Filled(1500, 0x77)).PageId.ShouldBe(target.PageId); }
            else { storage.Update(bracket, target.PageId, target.SlotIndex, Filled(2000, 0x77)); }
            storage.PageManager.FlushAll();
            commitState = 1;
            bracket.Commit();
            commitState = 2;
            storage.PageManager.FlushAll();
        }
        catch (Exception exception) when (point.HasCrashed && IsCompactionPowerLoss(exception)) { }
        var run = new PowerLossRun(data.CaptureDurable(), journal.CaptureDurable(), backup.CaptureDurable(),
            target.PageId, commitState, point.Writes - seedWrites, point.HasCrashed);
        try { storage.Dispose(); }
        catch (Exception exception) when (point.HasCrashed && IsCompactionPowerLoss(exception)) { }
        return run;
    }

    private static bool IsCompactionPowerLoss(Exception exception)
        => exception is SimulatedPowerLossException or StorageOfflineException
        || exception.InnerException is not null && IsCompactionPowerLoss(exception.InnerException);

    private sealed record PowerLossRun(byte[] Data, byte[] Journal, byte[] Backup, PageId Page,
        int CommitState, int Writes, bool Crashed);

    [Theory(DisplayName = "Cohesion Test [Storage] - Compaction: WAL restores committed and uncommitted packed layouts with or without stolen writes (#1161)")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Recovery_CompactedRecordPage_ShouldHonorThePhysicalCommit(bool insert, bool commit, bool steal)
    {
        // Arrange: data writes can persist before commit; only journal flushes are durable.
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream();
        var backup = new CrashSimulationStream(writeThrough: true);
        byte[] original = Filled(insert ? 4000 : 1000, 0x11);
        byte[] neighbour = Filled(insert ? 3000 : 5800, 0x33);
        byte[] changed = Filled(insert ? 1500 : 2000, 0x77);
        (PageId PageId, int SlotIndex) target, kept, inserted = default;
        byte[] crashedData, crashedJournal, crashedBackup;
        using (var storage = RecordStorage.Create(data, journal, backup, steal ? 1 : 16))
        {
            using (var bracket = storage.BeginTransaction())
            {
                target = storage.Insert(bracket, ownerValue, original);
                kept = storage.Insert(bracket, ownerValue, neighbour);
                bracket.Commit();
            }
            if (insert)
            {
                using var bracket = storage.BeginTransaction();
                storage.Delete(bracket, target.PageId, target.SlotIndex);
                bracket.Commit();
            }
            int rewrites = 0;
            storage.RecordPageCompacted = page => { page.ShouldBe(target.PageId); rewrites++; };
            using (var bracket = storage.BeginTransaction())
            {
                if (insert) { inserted = storage.Insert(bracket, ownerValue, changed); }
                else { storage.Update(bracket, target.PageId, target.SlotIndex, changed); }
                if (steal)
                {
                    // The one-frame pool must evict the compacted frame before this bracket resolves.
                    storage.Insert(bracket, ownerValue + 1, Filled(6000, 0x55)).PageId.ShouldNotBe(target.PageId);
                }
                if (commit) { bracket.Commit(); }
                rewrites.ShouldBe(1);
                crashedData = data.CaptureDurable();
                crashedJournal = journal.CaptureDurable();
                crashedBackup = backup.CaptureDurable();
            }
        }

        // Act: disposing the original storage after capture cannot change these power-loss images.
        using var recovered = RecordStorage.Open(crashedData, crashedJournal, crashedBackup);

        // Assert: stable references and the shifted neighbour survive both redo and abort recovery.
        recovered.Read(kept.PageId, kept.SlotIndex).Span.SequenceEqual(neighbour).ShouldBeTrue();
        if (insert)
        {
            recovered.TryReadRecord(target.PageId, target.SlotIndex, ownerValue, out _).ShouldBeFalse();
            bool found = recovered.TryReadRecord(inserted.PageId, inserted.SlotIndex, ownerValue, out var bytes);
            found.ShouldBe(commit);
            if (commit) { bytes.Span.SequenceEqual(changed).ShouldBeTrue(); }
        }
        else
        {
            recovered.Read(target.PageId, target.SlotIndex).Span.SequenceEqual(commit ? changed : original).ShouldBeTrue();
        }
        recovered.GetOwnerPages(ownerValue).ShouldHaveSingleItem().ShouldBe(kept.PageId);
    }
}
