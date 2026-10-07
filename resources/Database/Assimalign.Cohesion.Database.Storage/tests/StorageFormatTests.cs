using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Storage format 2 (#1251): the exact format fence on page 0 and on journal frames, the LSN
/// floor a checkpoint persists before it truncates the journal (#1242), and page 0's two
/// alternating header slots, which a torn write cannot make unopenable.
/// </summary>
public sealed class StorageFormatTests
{
    // ---------------------------------------------------------------- format fence

    /// <summary>
    /// Page 0's magic number and format version are read from the raw bytes before any checksum
    /// is verified. A file of another format fails the identity checksum too (format 1 used
    /// another layout and CRC), so checking the checksum first would report it as corrupt.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Format fence: a file of another storage format is refused as a format error, not as corruption")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void Open_OtherFormatVersion_ShouldBeRefusedBeforeAnyChecksum(int version)
    {
        // Arrange: a clean file whose page 0 names another version; its identity checksum and
        // both slots no longer verify, as a format-1 file's would not.
        var images = CleanImages();
        BinaryPrimitives.WriteInt32LittleEndian(images.Data.AsSpan(StorageHeaderPage.FormatVersionOffset), version);
        images.Data.AsSpan(StorageHeaderPage.Slot0Offset, 64).Fill(0xA5);

        // Act
        var refusal = Should.Throw<StorageFormatException>(() => TornStorage.Open(images));

        // Assert
        refusal.FoundVersion.ShouldBe(version);
        refusal.SupportedVersion.ShouldBe(StorageFileHeader.CurrentFormatVersion);
        refusal.Message.ShouldStartWith(StorageFormatException.ErrorCode + ":");
        refusal.Message.ShouldContain($"storage format {version}");
        refusal.Message.ShouldContain($"supports only storage format {StorageFileHeader.CurrentFormatVersion}");
        refusal.Message.ShouldContain("#1152");
        refusal.Message.ShouldContain(version < StorageFileHeader.CurrentFormatVersion ? "does not upgrade" : "newer engine");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Format fence: the current format is 3, and a header names it exactly")]
    public void FileHeader_CurrentFormat_ShouldBeThreeAndExact()
    {
        // Arrange
        var current = new StorageFileHeader { Magic = StorageFileHeader.ExpectedMagic, FormatVersion = 3 };
        var older = new StorageFileHeader { Magic = StorageFileHeader.ExpectedMagic, FormatVersion = 2 };
        var newer = new StorageFileHeader { Magic = StorageFileHeader.ExpectedMagic, FormatVersion = 4 };

        // Act
        bool[] valid = [current.IsValid(), older.IsValid(), newer.IsValid()];

        // Assert
        StorageFileHeader.CurrentFormatVersion.ShouldBe(3);
        valid.ShouldBe([true, false, false]);
    }

    /// <summary>
    /// Storage format 3 (#1253) changed what the journal's page records mean, so a file set of
    /// format 2 — full before- and after-images, journal frame version 3 — is refused at open with
    /// <c>COHDBS001</c> before anything reads its journal or writes either file: the data file and
    /// the journal are left byte for byte as they were, so the engine that wrote them still opens
    /// them.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Format fence: a format-2 file set is refused with COHDBS001 and left byte-identical")]
    public void Open_FormatTwoFileSet_ShouldBeRefusedAndLeftByteIdentical()
    {
        // Arrange: a crashed file set (its journal holds records recovery would replay) whose page
        // 0 names format 2, with a format-2 journal frame (version 3) appended to its journal.
        var storage = TornStorage.Create(); // abandoned: a crash
        storage.Insert("row");
        storage.Log.Flush();
        var images = storage.CaptureDurable();
        BinaryPrimitives.WriteInt32LittleEndian(images.Data.AsSpan(StorageHeaderPage.FormatVersionOffset), 2);
        byte[] journalBytes = [.. images.Journal, .. FrameOfVersion(3, lsn: 1000)];
        var data = new CrashSimulationStream(images.Data, writeThrough: true);
        var journal = new CrashSimulationStream(journalBytes, writeThrough: true);

        // Act
        var refusal = Should.Throw<StorageFormatException>(() => TornStorage.Open(data, journal));
        var frameRefusal = Should.Throw<StorageFormatException>(() => StorageJournal.Create(new MemoryStream(journalBytes)).ReadAll());

        // Assert
        refusal.Message.ShouldStartWith(StorageFormatException.ErrorCode + ":");
        refusal.FoundVersion.ShouldBe(2);
        refusal.SupportedVersion.ShouldBe(3);
        data.CaptureLive().ShouldBe(images.Data);
        data.CaptureDurable().ShouldBe(images.Data);
        journal.CaptureLive().ShouldBe(journalBytes);
        journal.CaptureDurable().ShouldBe(journalBytes);
        frameRefusal.FoundVersion.ShouldBe(3);
        frameRefusal.SupportedVersion.ShouldBe(4);
        frameRefusal.Message.ShouldContain("journal frame format 3");
    }

    /// <summary>
    /// Builds a whole journal frame of another frame version: its length, magic and CRC-32C
    /// verify, so a reader must refuse it rather than stop at it as a torn tail.
    /// </summary>
    private static byte[] FrameOfVersion(byte version, long lsn)
    {
        const int bodyLength = 1 + sizeof(long) + sizeof(long) + 1 + sizeof(long);
        var frame = new byte[sizeof(int) + sizeof(int) + bodyLength + sizeof(uint)];
        BinaryPrimitives.WriteInt32LittleEndian(frame, bodyLength);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(sizeof(int)), 0x324C4157);
        var body = frame.AsSpan(sizeof(int) + sizeof(int), bodyLength);
        body[0] = version;
        BinaryPrimitives.WriteInt64LittleEndian(body[1..], lsn);
        body[17] = (byte)JournalRecordType.CommitTransaction;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(sizeof(int) + sizeof(int) + bodyLength), Crc32C.Compute(body));
        return frame;
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Format fence: a file without the magic number and without a valid header slot is not a storage file")]
    public void Open_WrongMagicAndNoValidSlot_ShouldBeRefusedAsNotAStorageFile()
    {
        // Arrange
        var images = CleanImages();
        images.Data[StorageHeaderPage.MagicOffset] ^= 0xFF;
        images.Data[StorageHeaderPage.Slot0Offset + 20] ^= 0x01;
        images.Data[StorageHeaderPage.Slot1Offset + 20] ^= 0x01;

        // Act
        var refusal = Should.Throw<StorageIOException>(() => TornStorage.Open(images));

        // Assert
        refusal.Message.ShouldContain("magic");
    }

    /// <summary>
    /// The identity block shares slot 0's 4 KiB block. On a drive with 4 KiB physical sectors that
    /// emulates 512-byte ones, power lost during a slot-0 write can destroy the whole physical
    /// sector — page header, identity block, magic and format version included — while slot 1,
    /// the newest generation, is intact. Each slot carries a copy of the identity block, so open
    /// takes it from slot 1, and the next write to slot 0 rewrites the whole block.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Header slots: page 0's destroyed leading 4 KiB block opens from slot 1 and is rewritten with slot 0")]
    [InlineData(0x00)]
    [InlineData(-1)]
    public void Open_LeadingBlockDestroyed_ShouldOpenFromSlotOneAndRestoreTheBlock(int fill)
    {
        // Arrange: a clean file whose newest generation is in slot 1; its first 4 KiB are lost.
        var images = CleanImages();
        var original = images.Data.AsSpan(0, StorageHeaderPage.Slot0Offset).ToArray();
        var block = images.Data.AsSpan(0, StorageHeaderPage.LeadingBlockSize);
        if (fill < 0)
        {
            new Random(1251).NextBytes(block);
        }
        else
        {
            block.Fill((byte)fill);
        }

        // Act
        long generation;
        bool pendingAtOpen;
        (byte[] Data, byte[] Journal) repaired;
        using (var reopened = TornStorage.Open(images))
        {
            generation = reopened.HeaderState.Generation;
            pendingAtOpen = reopened.IdentityRepairPending;
            reopened.ScanText().ShouldBe(["row"]);
            reopened.Checkpoint();
            repaired = reopened.CaptureDurable();
        }

        using var again = TornStorage.Open(repaired);

        // Assert: opened from slot 1; the checkpoint wrote slot 0 and, with it, the block.
        generation.ShouldBe(2L);
        pendingAtOpen.ShouldBeTrue();
        repaired.Data.AsSpan(0, StorageHeaderPage.Slot0Offset).SequenceEqual(original).ShouldBeTrue();
        again.IdentityRepairPending.ShouldBeFalse();
        again.Name.ShouldBe((Name)"torn-harness");
        again.ScanText().ShouldBe(["row"]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Header slots: a damaged identity block opens from the newest slot's copy")]
    public void Open_DamagedIdentityBlock_ShouldOpenFromTheNewestSlotCopy()
    {
        // Arrange
        var created = TornStorage.Create();
        var id = created.Id;
        created.Insert("row");
        created.Dispose();
        var images = created.CaptureDurable();
        images.Data[StorageHeaderPage.IdentityOffset + 50] ^= 0x01; // inside the name

        // Act
        using var reopened = TornStorage.Open(images);

        // Assert
        reopened.Id.ShouldBe(id);
        reopened.Name.ShouldBe((Name)"torn-harness");
        reopened.IdentityRepairPending.ShouldBeTrue();
        reopened.ScanText().ShouldBe(["row"]);
    }

    /// <summary>
    /// The identity block is restored only by a write to slot 0, the slot whose 4 KiB block it
    /// shares: writing it beside a slot-1 write would put the newest generation, in slot 0, at
    /// risk of the very read-modify-write loss the copy guards against.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Header slots: a damaged identity block is rewritten only with slot 0")]
    public void Checkpoint_IdentityRepairPending_ShouldRewriteTheBlockOnlyWithSlotZero()
    {
        // Arrange: the newest generation is in slot 0 (three header writes: 1 in slot 0, 2 in 1, 3 in 0).
        var created = TornStorage.Create();
        created.Insert("row");
        created.Checkpoint();
        created.Dispose();
        var images = created.CaptureDurable();
        images.Data[StorageHeaderPage.IdentityOffset + 50] ^= 0x01;
        var damaged = images.Data.AsSpan(StorageHeaderPage.IdentityOffset, StorageFileHeader.ByteSize).ToArray();

        // Act
        using var reopened = TornStorage.Open(images);
        int slotAtOpen = reopened.HeaderState.Slot;
        reopened.Checkpoint();
        var afterSlotOne = reopened.CaptureDurable().Data.AsSpan(StorageHeaderPage.IdentityOffset, StorageFileHeader.ByteSize).ToArray();
        bool pendingAfterSlotOne = reopened.IdentityRepairPending;
        reopened.Checkpoint();
        var afterSlotZero = reopened.CaptureDurable().Data;

        // Assert
        slotAtOpen.ShouldBe(0);
        afterSlotOne.ShouldBe(damaged);
        pendingAfterSlotOne.ShouldBeTrue();
        reopened.IdentityRepairPending.ShouldBeFalse();
        StorageHeaderPage.VerifyIdentity(StorageHeaderPage.IdentityBlock(afterSlotZero)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Header slots: a damaged identity block with neither slot valid is corruption")]
    public void Open_DamagedIdentityBlockAndBothSlots_ShouldBeCorruption()
    {
        // Arrange
        var images = CleanImages();
        images.Data[StorageHeaderPage.IdentityOffset + 50] ^= 0x01;
        images.Data[StorageHeaderPage.Slot0Offset + 20] ^= 0x01;
        images.Data[StorageHeaderPage.Slot1Offset + 20] ^= 0x01;

        // Act
        var refusal = Should.Throw<StorageCorruptionException>(() => TornStorage.Open(images));

        // Assert
        refusal.Message.ShouldContain("identity block");
    }

    /// <summary>
    /// A frame that passes its checksum was written whole: another frame version is a format
    /// this engine does not read, and stopping the scan there would silently drop it and every
    /// record after it, commit records included.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Format fence: a verified journal frame of another version is a format error, not a torn tail")]
    [InlineData(3)]
    [InlineData(5)]
    public void Open_JournalFrameOfAnotherVersion_ShouldBeAFormatError(byte version)
    {
        // Arrange: a crashed file set whose journal holds a transaction; its second frame is
        // rewritten as another version under a valid CRC-32C.
        var images = CrashedImages();
        var journal = images.Journal;
        int second = FrameOffsets(journal)[1];
        int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(journal.AsSpan(second));
        journal[second + 8] = version;
        BinaryPrimitives.WriteUInt32LittleEndian(
            journal.AsSpan(second + 8 + bodyLength),
            Crc32C.Compute(journal.AsSpan(second + 8, bodyLength)));

        // Act
        var refusal = Should.Throw<StorageFormatException>(() => TornStorage.Open(images));
        var direct = Should.Throw<StorageFormatException>(() => StorageJournal.Create(new MemoryStream(journal)).ReadAll());

        // Assert
        refusal.FoundVersion.ShouldBe(version);
        refusal.SupportedVersion.ShouldBe(4);
        refusal.Message.ShouldContain("Journal frame 2");
        refusal.Message.ShouldContain("not a torn tail");
        refusal.Message.ShouldContain("#1152");
        direct.FoundVersion.ShouldBe(version);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Format fence: a journal frame whose checksum fails stays a torn tail")]
    public void Open_JournalFrameWithABadChecksum_ShouldEndTheScanQuietly()
    {
        // Arrange
        var images = CrashedImages();
        int second = FrameOffsets(images.Journal)[1];
        images.Journal[second + 8] = 5; // a version byte changed without its CRC: torn, not foreign

        // Act
        using var reopened = TornStorage.Open(images);

        // Assert: only the first frame (the begin record) survived; the committed row did not.
        reopened.Log.ReadAll().Count.ShouldBe(1);
        reopened.ScanText().ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- LSN floor

    [Fact(DisplayName = "Cohesion Test [Storage] - LSN floor: every checkpoint persists the last LSN in the header before it truncates")]
    public void Checkpoint_WithCommittedWork_ShouldPersistTheLastLsnAsTheFloor()
    {
        // Arrange
        using var storage = TornStorage.Create();
        storage.Insert("row");
        long lastLsn = storage.Log.LastLsn;

        // Act
        storage.Checkpoint();

        // Assert: the floor is the last LSN before the truncation; the checkpoint record follows it.
        storage.HeaderState.LsnFloor.ShouldBe(lastLsn);
        storage.Log.ReadAll().Single().Lsn.ShouldBe(lastLsn + 1);
    }

    /// <summary>
    /// The crash #1242 names: a checkpoint truncates the journal, then loses its own record.
    /// Without a floor the journal restarts LSNs at 1 while the data pages keep theirs. In storage
    /// format 2 the next after-image of page 1 then got the LSN the stale page already carried,
    /// recovery's exact-LSN skip took it for applied, and the committed update was lost; in
    /// format 3 (#1253) the redo point would restart at zero too, page 1 (above it) would get no
    /// full image, and its delta would have no base to chain onto.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - LSN floor: a journal lost after the truncation does not restart LSNs, and recovery stays correct")]
    public void Open_JournalLostAfterTheTruncation_ShouldResumeAboveTheFloorAndRecoverCorrectly()
    {
        // Arrange: one committed row (begin 1, full page image 2, page delta 3, commit 4), then a
        // checkpoint that truncates the journal and loses power before its record is written.
        var point = new CrashPoint();
        var storage = TornStorage.Create(point); // abandoned after its simulated power loss
        var (pageId, slot) = storage.Insert("old");
        long lastLsn = storage.Log.LastLsn;
        bool truncated = false;
        point.CrashWhen = (stream, operation, _, _) =>
        {
            if (stream == "journal" && operation == "SetLength")
            {
                truncated = true;
                return false;
            }

            return truncated && stream == "journal" && operation == "Write";
        };
        SimulatedPowerLossException.ShouldBeThrownBy(() => storage.Checkpoint());
        var images = storage.CaptureDurable();

        // Act: reopen on the empty journal, commit an update of the same page, and crash before
        // the page reaches the data file.
        long resumedLsn;
        (byte[] Data, byte[] Journal) afterUpdate;
        using (var reopened = TornStorage.Open(images))
        {
            resumedLsn = reopened.Log.LastLsn;
            using (var transaction = reopened.BeginTransaction())
            {
                reopened.Update(transaction, pageId, slot, "new");
                transaction.Commit();
            }

            afterUpdate = reopened.CaptureDurable();
        }

        using var recovered = TornStorage.Open(afterUpdate);
        var records = StorageJournal.Create(new MemoryStream(afterUpdate.Journal)).ReadAll();
        var image = records.Single(record => record.Type == JournalRecordType.FullPageImage);
        var delta = records.Single(record => record.Type == JournalRecordType.PageDelta);

        // Assert: the committed update survives first — that is the loss a restarted LSN causes.
        // The floor is also the redo point after the lost record (#1253): the page carries an LSN
        // at or below it, so the update journals the page's full image before its delta, which
        // chains onto that image.
        recovered.Read(pageId, slot).ShouldBe("new");
        images.Journal.ShouldBeEmpty();
        resumedLsn.ShouldBe(lastLsn);
        image.Lsn.ShouldBeGreaterThan(lastLsn);
        System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(delta.Payload.Span).ShouldBe(image.Lsn);
        recovered.Log.AppendBegin(99).ShouldBeGreaterThan(delta.Lsn);
    }

    // ---------------------------------------------------------------- header slots

    [Fact(DisplayName = "Cohesion Test [Storage] - Header slots: header writes alternate slots with increasing generations")]
    public void HeaderWrites_ConsecutiveCheckpoints_ShouldAlternateSlots()
    {
        // Arrange
        using var storage = TornStorage.Create();
        var created = storage.HeaderState;

        // Act
        storage.Checkpoint([3]);
        var first = storage.HeaderState;
        storage.Checkpoint([4]);
        var second = storage.HeaderState;
        var slots = ReadSlots(storage.CaptureDurable().Data);

        // Assert
        (created.Generation, created.Slot).ShouldBe((1L, 0));
        (first.Generation, first.Slot).ShouldBe((2L, 1));
        (second.Generation, second.Slot).ShouldBe((3L, 0));
        slots[0]!.Generation.ShouldBe(3L);
        slots[1]!.Generation.ShouldBe(2L);
        slots[1]!.AnchorCount.ShouldBe(1);
    }

    /// <summary>
    /// Page 0 carries checkpoint state now, so a write torn by power loss must not make the file
    /// unopenable. A slot write that leaves only a durable prefix of its sectors fails the slot's
    /// checksum, and open reads the other slot; the checkpoint never reached its truncation, so
    /// the journal still holds everything that generation does not.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Header slots: a torn header write opens from the other slot")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(7)]
    public void Open_TornHeaderSlotWrite_ShouldOpenFromTheOtherSlot(int durableSectors)
    {
        // Arrange: generation 2 anchors 5; then more committed work, and a checkpoint whose slot
        // write tears. Its anchor fills the slot, so every sector of the new slot differs from the
        // old one (a sector that is the same either way cannot tell a torn write from a whole one).
        var point = new CrashPoint();
        var storage = TornStorage.Create(point); // abandoned after its simulated power loss
        storage.Insert("before");
        storage.Checkpoint([5]);
        storage.Insert("after");
        long lastLsn = storage.Log.LastLsn;
        int target = 1 - storage.HeaderState.Slot;
        long[] anchor = [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity).Select(i => (long)i + 100)];
        point.DurableSectors = durableSectors;
        point.CrashWhen = (stream, operation, offset, count) =>
            stream == "data" && operation == "Write" && offset == StorageHeaderPage.SlotOffset(target) && count == StorageHeaderPage.SlotSize;

        // Act
        SimulatedPowerLossException.ShouldBeThrownBy(() => storage.Checkpoint(anchor));
        var images = storage.CaptureDurable();
        using var reopened = TornStorage.Open(images);

        // Assert: a slot whose every sector landed is the new generation; any torn one is not.
        bool whole = durableSectors * CrashSimulationStream.SectorSize >= StorageHeaderPage.SlotSize;
        reopened.HeaderState.Generation.ShouldBe(whole ? 3L : 2L);
        reopened.CheckpointActiveTransactions.ShouldBe(whole ? anchor : [5L]);
        reopened.ScanText().ShouldBe(["before", "after"]);
        reopened.Log.LastLsn.ShouldBe(lastLsn);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Header slots: page 0 with neither slot valid is corruption")]
    public void Open_BothSlotsDamaged_ShouldBeCorruption()
    {
        // Arrange
        var images = CleanImages();
        images.Data[StorageHeaderPage.Slot0Offset + 20] ^= 0x01;
        images.Data[StorageHeaderPage.Slot1Offset + 20] ^= 0x01;

        // Act
        var refusal = Should.Throw<StorageCorruptionException>(() => TornStorage.Open(images));

        // Assert
        refusal.Message.ShouldContain("neither header slot");
    }

    /// <summary>
    /// A header write whose slot write was issued and then failed — the write itself reported an
    /// error — may have left that slot on the media as the newest generation. A retry would
    /// rewrite the slot's anchor chain in place under it, so no header write may run again, and
    /// without one no checkpoint can truncate the journal. The storage goes offline (#1268), as a
    /// failed durable flush takes it: the failing checkpoint throws the coded offline error,
    /// every later header write and record change is refused with it, OnOffline is raised once,
    /// and the close writes nothing. The reopen finds a whole generation and the journal as it
    /// was. Before #1268 the storage only refused later header writes and kept accepting record
    /// changes, so an engine's database kept committing into a journal no checkpoint could truncate.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Header slots: a failed slot write takes the storage offline until a reopen")]
    public void Checkpoint_SlotWriteFails_ShouldTakeTheStorageOfflineUntilReopened()
    {
        // Arrange: both slots chain anchor pages (generation 2 in slot 1, 3 in slot 0).
        long[] Anchor(int salt) => [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity + 50).Select(i => (long)(i * 10) + salt)];
        var point = new CrashPoint();
        var storage = TornStorage.Create(point);
        var (pageId, slot) = storage.Insert("before the fault");
        storage.Checkpoint(Anchor(1));
        storage.Checkpoint(Anchor(2));
        int target = 1 - storage.HeaderState.Slot;
        storage.DataFaults.FailWriteAt = StorageHeaderPage.SlotOffset(target);
        var raised = new List<StorageOfflineException>();
        storage.OnOffline = raised.Add;

        // Act
        var error = Should.Throw<StorageOfflineException>(() => storage.Checkpoint(Anchor(3)));
        int writesAfterTheFault = point.Writes;
        var refusal = Should.Throw<StorageOfflineException>(() => storage.Checkpoint(Anchor(4)));
        var flushRefusal = Should.Throw<StorageOfflineException>(() => storage.FlushHeader());
        var insertRefusal = Should.Throw<StorageOfflineException>(() => storage.Insert("after the fault"));
        int writesAfterTheRefusals = point.Writes;
        storage.Dispose();
        int writesAtClose = point.Writes - writesAfterTheRefusals;
        using var reopened = TornStorage.Open(storage.CaptureDurable());

        // Assert: the coded offline error naming the header write, raised once; nothing written
        // after the failure, the close included; the reopen found the previous generation (this
        // failed write wrote nothing) with the journal that describes everything before it.
        error.Message.ShouldStartWith(StorageOfflineException.ErrorCode, Case.Sensitive);
        error.Cause.ShouldBe(StorageOfflineCause.HeaderWrite);
        error.InnerException.ShouldBeOfType<IOException>();
        new[] { refusal, flushRefusal, insertRefusal }.ShouldAllBe(e => e.InnerException == error.InnerException && e.Cause == error.Cause);
        raised.Count.ShouldBe(1);
        storage.HeaderFaulted.ShouldBeTrue();
        storage.IsOffline.ShouldBeTrue();
        writesAfterTheRefusals.ShouldBe(writesAfterTheFault);
        writesAtClose.ShouldBe(0);
        reopened.HeaderState.Generation.ShouldBe(3L);
        reopened.CheckpointActiveTransactions.ShouldBe(Anchor(2));
        reopened.Read(pageId, slot).ShouldBe("before the fault");
        reopened.HeaderFaulted.ShouldBeFalse();
        reopened.IsOffline.ShouldBeFalse();
    }

    /// <summary>
    /// The durable flush after a slot write fails: the slot may be on the media as the newest
    /// generation, and the write-backs before it may have been dropped. The storage goes offline
    /// (#1243), which refuses every later header write and every other write too, and its close
    /// writes nothing; the reopen finds the failed attempt's whole generation (the write landed).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Header slots: a failed flush after the slot write takes the storage offline")]
    public void Checkpoint_SlotFlushFailsAfterTheWrite_ShouldTakeTheStorageOffline()
    {
        // Arrange: both slots chain anchor pages (generation 2 in slot 1, 3 in slot 0).
        long[] Anchor(int salt) => [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity + 50).Select(i => (long)(i * 10) + salt)];
        var point = new CrashPoint();
        var storage = TornStorage.Create(point);
        storage.Checkpoint(Anchor(1));
        storage.Checkpoint(Anchor(2));
        int target = 1 - storage.HeaderState.Slot;
        storage.DataFaults.FailFlushAfterWriteAt = StorageHeaderPage.SlotOffset(target);

        // Act
        var error = Should.Throw<StorageOfflineException>(() => storage.Checkpoint(Anchor(3)));
        int writesAfterTheFault = point.Writes;
        var refusal = Should.Throw<StorageOfflineException>(() => storage.Checkpoint(Anchor(4)));
        var flushRefusal = Should.Throw<StorageOfflineException>(() => storage.FlushHeader());
        var insertRefusal = Should.Throw<StorageOfflineException>(() => storage.Insert("after the fault"));
        storage.Dispose();
        using var reopened = TornStorage.Open(storage.CaptureDurable());

        // Assert
        error.InnerException.ShouldBeOfType<IOException>();
        storage.DataFaults.FailedFlushes.ShouldBe(1);
        storage.HeaderFaulted.ShouldBeTrue();
        new[] { refusal, flushRefusal, insertRefusal }.ShouldAllBe(e => e.InnerException == error.InnerException);
        point.Writes.ShouldBe(writesAfterTheFault);
        reopened.HeaderState.Generation.ShouldBe(4L);
        reopened.CheckpointActiveTransactions.ShouldBe(Anchor(3));
        reopened.HeaderFaulted.ShouldBeFalse();
    }

    /// <summary>
    /// A write that fails before the slot write is issued leaves the slot it targets older than
    /// the newest one on the media, so its chain may be rewritten: only a failure after the slot
    /// write takes the storage offline (#1268), and this one leaves the retry allowed. (A failed
    /// durable flush is different at any point: it takes the storage offline,
    /// <see cref="StorageOfflineTests"/>.)
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Header slots: a write failure before the slot write leaves header writes allowed")]
    public void Checkpoint_WriteFailsBeforeTheSlotWrite_ShouldAllowTheRetry()
    {
        // Arrange: slot 1's chain is the one the third checkpoint rewrites.
        long[] Anchor(int salt) => [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity + 50).Select(i => (long)(i * 10) + salt)];
        using var storage = TornStorage.Create();
        storage.Checkpoint(Anchor(1));
        long chainPage = storage.AnchorChainPages[0];
        storage.Checkpoint(Anchor(2));
        storage.DataFaults.FailWriteAt = chainPage * Page.Size;

        // Act
        Should.Throw<IOException>(() => storage.Checkpoint(Anchor(3)));
        bool faulted = storage.HeaderFaulted;
        storage.Checkpoint(Anchor(4));
        using var reopened = TornStorage.Open(storage.CaptureDurable());

        // Assert
        faulted.ShouldBeFalse();
        storage.IsOffline.ShouldBeFalse();
        reopened.HeaderState.Generation.ShouldBe(4L);
        reopened.CheckpointActiveTransactions.ShouldBe(Anchor(4));
    }

    /// <summary>
    /// The durable flush of the anchor chain fails before the slot write: the chain pages are
    /// recorded clean although the operating system may have dropped them, so a retry could not
    /// rewrite them. The storage goes offline, and the reopen finds the previous generation.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Header slots: a failed flush before the slot write takes the storage offline")]
    public void Checkpoint_FlushFailsBeforeTheSlotWrite_ShouldTakeTheStorageOffline()
    {
        // Arrange: slot 1's chain is the one the third checkpoint rewrites.
        long[] Anchor(int salt) => [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity + 50).Select(i => (long)(i * 10) + salt)];
        var storage = TornStorage.Create();
        storage.Checkpoint(Anchor(1));
        long chainPage = storage.AnchorChainPages[0];
        storage.Checkpoint(Anchor(2));
        storage.DataFaults.FailFlushAfterWriteAt = chainPage * Page.Size;

        // Act
        Should.Throw<StorageOfflineException>(() => storage.Checkpoint(Anchor(3)));
        var retry = Should.Throw<StorageOfflineException>(() => storage.Checkpoint(Anchor(4)));
        storage.Dispose();
        using var reopened = TornStorage.Open(storage.CaptureDurable());

        // Assert
        retry.Message.ShouldStartWith(StorageOfflineException.ErrorCode, Case.Sensitive);
        storage.DataFaults.FailedFlushes.ShouldBe(1);
        storage.HeaderFaulted.ShouldBeFalse();
        reopened.HeaderState.Generation.ShouldBe(3L);
        reopened.CheckpointActiveTransactions.ShouldBe(Anchor(2));
    }

    // ---------------------------------------------------------------- torn data pages

    /// <summary>
    /// Data pages are repaired by journal images: every page a checkpoint writes was changed by
    /// a transaction whose images are still in the journal, which is truncated only after the
    /// data flush.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Torn writes: a data page torn during a checkpoint is repaired from the journal")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(15)]
    public void Open_TornDataPageWrite_ShouldBeRepairedFromTheJournal(int durableSectors)
    {
        // Arrange: page 1 holds a committed row that was never flushed.
        var point = new CrashPoint();
        var storage = TornStorage.Create(point); // abandoned after its simulated power loss
        var (pageId, slot) = storage.Insert("committed and unflushed");
        point.DurableSectors = durableSectors;
        point.CrashWhen = (stream, operation, offset, count) =>
            stream == "data" && operation == "Write" && offset == (long)pageId * Page.Size && count == Page.Size;

        // Act
        SimulatedPowerLossException.ShouldBeThrownBy(() => storage.Checkpoint());
        var images = storage.CaptureDurable();
        var tornPage = images.Data.AsSpan((int)((long)pageId * Page.Size), Page.Size).ToArray();
        using var reopened = TornStorage.Open(images);

        // Assert: the page on the media did not verify (or still held its old image), and
        // recovery rewrote it from the after-image.
        if (durableSectors is > 0 and < Page.Size / CrashSimulationStream.SectorSize)
        {
            PageChecksum.TryVerify(tornPage, out _, out _).ShouldBeFalse();
        }

        reopened.Read(pageId, slot).ShouldBe("committed and unflushed");
    }

    // ---------------------------------------------------------------- torn journal tail

    /// <summary>
    /// A crash can leave a torn frame at the end of the journal. A reopen that defers its
    /// checkpoint (every engine does, to analyze the journal first) appends before it truncates;
    /// those frames must not land behind the torn one, which the next read scan stops at, or a
    /// second crash loses them, acknowledged commits included.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Torn writes: an append after a torn journal tail lands where the next read finds it")]
    public void Append_AfterATornJournalTail_ShouldSurviveTheNextCrash()
    {
        // Arrange: a committed row beside a 3,000-byte one, a checkpoint, then a bracket whose
        // begin record and the page's full image (its first change since the checkpoint, #1253)
        // are still in the journal's append buffer; the drain that writes them tears after one
        // sector, inside the image (#1252: an append is no longer a write of its own).
        var point = new CrashPoint();
        var storage = TornStorage.Create(point); // abandoned after its simulated power loss
        storage.Insert(new string('f', 3000));
        var (pageId, slot) = storage.Insert("v1");
        storage.Checkpoint();
        var torn = storage.BeginTransaction();
        storage.Update(torn, pageId, slot, "v2");
        long drainStart = storage.CaptureDurable().Journal.Length;
        point.DurableSectors = 1;
        point.CrashWhen = (stream, operation, _, count) => stream == "journal" && operation == "Write" && count > 2 * CrashSimulationStream.SectorSize;
        SimulatedPowerLossException.ShouldBeThrownBy(() => storage.Log.Flush());
        var images = storage.CaptureDurable();
        int verifiedFrames = StorageJournal.Create(new MemoryStream(images.Journal)).ReadAll().Count;

        // Act: reopen without the open-time checkpoint, commit, and lose power again.
        (byte[] Data, byte[] Journal) second;
        using (var reopened = TornStorage.Open(images))
        {
            reopened.Read(pageId, slot).ShouldBe("v1");
            using (var transaction = reopened.BeginTransaction())
            {
                reopened.Update(transaction, pageId, slot, "v3");
                transaction.Commit();
            }

            second = reopened.CaptureDurable();
        }

        using var recovered = TornStorage.Open(second);

        // Assert: one sector of the torn drain reached the media — the begin record whole, then
        // the start of the full page image after the verified frames; the new bracket replaced the
        // torn bytes and reads back after the second crash.
        ((long)images.Journal.Length).ShouldBe(drainStart + CrashSimulationStream.SectorSize);
        FrameOffsets(images.Journal)[verifiedFrames].ShouldBeLessThan(images.Journal.Length);
        recovered.Read(pageId, slot).ShouldBe("v3");
        recovered.Log.ReadAll().Count(record => record.Type == JournalRecordType.CommitTransaction).ShouldBe(1);
        recovered.Log.ReadAll().Take(verifiedFrames).Select(record => record.Lsn)
            .ShouldBe(StorageJournal.Create(new MemoryStream(images.Journal)).ReadAll().Select(record => record.Lsn));
    }

    /// <summary>
    /// The journal a checkpoint truncated can hold nothing but the torn start of its checkpoint
    /// record. No frame verifies, so the reopen has no records to checkpoint away; its first
    /// append must still cut the torn bytes off.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Torn writes: a journal holding only a torn checkpoint record keeps later commits readable")]
    [InlineData(true)]
    [InlineData(false)]
    public void Append_AfterAJournalWithNoVerifiedFrame_ShouldSurviveTheNextCrash(bool checkpointOnOpen)
    {
        // Arrange: the checkpoint record (100 writers, more than a sector) tears after the truncation.
        var point = new CrashPoint();
        var storage = TornStorage.Create(point); // abandoned after its simulated power loss
        var (pageId, slot) = storage.Insert("v1");
        long[] writers = [.. Enumerable.Range(1, 100).Select(i => (long)i)];
        bool truncated = false;
        point.DurableSectors = 1;
        point.CrashWhen = (stream, operation, _, _) =>
        {
            if (stream == "journal" && operation == "SetLength")
            {
                truncated = true;
                return false;
            }

            return truncated && stream == "journal" && operation == "Write";
        };
        SimulatedPowerLossException.ShouldBeThrownBy(() => storage.Checkpoint(writers));
        var images = storage.CaptureDurable();

        // Act
        (byte[] Data, byte[] Journal) second;
        using (var reopened = TornStorage.Open(images, checkpointOnOpen: checkpointOnOpen))
        {
            using (var transaction = reopened.BeginTransaction())
            {
                reopened.Update(transaction, pageId, slot, "v2");
                transaction.Commit();
            }

            second = reopened.CaptureDurable();
        }

        using var recovered = TornStorage.Open(second);

        // Assert
        images.Journal.Length.ShouldBe(CrashSimulationStream.SectorSize);
        StorageJournal.Create(new MemoryStream(images.Journal)).ReadAll().ShouldBeEmpty();
        recovered.Read(pageId, slot).ShouldBe("v2");
        recovered.CheckpointActiveTransactions.ShouldBe(writers);
    }

    // ---------------------------------------------------------------- crash at every step

    /// <summary>
    /// #1242's acceptance: power is lost at every write a checkpoint issues — anchor pages, data
    /// pages, the header slot, the journal truncation, the checkpoint record — each torn at
    /// several sector counts, with logical writers in flight whose anchor needs a chain page.
    /// Every crash reopens; the writers stay named (by the journal or by the anchor), LSNs keep
    /// increasing, and the committed data reads back.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint: a crash at every write of a checkpoint keeps writers classified and LSNs increasing")]
    public void Checkpoint_CrashAtEveryWrite_ShouldKeepWritersClassifiedAndLsnsIncreasing()
    {
        // Arrange: more writers than a slot holds, and a dry run that counts the checkpoint's writes.
        long[] writers = [.. Enumerable.Range(1_000, StorageHeaderPage.InlineAnchorCapacity + 40).Select(i => (long)i)];
        var dryPoint = new CrashPoint();
        var dry = Arrange(dryPoint, writers, out _);
        int before = dryPoint.Writes;
        dry.Checkpoint(writers);
        int checkpointWrites = dryPoint.Writes - before;
        dryPoint.Log.Skip(before).ShouldContain(entry => entry.StartsWith("journal SetLength", StringComparison.Ordinal));
        dry.Dispose();

        // Act and assert: one crash per write and sector count, each checked after its reopen.
        int crashes = 0;
        for (int write = 1; write <= checkpointWrites; write++)
        {
            foreach (int sectors in new[] { 0, 1, 7, 15 })
            {
                var point = new CrashPoint();
                var storage = Arrange(point, writers, out long lastLsn); // abandoned after its simulated power loss
                point.CrashAtWrite = point.Writes + write;
                point.DurableSectors = sectors;
                SimulatedPowerLossException.ShouldBeThrownBy(() => storage.Checkpoint(writers), $"write {write}, {sectors} sectors");
                crashes++;

                using var reopened = TornStorage.Open(storage.CaptureDurable());
                var named = reopened.CheckpointActiveTransactions.ToHashSet();
                foreach (var record in reopened.Log.ReadAll())
                {
                    named.Add(record.TransactionSequence);
                    if (record.Type == JournalRecordType.Checkpoint)
                    {
                        for (int offset = 0; offset < record.Payload.Length; offset += sizeof(long))
                        {
                            named.Add(BinaryPrimitives.ReadInt64LittleEndian(record.Payload.Span[offset..]));
                        }
                    }
                }

                string at = $"crash at checkpoint write {write} ({point.Log[point.CrashAtWrite - 1]}), {sectors} sectors";
                writers.ShouldAllBe(writer => named.Contains(writer), at);
                reopened.Log.LastLsn.ShouldBeGreaterThanOrEqualTo(lastLsn, at);
                reopened.Log.AppendBegin(1).ShouldBeGreaterThan(lastLsn, at);
                reopened.ScanText().ShouldBe(["kept-0", "kept-1", "kept-2", "updated!"], at);
                reopened.CountRecords(owner: 7).ShouldBe(3, at);
            }
        }

        crashes.ShouldBe(checkpointWrites * 4);
        checkpointWrites.ShouldBeGreaterThan(4);
    }

    /// <summary>
    /// The state each crash starts from: committed rows on several pages, one of them updated
    /// and unflushed, and the writers' begin records in the journal.
    /// </summary>
    private static TornStorage Arrange(CrashPoint point, long[] writers, out long lastLsn)
    {
        var storage = TornStorage.Create(point, poolCapacity: 4);
        storage.Insert("kept-0");
        storage.FillPages(3);
        storage.Insert("kept-1");
        var (pageId, slot) = storage.Insert("kept-2");
        storage.Insert("original");
        storage.Checkpoint();
        using (var transaction = storage.BeginTransaction())
        {
            storage.Update(transaction, pageId, slot + 1, "updated!");
            transaction.Commit();
        }

        foreach (long writer in writers)
        {
            storage.Log.AppendBegin(writer);
        }

        // A writer's begin record is durable before anything it stamps can reach the data file:
        // the write-ahead gate flushes the journal through a page's LSN before writing the page.
        // Appends are buffered (#1252), so a checkpoint would otherwise lead with the write that
        // drains these records, and a crash in it would lose writers that never stamped a page.
        storage.Log.Flush(forceDurable: true);

        lastLsn = storage.Log.LastLsn;
        return storage;
    }

    // ---------------------------------------------------------------- helpers

    private static (byte[] Data, byte[] Journal) CleanImages()
    {
        var storage = TornStorage.Create();
        storage.Insert("row");
        storage.Dispose();
        return storage.CaptureDurable();
    }

    /// <summary>Images of a file set that lost power right after a committed insert.</summary>
    private static (byte[] Data, byte[] Journal) CrashedImages()
    {
        var storage = TornStorage.Create();
        storage.Insert("row");
        return storage.CaptureDurable();
    }

    private static int[] FrameOffsets(byte[] journal)
    {
        var offsets = new System.Collections.Generic.List<int>();
        int offset = 0;
        while (offset + 8 <= journal.Length)
        {
            offsets.Add(offset);
            offset += 8 + BinaryPrimitives.ReadInt32LittleEndian(journal.AsSpan(offset)) + sizeof(uint);
        }

        return [.. offsets];
    }

    private static StorageHeaderSlot?[] ReadSlots(byte[] data)
    {
        var slots = new StorageHeaderSlot?[2];
        for (int slot = 0; slot < 2; slot++)
        {
            StorageHeaderPage.TryReadSlot(data.AsSpan(StorageHeaderPage.SlotOffset(slot), StorageHeaderPage.SlotSize), out slots[slot]);
        }

        return slots;
    }
}
