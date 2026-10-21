using System;
using System.Buffers.Binary;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StorageAnchorReclamationTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Checkpoint: retiring an anchor cannot make an acknowledged record unrecoverable")]
    [InlineData(0, 1)]
    [InlineData(8, 8)]
    public void Checkpoint_RetiredAnchorWriteTears_ShouldReopenAcknowledgedRecord(int firstSector, int sectors)
    {
        // Arrange: each slot has its own overflow chain, with a record acknowledged before either.
        var point = new CrashPoint();
        var storage = TornStorage.Create(point);
        var (pageId, slot) = storage.Insert("acknowledged");
        long[] writers = [.. Enumerable.Range(1, 1200).Select(i => (long)i)];
        storage.Checkpoint(writers);
        long retired = storage.AnchorChainPages.Single();
        storage.Checkpoint(writers);
        long live = storage.AnchorChainPages.Single();
        live.ShouldNotBe(retired);
        point.DurableSectorOffset = firstSector;
        point.DurableSectors = sectors;
        int before = point.Writes;
        // The fixed implementation issues no retirement write; crash at the slot write instead.
        point.CrashWhen = (stream, operation, offset, count) => stream == "data" && operation == "Write"
            && (offset == retired * Page.Size || (offset < Page.Size && count == StorageHeaderPage.SlotSize));

        // Act: abandon the storage after power loss; its shutdown must not write anything.
        SimulatedPowerLossException.ShouldBeThrownBy(() => storage.Checkpoint([]));
        using var reopened = TornStorage.Open(storage.CaptureDurable());

        // Assert
        point.HasCrashed.ShouldBeTrue();
        reopened.Read(pageId, slot).ShouldBe("acknowledged");
        point.Log.Skip(before).ShouldNotContain($"data Write {retired * Page.Size} {Page.Size}");
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Checkpoint: out-of-order anchor initialization is repaired before free-page verification")]
    [InlineData(false)]
    [InlineData(true)]
    public void Checkpoint_NewAnchorSecondHalfLands_ShouldReopenAcknowledgedRecord(bool reuseReclaimed)
    {
        // Arrange: start the anchor on either zero extension space or a reclaimed data page.
        var point = new CrashPoint();
        var storage = TornStorage.Create(point);
        var (pageId, slot) = storage.Insert("acknowledged");
        long? reused = null;
        if (reuseReclaimed)
        {
            var pages = storage.FillPages(1);
            reused = pages.Single();
            storage.FreeAll(pages);
        }
        storage.Checkpoint();
        point.DurableSectorOffset = 8;
        point.DurableSectors = 8;
        long torn = 0;
        point.CrashWhen = (stream, operation, offset, count) =>
        {
            if (stream != "data" || operation != "Write" || offset <= 0 || count != Page.Size) { return false; }
            torn = offset / Page.Size;
            return true;
        };
        long[] writers = [.. Enumerable.Range(1, 1200).Select(i => (long)i)];

        // Act
        SimulatedPowerLossException.ShouldBeThrownBy(() => storage.Checkpoint(writers));
        using var reopened = TornStorage.Open(storage.CaptureDurable());

        // Assert
        torn.ShouldBeGreaterThan(0);
        if (reuseReclaimed) { torn.ShouldBe(reused!.Value); }
        reopened.Read(pageId, slot).ShouldBe("acknowledged");
        reopened.FreeSpaceMap.IsAllocated((PageId)torn).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Open: both valid header slots retain their anchor chains")]
    public void Open_TwoValidAnchorChains_ShouldProtectBothFromAllocationAndReplay()
    {
        // Arrange
        using var storage = TornStorage.Create();
        var (pageId, slot) = storage.Insert("acknowledged");
        long[] writers = [.. Enumerable.Range(1, 1200).Select(i => (long)i)];
        storage.Checkpoint(writers);
        long older = storage.AnchorChainPages.Single();
        storage.Checkpoint(writers);
        long newest = storage.AnchorChainPages.Single();

        // Act
        using var reopened = TornStorage.Open(storage.CaptureDurable());

        // Assert
        reopened.FreeSpaceMap.IsAllocated((PageId)older).ShouldBeTrue();
        reopened.FreeSpaceMap.IsAllocated((PageId)newest).ShouldBeTrue();
        reopened.Read(pageId, slot).ShouldBe("acknowledged");
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Open: only unchained anchors may have an unverifiable checksum")]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_AnchorChecksumMissing_ShouldTolerateOnlyRetiredPage(bool live)
    {
        // Arrange
        using var storage = TornStorage.Create();
        var (pageId, slot) = storage.Insert("acknowledged");
        long[] writers = [.. Enumerable.Range(1, 1200).Select(i => (long)i)];
        storage.Checkpoint(writers);
        long retired = storage.AnchorChainPages.Single();
        storage.Checkpoint(writers);
        long newest = storage.AnchorChainPages.Single();
        storage.Checkpoint([]);
        var images = storage.CaptureDurable();
        long damaged = live ? newest : retired;
        images.Data.AsSpan(checked((int)(damaged * Page.Size)) + Page.ChecksumFieldOffset, sizeof(uint)).Clear();
        if (live)
        {
            // Make the slot with the damaged chain the newest, so it must verify.
            images.Data.AsSpan(StorageHeaderPage.Slot1Offset, StorageHeaderPage.SlotSize).Clear();
        }

        // Act and assert
        if (live)
        {
            Should.Throw<StorageCorruptionException>(() => TornStorage.Open(images));
        }
        else
        {
            using var reopened = TornStorage.Open(images);
            reopened.Read(pageId, slot).ShouldBe("acknowledged");
            reopened.FreeSpaceMap.IsAllocated((PageId)retired).ShouldBeFalse();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Open: torn non-anchor free and reclaimed pages are refused")]
    [InlineData(PageType.Free, 0, 1)]
    [InlineData(PageType.Free, 8, 8)]
    [InlineData(PageType.Reclaimed, 0, 1)]
    [InlineData(PageType.Reclaimed, 8, 8)]
    public void Open_TornNonAnchorPage_ShouldRefuseCorruption(PageType type, int firstSector, int sectors)
    {
        // Arrange: no journal image remains that can repair this ordinary page.
        using var storage = TornStorage.Create();
        long pageId = storage.FillPages(1).Single();
        storage.Checkpoint();
        var images = storage.CaptureDurable();
        int offset = checked((int)(pageId * Page.Size));
        var point = new CrashPoint { CrashAtWrite = 1, DurableSectorOffset = firstSector, DurableSectors = sectors };
        var replacement = new byte[Page.Size];
        if (firstSector == 0)
        {
            replacement[Page.TypeFieldOffset] = (byte)type;
            if (type == PageType.Reclaimed)
            {
                BinaryPrimitives.WriteInt64LittleEndian(replacement, pageId);
                PageChecksum.Stamp(replacement);
            }
        }
        else
        {
            // Old free/reclaimed header with only a later sector of an unrelated write landing.
            images.Data.AsSpan(offset, Page.Size).Clear();
            images.Data[offset + Page.TypeFieldOffset] = (byte)type;
            if (type == PageType.Reclaimed) { PageChecksum.Stamp(images.Data.AsSpan(offset, Page.Size)); }
            replacement.AsSpan().Fill(42);
        }
        using var data = new CrashSimulationStream(images.Data, writeThrough: true, point, "data");

        // Act
        SimulatedPowerLossException.ShouldBeThrownBy(() => data.Write(replacement, offset));
        var failure = Should.Throw<StorageCorruptionException>(() => TornStorage.Open((data.CaptureDurable(), images.Journal)));

        // Assert
        failure.PageId.ShouldBe((PageId)pageId);
        failure.Code.ShouldBe("COHDBS003");
    }
}
