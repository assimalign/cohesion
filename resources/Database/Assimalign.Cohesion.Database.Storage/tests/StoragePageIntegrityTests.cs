using System;
using System.Buffers.Binary;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StoragePageIntegrityTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Integrity: a real CRC of zero is escaped and remains verifiable")]
    public void Stamp_NonzeroPageWithZeroCrc_ShouldStoreNonzeroChecksumAndVerify()
    {
        // Arrange: fixed CRC-32C zero vector, with a data header and a four-byte suffix.
        var page = new byte[Page.Size];
        page[0] = 1;
        page[Page.TypeFieldOffset] = (byte)PageType.Data;
        new byte[] { 0xB1, 0x44, 0x70, 0x54 }.CopyTo(page, Page.Size - 4);
        Crc32C.Compute(page).ShouldBe(0u);

        // Act
        PageChecksum.Stamp(page);

        // Assert
        BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(Page.ChecksumFieldOffset)).ShouldBe(uint.MaxValue);
        PageChecksum.Verify(page, (PageId)1L);
        page[Page.HeaderSize] ^= 1;
        Should.Throw<StorageCorruptionException>(() => PageChecksum.Verify(page, (PageId)1L)).PageId.ShouldBe((PageId)1L);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Integrity: damaged pages cannot become free space at open")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Open_DamagedPage_ShouldRefuseCorruptionBeforeReusingIt(int damage)
    {
        // Arrange: checkpoint away redo images so recovery cannot repair the damaged page.
        using var original = TornStorage.Create();
        var (pageId, _) = original.Insert(new string('x', 2000));
        original.Checkpoint();
        var images = original.CaptureDurable();
        var page = images.Data.AsSpan(checked((int)((long)pageId * Page.Size)), Page.Size);
        if (damage == 0) { page[..512].Clear(); }
        else if (damage == 1) { page.Slice(Page.ChecksumFieldOffset, sizeof(uint)).Clear(); }
        else
        {
            page[Page.TypeFieldOffset] = (byte)PageType.Free;
            PageChecksum.Stamp(page); // Even a matching checksum cannot legitimize a false free header.
        }

        // Act
        var failure = Should.Throw<StorageCorruptionException>(() => TornStorage.Open(images));

        // Assert
        failure.Code.ShouldBe("COHDBS003");
        failure.PageId.ShouldBe(pageId);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Integrity: wholly zero pages and checksummed reclaimed pages remain reusable")]
    public void Open_ZeroAndReclaimedPages_ShouldRebuildFreeSpaceWithoutHidingLivePages()
    {
        // Arrange
        using var original = TornStorage.Create();
        var reclaimed = original.FillPages(1);
        original.FreeAll(reclaimed);
        original.Checkpoint();
        var images = original.CaptureDurable();
        long zeroPage = images.Data.Length / Page.Size;
        var extended = (Data: (byte[])[.. images.Data, .. new byte[Page.Size]], Journal: images.Journal);

        // Act
        using var reopened = TornStorage.Open(extended);

        // Assert
        reopened.FreeSpaceMap.IsAllocated((PageId)reclaimed[0]).ShouldBeFalse();
        reopened.FreeSpaceMap.IsAllocated((PageId)zeroPage).ShouldBeFalse();
        PageChecksum.TryVerify(new byte[Page.Size], out _, out _).ShouldBeTrue();
    }
}
