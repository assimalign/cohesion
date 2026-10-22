using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed class StorageInPlaceReadTests
{
    private const ulong Owner = 1365;
    private const int Length = 64;
    private readonly ITestOutputHelper _output;

    public StorageInPlaceReadTests(ITestOutputHelper output) => _output = output;

    [Theory(DisplayName = "Cohesion Test [Storage] - In-place writes: unlatched reads wait for a whole record (#1365)")]
    [InlineData("direct")]
    [InlineData("try")]
    [InlineData("scan")]
    public void OpenPageForWrite_HalfWrittenRecord_ShouldMakeUnlatchedReadersWait(string readKind)
    {
        // Arrange
        using var storage = TornStorage.Create();
        var location = Seed(storage);

        // Act: the reader either finishes or reports its actual sequence wait before
        // the writer completes the second half. There is no scheduling-delay decision.
        var result = ReadDuringWrite(storage, location, readKind);

        // Assert
        _output.WriteLine($"Read {readKind}: signal={result.Signal}; bytes={Convert.ToHexString(result.Bytes)}; unchanged={result.Unchanged}.");
        result.Signal.ShouldBe(1, "the reader must wait rather than publish the half-written copy");
        result.Bytes.Length.ShouldBe(Length);
        result.Bytes.ShouldAllBe(value => value == 0x22);
        result.Unchanged.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - In-place writes: record changes invalidate an earlier frame read (#1365)")]
    [InlineData("replace")]
    [InlineData("shrink")]
    [InlineData("relocate")]
    [InlineData("delete")]
    public void RecordMutation_ConfirmedAfterAnEarlierRead_ShouldInvalidateThatRead(string operation)
    {
        // Arrange
        using var storage = TornStorage.Create();
        var location = Seed(storage);
        using var pin = storage.PageManager.GetPage(location.PageId);
        var before = pin.Entry.BeginRead();

        // Act
        using (var transaction = storage.BeginTransaction())
        {
            if (operation == "delete") { storage.Delete(transaction, location.PageId, location.SlotIndex); }
            else
            {
                int size = operation == "shrink" ? Length / 2 : operation == "relocate" ? Length * 2 : Length;
                storage.Update(transaction, location.PageId, location.SlotIndex, Enumerable.Repeat((byte)0x22, size).ToArray());
            }
            transaction.Commit();
        }

        // Assert
        pin.Entry.IsUnchangedSince(before).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Allocation: an unpublished identity cannot be read past the stream (#1365)")]
    public void AllocatePage_BeforeItsFrameIsPinned_ShouldKeepTheIdentityUnpublished()
    {
        // Arrange
        using var storage = TornStorage.Create();
        Exception? failure = null;
        bool? allocated = null;
        bool? found = null;
        storage.PageManager.PageReserved = page =>
        {
            allocated = storage.FreeSpaceMap.IsAllocated(page);
            try { found = storage.TryReadRecord(page, 0, out _); }
            catch (Exception exception) { failure = exception; }
        };

        // Act
        using var handle = storage.PageManager.AllocatePage(PageType.Data);
        storage.PageManager.PageReserved = null;

        // Assert: the same-thread rendezvous enters the exact reserve-to-pin window.
        failure.ShouldBeNull();
        allocated.ShouldBe(false);
        found.ShouldBe(false);
        storage.FreeSpaceMap.IsAllocated(handle.Id).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - In-place writes: closing a nested handle keeps the outer writer active (#1365)")]
    public void OpenPageForWrite_NestedHandleDisposed_ShouldKeepReadersWaiting()
    {
        using var storage = TornStorage.Create();
        var location = Seed(storage);
        var result = ReadDuringWrite(storage, location, "try", nested: true);
        result.Signal.ShouldBe(1);
        result.Bytes.ShouldAllBe(value => value == 0x22);
        result.Unchanged.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - In-place writes: size changes cannot publish a truncated copy (#1365)")]
    [InlineData("direct")]
    [InlineData("try")]
    [InlineData("scan")]
    public void UpdateSlot_ShrinkThenGrow_ShouldReturnOneCompleteSize(string readKind)
    {
        using var storage = TornStorage.Create();
        var location = Seed(storage);
        var result = ReadDuringWrite(storage, location, readKind, resize: true);
        result.Signal.ShouldBe(1);
        result.Bytes.Length.ShouldBe(Length);
        result.Bytes.ShouldAllBe(value => value == 0x22);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - In-place writes: completed brackets cannot strand readers or release a later writer (#1365)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedTransaction_PinnedOldHandle_ShouldNotStrandOrReleaseAnotherWriter(bool rollback)
    {
        using var storage = TornStorage.Create();
        var location = Seed(storage);
        using var transaction = storage.BeginTransaction();
        using var old = storage.OpenPageForWrite(transaction, location.PageId);
        if (rollback) { transaction.Rollback(); }
        else { transaction.Commit(); }

        // Completion, rather than eventual disposal of a pin, ends the mutable
        // scope. Thirty seconds is only a hang guard.
        var bytes = await DedicatedTestTask.Run(() => storage.ReadBytes(location.PageId, location.SlotIndex))
            .WaitAsync(TimeSpan.FromSeconds(30));
        bytes.ShouldAllBe(value => value == 0x11);
        var result = ReadDuringWrite(storage, location, "try", afterWriter: old.Dispose);
        result.Signal.ShouldBe(1, "an old handle must not close the new writer's epoch");
        result.Bytes.ShouldAllBe(value => value == 0x22);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - In-place writes: the writer can read its own changes and dispose across threads (#1365)")]
    public async Task OpenPageForWrite_OwnReadAndCrossThreadDispose_ShouldKeepAStableSequence()
    {
        using var storage = TornStorage.Create();
        var location = Seed(storage);
        using var transaction = storage.BeginTransaction();
        using var writer = storage.OpenPageForWrite(transaction, location.PageId);
        var sequence = writer.Entry.BeginRead();
        storage.ReadBytes(location.PageId, location.SlotIndex).ShouldAllBe(value => value == 0x11);
        writer.Entry.IsUnchangedSince(sequence).ShouldBeTrue();
        await DedicatedTestTask.Run(writer.Dispose).WaitAsync(TimeSpan.FromSeconds(30));
        writer.Entry.IsUnchangedSince(sequence).ShouldBeFalse();
        writer.Dispose();
        transaction.Commit();
        storage.TryReadRecord(location.PageId, location.SlotIndex, Owner, out var bytes).ShouldBeTrue();
        bytes.Length.ShouldBe(Length);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - In-place writes: a writer after the copy forces a retry (#1365)")]
    [InlineData("direct")]
    [InlineData("try")]
    [InlineData("scan")]
    public void ReadRecord_WriteBetweenCopyAndConfirmation_ShouldRetry(string readKind)
    {
        using var storage = TornStorage.Create();
        var location = Seed(storage);
        using var pin = storage.PageManager.GetPage(location.PageId);
        int copies = 0;
        pin.Entry.ReadCopied = () =>
        {
            if (++copies != 1) { return; }
            // The exact copy-to-confirm window, independent of scheduling speed.
            using var transaction = storage.BeginTransaction();
            storage.Update(transaction, location.PageId, location.SlotIndex,
                Enumerable.Repeat((byte)0x22, Length / 2).ToArray());
            transaction.Commit();
        };
        byte[] bytes;
        try
        {
            if (readKind == "direct") { bytes = storage.ReadBytes(location.PageId, location.SlotIndex); }
            else if (readKind == "try")
            {
                storage.TryReadRecord(location.PageId, location.SlotIndex, Owner, out var data).ShouldBeTrue();
                bytes = data.ToArray();
            }
            else
            {
                using var scan = storage.GetUnitIterator(Owner);
                scan.MoveNext().ShouldBeTrue();
                bytes = scan.Current.Data.ToArray();
            }
        }
        finally { pin.Entry.ReadCopied = null; }
        copies.ShouldBe(2);
        bytes.Length.ShouldBe(Length / 2);
        bytes.ShouldAllBe(value => value == 0x22);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - In-place writes: epoch wrap skips a still-pinned retired handle (#1365)")]
    public async Task BeginWrite_WrappedEpoch_ShouldSkipAStillPinnedRetiredEpoch()
    {
        using var storage = TornStorage.Create();
        var location = Seed(storage);
        using var first = storage.BeginTransaction();
        using var old = storage.OpenPageForWrite(first, location.PageId);
        int retired = WriteEpoch(old.Entry);
        first.Commit();
        await DedicatedTestTask.Run(() => storage.ReadBytes(location.PageId, location.SlotIndex))
            .WaitAsync(TimeSpan.FromSeconds(30));

        // Simulate a full token cycle while that old handle remains pinned.
        // The 64-bit read sequence is separate from the handle's compact token.
        WriteEpoch(old.Entry) = unchecked(retired - 1);
        using var second = storage.BeginTransaction();
        using var current = storage.OpenPageForWrite(second, location.PageId);
        WriteEpoch(current.Entry).ShouldNotBe(retired);
        long sequence = current.Entry.BeginRead();
        old.Dispose();
        current.Entry.IsUnchangedSince(sequence).ShouldBeTrue();
        current.Dispose();
        current.Entry.IsUnchangedSince(sequence).ShouldBeFalse();
        second.Commit();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_writeEpoch")]
    private static extern ref int WriteEpoch(StorageBufferPool.BufferEntry entry);

    [Fact(DisplayName = "Cohesion Test [Storage] - In-place writes: an interrupted overwrite wait releases its pin (#1365)")]
    public async Task PinForOverwrite_InterruptedWriterWait_ShouldReleaseTheNewPin()
    {
        using var storage = TornStorage.Create();
        using var held = storage.PageManager.AllocatePage(PageType.Data);
        await DedicatedTestTask.Run(() =>
        {
            // Interrupt is pending before the first wait: no scheduling delay is
            // needed to reach the private writer gate held by the raw allocation.
            Thread.CurrentThread.Interrupt();
            Should.Throw<ThreadInterruptedException>(() => storage.PageManager.PinForOverwrite(held.Id));
        }).WaitAsync(TimeSpan.FromSeconds(30));
        held.PinCount.ShouldBe(1, "the refused overwrite owns no pin after its wait fails");
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - In-place writes: an own-thread read cannot accept an overlapping restore (#1365)")]
    [InlineData("direct")]
    [InlineData("try")]
    [InlineData("scan")]
    public void OwnRead_RestoreStartsAfterPermission_ShouldRetryTheOriginalSequence(string readKind)
    {
        using var storage = TornStorage.Create();
        var location = Seed(storage);
        var result = ReadOwnDuringRestore(storage, location, readKind);
        result.Signal.ShouldBe(1, "the overlapping copy must retry and observe the active restore");
        result.Bytes.Length.ShouldBe(Length);
        result.Bytes.ShouldAllBe(value => value == 0x22);
    }

    private static unsafe (int Signal, byte[] Bytes) ReadOwnDuringRestore(
        TornStorage storage, (PageId PageId, int SlotIndex) location, string readKind)
    {
        using var transaction = storage.BeginTransaction();
        using var writer = storage.OpenPageForWrite(transaction, location.PageId);
        using var entered = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var waiting = new ManualResetEventSlim();
        var page = writer.Page;
        var slot = (PageSlot*)(page.Pointer + Page.Size - (location.SlotIndex + 1) * sizeof(PageSlot));
        int offset = slot->Offset;
        var entry = writer.Entry;
        Task<int>? restoring = null;
        entry.ReadWaitStarted = waiting.Set;
        entry.OwnReadAccepted = () =>
        {
            entry.OwnReadAccepted = null;
            restoring = DedicatedTestTask.Run(() => RestoreInHalves(entry, transaction, offset, entered, finished, waiting));
            entered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue("restore rendezvous hang guard");
        };
        byte[] bytes;
        try
        {
            if (readKind == "direct") { bytes = storage.ReadBytes(location.PageId, location.SlotIndex); }
            else if (readKind == "try")
            {
                storage.TryReadRecord(location.PageId, location.SlotIndex, Owner, out var data).ShouldBeTrue();
                bytes = data.ToArray();
            }
            else
            {
                using var scan = storage.GetUnitIterator(Owner);
                scan.MoveNext().ShouldBeTrue();
                bytes = scan.Current.Data.ToArray();
            }
        }
        finally
        {
            finished.Set();
            entry.OwnReadAccepted = null;
            entry.ReadWaitStarted = null;
        }
        int signal = restoring!.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        return (signal, bytes);
    }

    private static unsafe int RestoreInHalves(StorageBufferPool.BufferEntry entry, StorageTransaction transaction,
        int offset, ManualResetEventSlim entered, ManualResetEventSlim finished, ManualResetEventSlim waiting)
    {
        entry.BeginRestore(transaction);
        try
        {
            var record = new Span<byte>(entry.Page.Pointer + offset, Length);
            record[..(Length / 2)].Fill(0x22);
            entered.Set();
            // Observed completion or frame wait, with a hang guard only.
            int signal = WaitHandle.WaitAny([finished.WaitHandle, waiting.WaitHandle], TimeSpan.FromSeconds(30));
            record[(Length / 2)..].Fill(0x22);
            return signal;
        }
        finally { entry.EndRestore(); }
    }

    private static (PageId PageId, int SlotIndex) Seed(TornStorage storage)
    {
        using var transaction = storage.BeginTransaction();
        var location = storage.Insert(transaction, Owner, Enumerable.Repeat((byte)0x11, Length).ToArray());
        transaction.Commit();
        return location;
    }

    private static unsafe (int Signal, byte[] Bytes, bool Unchanged) ReadDuringWrite(
        TornStorage storage, (PageId PageId, int SlotIndex) location, string readKind,
        bool nested = false, Action? afterWriter = null, bool resize = false)
    {
        using var pin = storage.PageManager.GetPage(location.PageId);
        var before = pin.Entry.BeginRead();
        using var waiting = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        pin.Entry.ReadWaitStarted = waiting.Set;
        try
        {
            using var transaction = storage.BeginTransaction();
            using var writer = storage.OpenPageForWrite(transaction, location.PageId);
            if (nested) { storage.OpenPageForWrite(transaction, location.PageId).Dispose(); }
            afterWriter?.Invoke();
            var page = writer.Page;
            var slot = (PageSlot*)(page.Pointer + Page.Size - (location.SlotIndex + 1) * sizeof(PageSlot));
            var record = new Span<byte>(page.Pointer + slot->Offset, slot->Length);
            if (resize)
            {
                storage.Update(transaction, location.PageId, location.SlotIndex,
                    Enumerable.Repeat((byte)0x22, Length / 2).ToArray());
            }
            else { record[..(Length / 2)].Fill(0x22); }
            writer.MarkDirty();
            var reader = DedicatedTestTask.Run(() =>
            {
                try
                {
                    if (readKind == "direct") { return storage.ReadBytes(location.PageId, location.SlotIndex); }
                    if (readKind == "try")
                    {
                        storage.TryReadRecord(location.PageId, location.SlotIndex, Owner, out var bytes).ShouldBeTrue();
                        return bytes.ToArray();
                    }
                    using var scan = storage.GetUnitIterator(Owner);
                    scan.MoveNext().ShouldBeTrue();
                    return scan.Current.Data.ToArray();
                }
                finally { finished.Set(); }
            });
            // Thirty seconds is a hang guard, never a duration/work threshold.
            int signal = WaitHandle.WaitAny([finished.WaitHandle, waiting.WaitHandle], TimeSpan.FromSeconds(30));
            if (resize)
            {
                storage.Update(transaction, location.PageId, location.SlotIndex,
                    Enumerable.Repeat((byte)0x22, Length).ToArray());
            }
            else { record[(Length / 2)..].Fill(0x22); }
            writer.MarkDirty();
            writer.Dispose();
            transaction.Commit();
            var bytes = reader.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            return (signal, bytes, pin.Entry.IsUnchangedSince(before));
        }
        finally { pin.Entry.ReadWaitStarted = null; }
    }
}
