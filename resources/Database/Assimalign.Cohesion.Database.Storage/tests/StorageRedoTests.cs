using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Storage format 3 (#1253): a full page image on a page's first change since the checkpoint, a
/// byte-range delta (or a committed full image) at commit, and ordered, redo-only recovery that
/// chains every delta onto the LSN the page carried. Each crash case runs over write-through data
/// streams, so every page write the pool makes — a steal included — is on the media at once.
/// </summary>
public sealed class StorageRedoTests
{
    // ---------------------------------------------------------------- record shapes

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a page's first change since the checkpoint journals its image, later commits only deltas chained by LSN")]
    public void Commit_FirstChangeSinceTheCheckpoint_ShouldJournalOneImageThenChainedDeltas()
    {
        // Arrange
        using var storage = TornStorage.Create();
        storage.Insert("first");
        storage.Checkpoint();
        long redoPoint = storage.RedoLsn;

        // Act
        var (pageId, _) = storage.Insert("second");
        storage.Insert("third");
        var records = storage.Log.ReadAll();

        // Assert: one image of the page after the checkpoint; each delta names the record before it.
        records.Select(record => record.Type).ShouldBe(
        [
            JournalRecordType.Checkpoint,
            JournalRecordType.BeginTransaction, JournalRecordType.FullPageImage, JournalRecordType.PageDelta, JournalRecordType.CommitTransaction,
            JournalRecordType.BeginTransaction, JournalRecordType.PageDelta, JournalRecordType.CommitTransaction,
        ]);
        redoPoint.ShouldBe(records[0].Lsn);
        BaseLsn(records[3]).ShouldBe(records[2].Lsn);
        BaseLsn(records[6]).ShouldBe(records[3].Lsn);
        storage.PageLsn(pageId).ShouldBe(records[6].Lsn);
        records[3].Payload.Length.ShouldBeLessThan(64);
        records[6].Payload.Length.ShouldBeLessThan(64);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a page touched and left unchanged journals nothing at commit and keeps its LSN")]
    public void Commit_TouchedButUnchanged_ShouldJournalNothingForThePage()
    {
        // Arrange
        using var storage = TornStorage.Create();
        var (pageId, slot) = storage.Insert("same");
        long lsn = storage.PageLsn(pageId);

        // Act
        long before = storage.Log.LastLsn;
        using (var transaction = storage.BeginTransaction())
        {
            storage.Update(transaction, pageId, slot, "same");
            transaction.Commit();
        }

        var records = storage.Log.ReadAll().Where(record => record.Lsn > before).ToArray();

        // Assert
        records.Select(record => record.Type).ShouldBe([JournalRecordType.BeginTransaction, JournalRecordType.CommitTransaction]);
        storage.PageLsn(pageId).ShouldBe(lsn);
    }

    // ---------------------------------------------------------------- invariant A

    /// <summary>
    /// Invariant A: allocation zeroes the page LSN, so a page freed and reallocated within one
    /// checkpoint interval is imaged again, initialization included. Had the LSN of its free
    /// survived, the reallocating transaction's delta would chain onto the freed page and the
    /// allocation's initialization (type, owner tag, slotted header) would be in no record.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a page freed and reallocated within one checkpoint interval is imaged again and recovers")]
    public void Recovery_PageFreedAndReallocatedInOneWindow_ShouldRecoverTheNewOwnersRecord()
    {
        // Arrange: owner 7 fills a page, releases it, and owner 9 takes the page in the same
        // checkpoint interval; then a crash before any page reaches the data file.
        var storage = TornStorage.Create(); // abandoned: a crash
        storage.Checkpoint();
        var first = Bytes(6000, seed: 1);
        var second = Bytes(5000, seed: 2);
        PageId pageId;
        int slot;
        using (var transaction = storage.BeginTransaction())
        {
            (pageId, slot) = storage.Insert(transaction, 7, first);
            transaction.Commit();
        }

        using (var transaction = storage.BeginTransaction())
        {
            storage.Delete(transaction, pageId, slot);
            transaction.Commit();
        }

        PageId reused;
        using (var transaction = storage.BeginTransaction())
        {
            (reused, _) = storage.Insert(transaction, 9, second);
            transaction.Commit();
        }

        var images = storage.CaptureDurable();

        // Act
        using var recovered = TornStorage.Open(images);

        // Assert: the page was imaged at each allocation, and recovers as owner 9's.
        reused.ShouldBe(pageId);
        JournalImage.PageRecords(images.Journal, (long)pageId).Count(record => record.Type == JournalRecordType.FullPageImage).ShouldBe(2);
        recovered.ScanOwner(9).Single().ShouldBe(second);
        recovered.ScanOwner(7).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: allocation zeroes the LSN of a page still resident in the pool")]
    public void AllocatePage_ResidentPageWithAnLsn_ShouldZeroItsLsn()
    {
        // Arrange
        var stream = new StorageStream(new System.IO.MemoryStream());
        var pool = new StorageBufferPool(4);
        var map = new StorageFreeSpaceMap();
        using var manager = new StoragePageManager(stream, pool, map);
        long pageId;
        using (var first = manager.AllocatePage(PageType.Data))
        {
            pageId = (long)first.Id;
            var page = first.Page;
            page.Lsn = 77;
            first.MarkDirty();
        }

        map.Free((PageId)pageId);

        // Act
        using var again = manager.AllocatePage(PageType.Index);

        // Assert
        ((long)again.Id).ShouldBe(pageId);
        again.Page.Lsn.ShouldBe(0L);
        pool.Dispose();
    }

    // ---------------------------------------------------------------- crash cases

    [Theory(DisplayName = "Cohesion Test [Storage] - Redo: a stolen uncommitted page is overwritten by its image and the committed deltas")]
    [InlineData(true)]
    [InlineData(false)]
    public void Recovery_StolenUncommittedPage_ShouldRebuildTheCommittedPage(bool checkpointFirst)
    {
        // Arrange: a committed row; then (cold: after a checkpoint, so the touch images the page;
        // warm: the page imaged already) an uncommitted update stolen to the data file.
        var storage = TornStorage.Create(poolCapacity: 2, journalWriteThrough: false); // abandoned: a crash
        var (pageId, slot) = storage.Insert("committed");
        if (checkpointFirst)
        {
            storage.Checkpoint();
        }

        var transaction = storage.BeginTransaction();
        storage.Update(transaction, pageId, slot, "stolen!!!");
        storage.PageManager.FlushAll();
        var images = storage.CaptureDurable();

        // Act
        using var recovered = TornStorage.Open(images);

        // Assert
        PageHolds(images.Data, pageId, "stolen!!!").ShouldBeTrue();
        recovered.Read(pageId, slot).ShouldBe("committed");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a non-durable statement bracket whose commit never reached the media is not redone")]
    public void Recovery_StolenThenNonDurableCommitLost_ShouldRebuildTheEarlierCommit()
    {
        // Arrange: a statement bracket steals its page, commits without awaiting durability (its
        // logical transaction owns durability), and power is lost before anything flushes it.
        var storage = TornStorage.Create(poolCapacity: 2, journalWriteThrough: false); // abandoned: a crash
        var (pageId, slot) = storage.Insert("committed");
        storage.Checkpoint();
        var statement = storage.BeginTransaction();
        storage.Update(statement, pageId, slot, "statement");
        storage.PageManager.FlushAll();
        statement.Commit(awaitDurability: false);
        storage.Log.ReadAll(); // the records leave the buffer, but no flush covers them
        var images = storage.CaptureDurable();

        // Act
        using var recovered = TornStorage.Open(images);

        // Assert
        PageHolds(images.Data, pageId, "statement").ShouldBeTrue();
        JournalImage.Records(images.Journal).ShouldNotContain(record => record.Type == JournalRecordType.CommitTransaction && record.TransactionSequence == statement.Sequence);
        recovered.Read(pageId, slot).ShouldBe("committed");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: durable commits are redone when no page reached the data file")]
    public void Recovery_DurableCommitsWithoutPageWrites_ShouldBeRedone()
    {
        // Arrange
        var storage = TornStorage.Create(journalWriteThrough: false); // abandoned: a crash
        var (pageId, slot) = storage.Insert("v1");
        storage.Checkpoint();
        var dataAtCheckpoint = storage.CaptureDurable().Data;
        using (var transaction = storage.BeginTransaction())
        {
            storage.Update(transaction, pageId, slot, "v2");
            transaction.Commit();
        }

        var images = storage.CaptureDurable();

        // Act
        using var recovered = TornStorage.Open(images);

        // Assert
        images.Data.ShouldBe(dataAtCheckpoint);
        recovered.Read(pageId, slot).ShouldBe("v2");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a rollback after a steal, lost before its restored page is written, recovers the committed page")]
    public void Recovery_RollbackAfterStealLostBeforeWriteBack_ShouldRebuildTheCommittedPage()
    {
        // Arrange
        var storage = TornStorage.Create(poolCapacity: 2, journalWriteThrough: false); // abandoned: a crash
        var (pageId, slot) = storage.Insert("committed");
        storage.Checkpoint();
        var transaction = storage.BeginTransaction();
        storage.Update(transaction, pageId, slot, "stolen!!!");
        storage.PageManager.FlushAll();
        transaction.Rollback();
        string inMemory = storage.Read(pageId, slot);
        var images = storage.CaptureDurable();

        // Act
        using var recovered = TornStorage.Open(images);

        // Assert
        inMemory.ShouldBe("committed");
        PageHolds(images.Data, pageId, "stolen!!!").ShouldBeTrue();
        recovered.Read(pageId, slot).ShouldBe("committed");
    }

    /// <summary>
    /// A rollback restores the page's content and the LSN of its last record, which here is the
    /// image the rolled-back bracket journaled: the next bracket journals no image of its own and
    /// chains its delta onto that one.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: after a rolled-back bracket imaged a page, the next bracket's delta chains onto that image")]
    public void Recovery_NextBracketAfterARolledBackImage_ShouldChainOntoTheImage()
    {
        // Arrange
        var storage = TornStorage.Create(); // abandoned: a crash
        var (pageId, slot) = storage.Insert("v1");
        storage.Checkpoint();
        using (var rolledBack = storage.BeginTransaction())
        {
            storage.Update(rolledBack, pageId, slot, "rolled back");
            rolledBack.Rollback();
        }

        using (var transaction = storage.BeginTransaction())
        {
            storage.Update(transaction, pageId, slot, "v2");
            transaction.Commit();
        }

        var images = storage.CaptureDurable();

        // Act
        using var recovered = TornStorage.Open(images);
        var records = JournalImage.PageRecords(images.Journal, (long)pageId);

        // Assert
        records.Select(record => record.Type).ShouldBe([JournalRecordType.FullPageImage, JournalRecordType.PageDelta]);
        BaseLsn(records[1]).ShouldBe(records[0].Lsn);
        recovered.Read(pageId, slot).ShouldBe("v2");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a crash at every write of a recovery, then another recovery, gives the same file")]
    public void Recovery_CrashAtEveryRecoveryWrite_ShouldRecoverToTheSameFile()
    {
        // Arrange: committed and uncommitted work over several pages, some stolen, then a crash;
        // a small pool also makes recovery write pages early and read them back.
        var crashed = CrashedWorkload(out var expected);
        var clean = RecoverAndCapture(crashed, point: null);
        var dryPoint = new CrashPoint();
        RecoverAndCapture(crashed, dryPoint);
        int recoveryWrites = dryPoint.Writes;
        recoveryWrites.ShouldBeGreaterThan(3);

        for (int write = 1; write <= recoveryWrites; write++)
        {
            foreach (int sectors in new[] { 0, 7 })
            {
                // Act: lose power at that write of the recovery, then recover again.
                var point = new CrashPoint { CrashAtWrite = write, DurableSectors = sectors };
                var data = new CrashSimulationStream(crashed.Data, writeThrough: true, point, "data");
                var journal = new CrashSimulationStream(crashed.Journal, writeThrough: true, point, "journal");
                SimulatedPowerLossException.ShouldBeThrownBy(() => TornStorage.Open(data, journal, poolCapacity: 2), $"write {write}");
                var again = RecoverAndCapture((data.CaptureDurable(), journal.CaptureDurable()), point: null);

                // Assert
                again.Data.ShouldBe(clean.Data, $"write {write}, {sectors} sectors");
            }
        }

        using var recovered = TornStorage.Open(crashed);
        foreach (var (location, text) in expected)
        {
            recovered.Read(location.PageId, location.SlotIndex).ShouldBe(text);
        }
    }

    // ---------------------------------------------------------------- committed full images

    /// <summary>
    /// A commit that rewrites most of a page journals a committed full image instead of a delta.
    /// It is a post-image: applied unconditionally, as a full page image is, it would redo a
    /// transaction whose commit record never reached the journal. Recovery applies it only with its
    /// commit record.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Redo: a committed full image whose commit record was lost is not applied")]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_CommittedImageWithoutItsCommitRecord_ShouldNotBeApplied(bool stolen)
    {
        // Arrange: a page holding 6,000 random bytes, rewritten whole after a checkpoint.
        var storage = TornStorage.Create(); // abandoned: a crash
        var before = Bytes(6000, seed: 3);
        var after = Bytes(6000, seed: 4);
        PageId pageId;
        int slot;
        using (var transaction = storage.BeginTransaction())
        {
            (pageId, slot) = storage.Insert(transaction, 7, before);
            transaction.Commit();
        }

        storage.Checkpoint();
        using (var transaction = storage.BeginTransaction())
        {
            storage.Update(transaction, pageId, slot, after);
            if (stolen)
            {
                storage.PageManager.FlushAll();
            }

            transaction.Commit();
        }

        var images = storage.CaptureDurable();
        var frames = JournalImage.Frames(images.Journal);
        var committedImage = frames.Single(frame => frame.Type == JournalRecordType.CommittedPageImage);
        var withoutCommit = images.Journal.AsSpan(0, committedImage.Offset + committedImage.Length).ToArray();

        // Act
        using (var committed = TornStorage.Open(images))
        {
            committed.ReadBytes(pageId, slot).ShouldBe(after);
        }

        using var lost = TornStorage.Open((images.Data, withoutCommit));

        // Assert
        frames.ShouldNotContain(frame => frame.Type == JournalRecordType.PageDelta && frame.PageId == (long)pageId);
        lost.ReadBytes(pageId, slot).ShouldBe(before);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a page a delete cleared commits a committed image of little more than its header")]
    public void Commit_PageClearedByADelete_ShouldJournalASmallCommittedImage()
    {
        // Arrange
        using var storage = TornStorage.Create();
        PageId pageId;
        int slot;
        using (var transaction = storage.BeginTransaction())
        {
            (pageId, slot) = storage.Insert(transaction, 7, Bytes(7000, seed: 5));
            transaction.Commit();
        }

        // Act
        long before = storage.Log.LastLsn;
        using (var transaction = storage.BeginTransaction())
        {
            storage.Delete(transaction, pageId, slot);
            transaction.Commit();
        }

        var record = storage.Log.ReadAll().Single(r => r.Lsn > before && StorageRecovery.IsPageRecord(r.Type));

        // Assert
        record.Type.ShouldBe(JournalRecordType.CommittedPageImage);
        record.Payload.Length.ShouldBeLessThan(64);
    }

    // ---------------------------------------------------------------- torn writes

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a data page write torn at every sector boundary recovers the committed page")]
    public void Recovery_DataPageWriteTornAtEverySector_ShouldRecoverTheCommittedPage()
    {
        for (int sectors = 0; sectors <= Page.Size / CrashSimulationStream.SectorSize; sectors++)
        {
            // Arrange: a committed rewrite of an imaged page, then an uncommitted one whose
            // write-back loses power with only a prefix of its sectors on the media.
            var point = new CrashPoint();
            var storage = TornStorage.Create(point, poolCapacity: 4); // abandoned after its power loss
            var original = Bytes(6000, seed: 6);
            var committed = Bytes(6000, seed: 7);
            var uncommitted = Bytes(6000, seed: 8);
            PageId pageId;
            int slot;
            using (var transaction = storage.BeginTransaction())
            {
                (pageId, slot) = storage.Insert(transaction, 7, original);
                transaction.Commit();
            }

            storage.Checkpoint();
            using (var transaction = storage.BeginTransaction())
            {
                storage.Update(transaction, pageId, slot, committed);
                transaction.Commit();
            }

            var open = storage.BeginTransaction();
            storage.Update(open, pageId, slot, uncommitted);
            long offset = (long)pageId * Page.Size;
            point.DurableSectors = sectors;
            point.CrashWhen = (stream, operation, at, _) => stream == "data" && operation == "Write" && at == offset;
            SimulatedPowerLossException.ShouldBeThrownBy(() => storage.PageManager.FlushAll(), $"{sectors} sectors");

            // Act
            using var recovered = TornStorage.Open(storage.CaptureDurable());

            // Assert
            recovered.ReadBytes(pageId, slot).ShouldBe(committed, $"{sectors} sectors");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a page delta or its commit record cut at every byte is a torn tail, never applied")]
    public void Recovery_DeltaFrameCutAtEveryByte_ShouldBeIgnoredAsATornTail()
    {
        // Arrange
        var storage = TornStorage.Create(); // abandoned: a crash
        var (pageId, slot) = storage.Insert("v1");
        using (var transaction = storage.BeginTransaction())
        {
            storage.Update(transaction, pageId, slot, "v2");
            transaction.Commit();
        }

        var images = storage.CaptureDurable();
        var frames = JournalImage.Frames(images.Journal);
        var delta = frames.Last(frame => frame.Type == JournalRecordType.PageDelta);
        var commit = frames.Last();
        commit.Type.ShouldBe(JournalRecordType.CommitTransaction);

        for (int cut = delta.Offset; cut <= commit.Offset + commit.Length; cut++)
        {
            // Act
            using var recovered = TornStorage.Open((images.Data, images.Journal.AsSpan(0, cut).ToArray()));

            // Assert
            recovered.Read(pageId, slot).ShouldBe(cut == commit.Offset + commit.Length ? "v2" : "v1", $"cut at {cut}");
        }
    }

    // ---------------------------------------------------------------- chain gaps

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a gap in a page's delta chain fails recovery as corruption")]
    public void Recovery_MissingDelta_ShouldFailAsAGap()
    {
        // Arrange: three committed deltas of one page; the middle one is cut out of the journal,
        // leaving every remaining frame valid.
        var storage = TornStorage.Create(); // abandoned: a crash
        var (pageId, slot) = storage.Insert("v1");
        foreach (string value in new[] { "v2", "v3" })
        {
            using var transaction = storage.BeginTransaction();
            storage.Update(transaction, pageId, slot, value);
            transaction.Commit();
        }

        var images = storage.CaptureDurable();
        var deltas = JournalImage.Frames(images.Journal).Where(frame => frame.Type == JournalRecordType.PageDelta).ToArray();
        deltas.Length.ShouldBe(3);

        // Act
        var refusal = Should.Throw<StorageCorruptionException>(() => TornStorage.Open((images.Data, JournalImage.Without(images.Journal, deltas[1]))));

        // Assert
        refusal.PageId.ShouldBe(pageId);
        refusal.Message.ShouldContain("gap");
        refusal.Message.ShouldContain($"LSN {deltas[2].Lsn} follows LSN {deltas[1].Lsn}");
        refusal.Message.ShouldContain($"carries LSN {deltas[0].Lsn}");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a committed delta with no full page image before it fails recovery as corruption")]
    public void Recovery_DeltaWithoutAnImage_ShouldFailAsAGap()
    {
        // Arrange
        var storage = TornStorage.Create(); // abandoned: a crash
        var (pageId, _) = storage.Insert("v1");
        var images = storage.CaptureDurable();
        var image = JournalImage.Frames(images.Journal).Single(frame => frame.Type == JournalRecordType.FullPageImage);

        // Act
        var refusal = Should.Throw<StorageCorruptionException>(() => TornStorage.Open((images.Data, JournalImage.Without(images.Journal, image))));

        // Assert
        refusal.PageId.ShouldBe(pageId);
        refusal.Message.ShouldContain("no full page image before it");
    }

    // ---------------------------------------------------------------- recovery's own writes

    /// <summary>
    /// Recovery writes every page it rebuilt with the LSN of the last record it applied. An open
    /// that defers its checkpoint (every engine does, for its scrub) then commits on top: the new
    /// deltas name the recovered LSNs, so a second crash recovers through the same chain.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: recovery stamps each page with its last record's LSN, and later deltas chain onto it")]
    public void Recovery_StampedPages_ShouldChainTheNextDeltasAndRecoverAgain()
    {
        // Arrange: a committed row, and an uncommitted bracket that allocated a page of its own.
        var storage = TornStorage.Create(); // abandoned: a crash
        var (pageId, slot) = storage.Insert("v1");
        var open = storage.BeginTransaction();
        storage.Insert(open, 7, Bytes(100, seed: 9));
        storage.Log.Flush(forceDurable: true);
        var images = storage.CaptureDurable();
        long lastRecord = JournalImage.PageRecords(images.Journal, (long)pageId).Last().Lsn;

        // Act: recover without the checkpoint, commit an update, crash, recover again.
        (byte[] Data, byte[] Journal) second;
        long stamped;
        long deltaBase;
        using (var recovered = TornStorage.Open(images))
        {
            var recoveredData = recovered.CaptureDurable().Data;
            stamped = BinaryPrimitives.ReadInt64LittleEndian(recoveredData.AsSpan((int)((long)pageId * Page.Size) + Page.LsnFieldOffset));
            using (var transaction = recovered.BeginTransaction())
            {
                recovered.Update(transaction, pageId, slot, "v2");
                transaction.Commit();
            }

            second = recovered.CaptureDurable();
            deltaBase = BaseLsn(JournalImage.PageRecords(second.Journal, (long)pageId).Last());
        }

        using var again = TornStorage.Open(second);

        // Assert: the uncommitted bracket's page is its image, an empty page of owner 7.
        stamped.ShouldBe(lastRecord);
        deltaBase.ShouldBe(stamped);
        again.Read(pageId, slot).ShouldBe("v2");
        again.ScanOwner(7).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a recovery that holds one page at a time writes pages early and reads them back")]
    public void Recovery_OnePageCache_ShouldMatchAFullCache()
    {
        // Arrange: six pages, each changed by six interleaved committed brackets, then a crash.
        var storage = TornStorage.Create(poolCapacity: 64); // abandoned: a crash
        var rows = new List<(PageId PageId, int Slot, ulong Owner)>();
        using (var transaction = storage.BeginTransaction())
        {
            for (ulong owner = 1; owner <= 6; owner++)
            {
                var (pageId, slot) = storage.Insert(transaction, owner, Bytes(1000, seed: (int)owner));
                rows.Add((pageId, slot, owner));
            }

            transaction.Commit();
        }

        for (int round = 0; round < 6; round++)
        {
            using var transaction = storage.BeginTransaction();
            foreach (var (pageId, slot, owner) in rows)
            {
                storage.Update(transaction, pageId, slot, Bytes(1000, seed: (round * 10) + (int)owner));
            }

            transaction.Commit();
        }

        var images = storage.CaptureDurable();

        // Act
        using var full = TornStorage.Open(images, poolCapacity: 64);
        using var small = TornStorage.Open(images, poolCapacity: 1);

        // Assert
        foreach (var (pageId, slot, owner) in rows)
        {
            small.ReadBytes(pageId, slot).ShouldBe(Bytes(1000, seed: 50 + (int)owner));
            full.ReadBytes(pageId, slot).ShouldBe(small.ReadBytes(pageId, slot));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a page past the end of the data file that only uncommitted images describe is not written")]
    public void Recovery_UncommittedAllocationPastTheEnd_ShouldNotExtendTheFile()
    {
        // Arrange: the data file is flush-gated, so a page allocated after the checkpoint never
        // extended it on the media; the allocating bracket's image is durable, its commit is not.
        var storage = TornStorage.Create(dataWriteThrough: false); // abandoned: a crash
        storage.Insert("kept");
        storage.Checkpoint();
        long lengthAtCheckpoint = storage.CaptureDurable().Data.Length;
        var open = storage.BeginTransaction();
        var (pageId, _) = storage.Insert(open, 9, Bytes(500, seed: 10));
        storage.Log.Flush(forceDurable: true);
        var images = storage.CaptureDurable();

        // Act
        long lengthAfterRecovery;
        using (var recovered = TornStorage.Open(images))
        {
            lengthAfterRecovery = recovered.CaptureDurable().Data.Length;
            recovered.ScanText().ShouldBe(["kept"]);
        }

        // Assert
        ((long)pageId * Page.Size).ShouldBeGreaterThanOrEqualTo(lengthAtCheckpoint);
        JournalImage.PageRecords(images.Journal, (long)pageId).ShouldContain(record => record.Type == JournalRecordType.FullPageImage);
        lengthAfterRecovery.ShouldBe(lengthAtCheckpoint);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a committed allocation past the end of the data file is written by recovery")]
    public void Recovery_CommittedAllocationPastTheEnd_ShouldExtendTheFile()
    {
        // Arrange
        var storage = TornStorage.Create(dataWriteThrough: false); // abandoned: a crash
        storage.Insert("kept");
        storage.Checkpoint();
        var record = Bytes(500, seed: 11);
        using (var transaction = storage.BeginTransaction())
        {
            storage.Insert(transaction, 9, record);
            transaction.Commit();
        }

        var images = storage.CaptureDurable();

        // Act
        using var recovered = TornStorage.Open(images);

        // Assert
        recovered.ScanOwner(9).Single().ShouldBe(record);
    }

    // ---------------------------------------------------------------- a page above the journal at open

    /// <summary>
    /// Under <see cref="StorageCommitDurability.None"/> the write-ahead gate drains the journal
    /// without an fsync, so a power loss can keep a page the pool wrote back and lose the journal
    /// records that stamped it. Open finds the page above every LSN the journal holds: LSNs resume
    /// above it and the redo point moves up to it, so its next change journals a full image and a
    /// second crash recovers through it. Before the #1253 review LSNs restarted below the page's,
    /// its next delta named a base no record produced, and the following open refused the file set
    /// with a chain gap.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a page that outlived its journal records under None durability is imaged again and recovers")]
    public void Open_PageAboveTheDurableJournal_ShouldRaiseTheRedoPointAndImageThePage()
    {
        // Arrange: a None-mode commit whose page reaches the data file while its journal records
        // stay in the operating system's cache, then a power loss.
        var storage = TornStorage.Create(journalWriteThrough: false, journalDurableFlushesOnly: true); // abandoned: a crash
        storage.CommitDurability = StorageCommitDurability.None;
        var (pageId, slot) = storage.Insert("v1");
        storage.PageManager.FlushAll();
        long pageLsn = storage.PageLsn(pageId);
        var lost = storage.CaptureDurable();

        // Act: open (with the consistency check on), change the page durably, lose power, open again.
        (byte[] Data, byte[] Journal) second;
        long redoLsn;
        string afterLoss;
        using (var reopened = TornStorage.Open(
            new CrashSimulationStream(lost.Data, writeThrough: true),
            new CrashSimulationStream(lost.Journal, writeThrough: true),
            consistencyChecks: true))
        {
            redoLsn = reopened.RedoLsn;
            afterLoss = reopened.Read(pageId, slot);
            using (var transaction = reopened.BeginTransaction())
            {
                reopened.Update(transaction, pageId, slot, "v2");
                transaction.Commit();
            }

            second = reopened.CaptureDurable();
        }

        using var again = TornStorage.Open(second);
        var pageRecords = JournalImage.PageRecords(second.Journal, (long)pageId);

        // Assert: the journal kept nothing, the page kept its LSN, and the page was imaged above it.
        JournalImage.Records(lost.Journal).ShouldNotContain(record => StorageRecovery.IsPageRecord(record.Type));
        DataPageLsn(lost.Data, pageId).ShouldBe(pageLsn);
        afterLoss.ShouldBe("v1");
        redoLsn.ShouldBe(pageLsn);
        pageRecords.Select(record => record.Type).ShouldBe([JournalRecordType.FullPageImage, JournalRecordType.PageDelta]);
        pageRecords[0].Lsn.ShouldBeGreaterThan(pageLsn);
        BaseLsn(pageRecords[1]).ShouldBe(pageRecords[0].Lsn);
        again.Read(pageId, slot).ShouldBe("v2");
    }

    /// <summary>
    /// A journal file restored from an older copy (or lost and recreated) leaves data pages above
    /// every LSN it holds, whatever the durability mode: the same open-time rule resumes LSNs above
    /// them, so a durable commit made after the open survives the next crash. Before the #1253
    /// review that commit made the file set unopenable; in storage format 2 it was lost silently.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a data file newer than its journal resumes LSNs above its pages and keeps later commits")]
    public void Open_JournalOlderThanTheDataFile_ShouldResumeLsnsAboveThePages()
    {
        // Arrange: v1 committed and checkpointed, the journal kept as it stood then; v2 committed
        // and its page written back; the file set opened with the older journal.
        var storage = TornStorage.Create(); // abandoned: a crash
        var (pageId, slot) = storage.Insert("v1");
        storage.Checkpoint();
        var olderJournal = storage.CaptureDurable().Journal;
        using (var transaction = storage.BeginTransaction())
        {
            storage.Update(transaction, pageId, slot, "v2");
            transaction.Commit();
        }

        storage.PageManager.FlushAll();
        long pageLsn = storage.PageLsn(pageId);
        var images = (storage.CaptureDurable().Data, olderJournal);

        // Act: open, commit v3 durably, crash, open again.
        (byte[] Data, byte[] Journal) second;
        long nextLsn;
        using (var reopened = TornStorage.Open(images))
        {
            reopened.Read(pageId, slot).ShouldBe("v2");
            using (var transaction = reopened.BeginTransaction())
            {
                reopened.Update(transaction, pageId, slot, "v3");
                transaction.Commit();
            }

            nextLsn = JournalImage.PageRecords(reopened.CaptureDurable().Journal, (long)pageId)[0].Lsn;
            second = reopened.CaptureDurable();
        }

        using var again = TornStorage.Open(second);

        // Assert
        nextLsn.ShouldBeGreaterThan(pageLsn);
        JournalImage.PageRecords(second.Journal, (long)pageId)[0].Type.ShouldBe(JournalRecordType.FullPageImage);
        again.Read(pageId, slot).ShouldBe("v3");
    }

    // ---------------------------------------------------------------- a commit's durable wait (#1018)

    /// <summary>
    /// A commit asking for durability the journal's handle cannot provide is refused before
    /// anything is journaled, so the caller's rollback agrees with recovery. Refused after the
    /// commit record (before the #1253 review), the rollback restored a base LSN the journal had
    /// moved past, and the next commit on the page made the file set unopenable.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Redo: a commit refused for durability journals nothing, and the next commit on its page recovers")]
    [InlineData(StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.Grouped)]
    public void Commit_DurabilityTheJournalCannotProvide_ShouldJournalNothingAndKeepTheChain(StorageCommitDurability durability)
    {
        // Arrange
        var storage = TornStorage.Create(consistencyChecks: true); // abandoned: a crash
        var (pageId, slot) = storage.Insert("v1");
        storage.CommitDurability = durability;
        storage.GroupCommitWindow = TimeSpan.Zero;
        storage.JournalFaults.RefuseDurableFlush = true;

        // Act: the refused commit is rolled back by its scope; then a commit on the same page.
        long refusedSequence;
        using (var refused = storage.BeginTransaction())
        {
            refusedSequence = refused.Sequence;
            storage.Update(refused, pageId, slot, "refused");
            Should.Throw<NotSupportedException>(() => refused.Commit()).Message.ShouldContain("Nothing was journaled");
            refused.IsActive.ShouldBeTrue();
        }

        storage.JournalFaults.RefuseDurableFlush = false;
        storage.CommitDurability = StorageCommitDurability.Synchronous;
        using (var transaction = storage.BeginTransaction())
        {
            storage.Update(transaction, pageId, slot, "v2");
            transaction.Commit();
        }

        var images = storage.CaptureDurable();
        using var recovered = TornStorage.Open(images);

        // Assert
        JournalImage.Records(images.Journal).ShouldNotContain(record =>
            record.TransactionSequence == refusedSequence
            && (record.Type == JournalRecordType.CommitTransaction || record.Type == JournalRecordType.PageDelta));
        recovered.Read(pageId, slot).ShouldBe("v2");
    }

    /// <summary>
    /// A durable wait that fails after the commit record is appended, with something other than
    /// the storage going offline, ends the bracket committed: recovery redoes it whenever the record
    /// reaches the media, so rolling it back in memory would put the page on a base the journal has
    /// moved past.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Redo: a durable wait that fails after the commit record ends the bracket committed, and its page's chain recovers")]
    public void Commit_DurableWaitFailsAfterTheCommitRecord_ShouldEndCommittedAndRecover()
    {
        // Arrange: the journal's handle stops supporting a durable flush after the commit checked it.
        var storage = TornStorage.Create(consistencyChecks: true); // abandoned: a crash
        var (pageId, slot) = storage.Insert("v1");
        storage.JournalFaults.NextDurableFlushFailure = new NotSupportedException("Injected: the handle stopped supporting a durable flush.");
        var failed = storage.BeginTransaction();
        storage.Update(failed, pageId, slot, "v2");

        // Act
        Should.Throw<NotSupportedException>(() => failed.Commit());
        bool activeAfterFailure = failed.IsActive;
        failed.Dispose();
        string inMemory = storage.Read(pageId, slot);
        using (var transaction = storage.BeginTransaction())
        {
            storage.Update(transaction, pageId, slot, "v3");
            transaction.Commit();
        }

        var images = storage.CaptureDurable();
        using var recovered = TornStorage.Open(images);
        var deltas = JournalImage.PageRecords(images.Journal, (long)pageId).Where(record => record.Type == JournalRecordType.PageDelta).ToArray();

        // Assert: the bracket ended committed and unlocked its page; the next delta chains onto its delta.
        activeAfterFailure.ShouldBeFalse();
        storage.ActiveTransactions.ShouldBe(0);
        inMemory.ShouldBe("v2");
        BaseLsn(deltas[^1]).ShouldBe(deltas[^2].Lsn);
        recovered.Read(pageId, slot).ShouldBe("v3");
    }

    // ---------------------------------------------------------------- helpers

    private static long DataPageLsn(byte[] data, PageId pageId)
        => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan((int)((long)pageId * Page.Size) + Page.LsnFieldOffset));

    internal static long BaseLsn(JournalRecord record) => BinaryPrimitives.ReadInt64LittleEndian(record.Payload.Span);

    internal static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static bool PageHolds(byte[] data, PageId pageId, string text)
    {
        long start = (long)pageId * Page.Size;
        return start + Page.Size <= data.Length
            && data.AsSpan((int)start, Page.Size).IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;
    }

    private static (byte[] Data, byte[] Journal) RecoverAndCapture((byte[] Data, byte[] Journal) images, CrashPoint? point)
    {
        var data = new CrashSimulationStream(images.Data, writeThrough: true, point, "data");
        var journal = new CrashSimulationStream(images.Journal, writeThrough: true, point, "journal");
        using var storage = TornStorage.Open(data, journal, poolCapacity: 2);
        return storage.CaptureDurable();
    }

    /// <summary>
    /// A crashed file set: committed rows over several pages, a rolled-back bracket, an
    /// uncommitted bracket whose pages were stolen, and a small pool that evicted pages along the
    /// way. Returns the committed rows.
    /// </summary>
    private static (byte[] Data, byte[] Journal) CrashedWorkload(out List<((PageId PageId, int SlotIndex) Location, string Text)> expected)
    {
        var storage = TornStorage.Create(poolCapacity: 3); // abandoned: a crash
        expected = [];
        for (int i = 0; i < 12; i++)
        {
            string text = $"row {i} ".PadRight(3000, (char)('a' + i));
            expected.Add((storage.Insert(text), text));
        }

        storage.Checkpoint();
        for (int i = 0; i < 12; i += 3)
        {
            string text = $"updated {i} ".PadRight(3000, 'u');
            using var transaction = storage.BeginTransaction();
            storage.Update(transaction, expected[i].Location.PageId, expected[i].Location.SlotIndex, text);
            transaction.Commit();
            expected[i] = (expected[i].Location, text);
        }

        using (var rolledBack = storage.BeginTransaction())
        {
            storage.Update(rolledBack, expected[1].Location.PageId, expected[1].Location.SlotIndex, "rolled back".PadRight(3000, 'r'));
            rolledBack.Rollback();
        }

        var open = storage.BeginTransaction();
        for (int i = 2; i < 12; i += 4)
        {
            storage.Update(open, expected[i].Location.PageId, expected[i].Location.SlotIndex, "uncommitted".PadRight(3000, 'x'));
        }

        storage.PageManager.FlushAll();
        return storage.CaptureDurable();
    }
}
