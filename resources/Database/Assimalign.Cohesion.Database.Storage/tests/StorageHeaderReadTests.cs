using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StorageHeaderReadTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Header read: an unreadable 4 KiB block opens from the other slot")]
    [InlineData(0)]
    [InlineData(1)]
    public void Open_UnreadableHeaderHalf_ShouldRecoverFromOtherSlot(int block)
    {
        // Arrange: both slots carry the same anchor, with durable rows in the journal.
        var source = TornStorage.Create(name: "read-half");
        source.Checkpoint([17, 19]);
        source.FlushHeader();
        source.Insert("durable row");
        var images = source.CaptureDurable();
        source.Dispose();
        using var data = new CrashSimulationStream(images.Data, writeThrough: true);
        using var journal = new CrashSimulationStream(images.Journal, writeThrough: true);
        var faults = new DeviceFaults { UnreadableHeaderBlock = block, HeaderReadPrefixBytes = 113, MaximumDataReadSize = 113 };

        // Act
        using var opened = TornStorage.Open(data, journal, faults: faults);
        bool repairPending = opened.IdentityRepairPending;
        faults.Clear();
        opened.FlushHeader();
        opened.FlushHeader();

        // Assert: identity, checkpoint anchor and journal replay all survived the read error.
        faults.HeaderReadFailures.ShouldBe(1);
        ((string)opened.Name).ShouldBe("read-half");
        opened.CheckpointActiveTransactions.ShouldBe([17L, 19L]);
        opened.ScanText().ShouldBe(["durable row"]);
        repairPending.ShouldBe(block == 0);
        opened.IdentityRepairPending.ShouldBeFalse();
        StorageHeaderPage.VerifyIdentity(StorageHeaderPage.IdentityBlock(opened.CaptureDurable().Data)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Header read: unreadable first half and damaged second slot preserves the I/O failure")]
    public void Open_UnreadableHalfAndNoValidSlot_ShouldThrowWithoutWriting()
    {
        // Arrange
        var source = TornStorage.Create();
        source.FlushHeader();
        var images = source.CaptureDurable();
        source.Dispose();
        images.Data[StorageHeaderPage.Slot1Offset] ^= 0x5A;
        using var data = new CrashSimulationStream(images.Data, writeThrough: true);
        using var journal = new CrashSimulationStream(images.Journal, writeThrough: true);
        var faults = new DeviceFaults { UnreadableHeaderBlock = 0 };

        // Act
        Should.Throw<IOException>(() => TornStorage.Open(data, journal, faults: faults));

        // Assert
        faults.HeaderReadFailures.ShouldBe(1);
        data.CaptureLive().ShouldBe(images.Data);
        journal.CaptureLive().ShouldBe(images.Journal);
    }
}
