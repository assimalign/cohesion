using System;
using System.Buffers.Binary;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// The byte-run encoding of the journal's page records (storage format 3, #1253): full page
/// images are a page's non-zero runs (its free gap elided), page deltas the runs that differ from
/// the pre-image, and neither ever carries the page LSN or checksum.
/// </summary>
public sealed class PageImageCodecTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Page codec: an image round-trips through its runs for random and sparse pages")]
    public void EncodeImage_RandomAndSparsePages_ShouldRoundTrip()
    {
        var random = new Random(1253);
        var buffer = new byte[PageImageCodec.MaximumRunsLength];
        for (int round = 0; round < 500; round++)
        {
            // Arrange: a page whose bytes are zero with a probability that varies by round.
            var page = new byte[Page.Size];
            double density = random.NextDouble();
            for (int i = 0; i < page.Length; i++)
            {
                page[i] = random.NextDouble() < density ? (byte)random.Next(1, 256) : (byte)0;
            }

            // Act
            int length = PageImageCodec.EncodeImage(page, buffer);
            var restored = new byte[Page.Size];
            new Random(round).NextBytes(restored);
            string? problem = PageImageCodec.TryApplyImage(buffer.AsSpan(0, length), restored);

            // Assert: equal outside the LSN and checksum, which the image never carries.
            problem.ShouldBeNull();
            length.ShouldBeLessThanOrEqualTo(PageImageCodec.MaximumRunsLength);
            PageImageCodec.FirstDifference(page, restored).ShouldBe(-1, $"round {round}");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page codec: a delta applied to the pre-image rebuilds the page, and is empty for an unchanged page")]
    public void EncodeRuns_RandomChanges_ShouldRebuildTheChangedPage()
    {
        var random = new Random(42);
        var buffer = new byte[PageImageCodec.MaximumRunsLength];
        for (int round = 0; round < 500; round++)
        {
            // Arrange: a pre-image and a copy changed in a few random ranges.
            var before = new byte[Page.Size];
            random.NextBytes(before);
            var after = (byte[])before.Clone();
            int changes = random.Next(0, 12);
            for (int c = 0; c < changes; c++)
            {
                int start = random.Next(0, Page.Size);
                int length = Math.Min(random.Next(1, 600), Page.Size - start);
                random.NextBytes(after.AsSpan(start, length));
            }

            // Act
            int runs = PageImageCodec.EncodeRuns(before, after, buffer);
            var rebuilt = (byte[])before.Clone();
            string? problem = PageImageCodec.TryApplyRuns(buffer.AsSpan(0, runs), rebuilt);

            // Assert
            problem.ShouldBeNull();
            PageImageCodec.FirstDifference(after, rebuilt).ShouldBe(-1, $"round {round}");
            if (PageImageCodec.FirstDifference(before, after) < 0)
            {
                runs.ShouldBe(0);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page codec: the LSN and checksum never travel in a run")]
    public void EncodeRuns_ChangedLsnAndChecksumOnly_ShouldEncodeNothing()
    {
        // Arrange
        var before = new byte[Page.Size];
        new Random(3).NextBytes(before);
        var after = (byte[])before.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(after.AsSpan(Page.LsnFieldOffset), 12345);
        BinaryPrimitives.WriteUInt32LittleEndian(after.AsSpan(Page.ChecksumFieldOffset), 0xDEADBEEF);
        var buffer = new byte[PageImageCodec.MaximumRunsLength];

        // Act
        int runs = PageImageCodec.EncodeRuns(before, after, buffer);
        after[7] ^= 0xFF;
        after[20] ^= 0xFF;
        int edges = PageImageCodec.EncodeRuns(before, after, buffer);

        // Assert: the bytes either side of the masked fields still travel, each as its own run.
        runs.ShouldBe(0);
        edges.ShouldBe(2 * (PageImageCodec.RunHeaderSize + 1));
        BinaryPrimitives.ReadUInt16LittleEndian(buffer).ShouldBe((ushort)7);
        BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(PageImageCodec.RunHeaderSize + 1)).ShouldBe((ushort)20);
    }

    /// <summary>
    /// A B-tree leaf insert writes an entry at the data end and shifts the directory tail by one
    /// two-byte slot. Adjacent offsets often share their high byte, so a byte-exact diff would
    /// split the shift into many runs; the block diff keeps it one run.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Page codec: a directory shift with equal high bytes stays one run")]
    public void EncodeRuns_DirectoryShift_ShouldStayOneRun()
    {
        // Arrange: a directory of 200 two-byte offsets descending from 0x1F00, then one more slot
        // inserted at position 10.
        var before = new byte[Page.Size];
        const int directory = 128;
        for (int i = 0; i < 200; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(before.AsSpan(directory + (2 * i)), (ushort)(0x1F00 - (8 * i)));
        }

        var after = (byte[])before.Clone();
        before.AsSpan(directory + 20, 380).CopyTo(after.AsSpan(directory + 22));
        BinaryPrimitives.WriteUInt16LittleEndian(after.AsSpan(directory + 20), 0x0F00);
        var buffer = new byte[PageImageCodec.MaximumRunsLength];

        // Act
        int length = PageImageCodec.EncodeRuns(before, after, buffer);

        // Assert: one run from the inserted slot to the end of the shifted directory.
        BinaryPrimitives.ReadUInt16LittleEndian(buffer).ShouldBe((ushort)(directory + 20));
        length.ShouldBe(PageImageCodec.RunHeaderSize + BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(2)));
        length.ShouldBeLessThan(420);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page codec: a slotted page's free gap is not journaled")]
    public unsafe void EncodeImage_HalfFullSlottedPage_ShouldElideTheFreeGap()
    {
        // Arrange: a slotted page holding 3,000 bytes of records.
        var buffer = GC.AllocateArray<byte>(Page.Size, pinned: true);
        fixed (byte* pointer = buffer)
        {
            var page = new Page(pointer) { Id = 5, Type = PageType.Data };
            var slotted = new SlottedPage(page);
            slotted.Initialize();
            var record = new byte[300];
            new Random(9).NextBytes(record);
            for (int i = 0; i < 10; i++)
            {
                slotted.InsertSlot(record);
            }
        }

        var runs = new byte[PageImageCodec.MaximumRunsLength];

        // Act
        int length = PageImageCodec.EncodeImage(buffer, runs);

        // Assert: the records, the slot directory and the header — not the 5 KiB gap.
        length.ShouldBeGreaterThan(3000);
        length.ShouldBeLessThan(3300);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page codec: the longest encoding stays within its bound")]
    public void EncodeImage_PageWithoutZeros_ShouldFitTheMaximum()
    {
        // Arrange
        var page = new byte[Page.Size];
        page.AsSpan().Fill(0xFF);
        var buffer = new byte[PageImageCodec.MaximumRunsLength];

        // Act
        int length = PageImageCodec.EncodeImage(page, buffer);

        // Assert: three runs (0-7, 20-31, 32 to the end) and every byte outside the masked twelve.
        length.ShouldBe(PageImageCodec.MaximumRunsLength);
        length.ShouldBe(Page.Size);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Page codec: malformed runs are refused, never applied past the page")]
    [InlineData("cut-header")]
    [InlineData("empty")]
    [InlineData("overlap")]
    [InlineData("past-end")]
    [InlineData("masked")]
    [InlineData("short-payload")]
    public void TryApplyRuns_MalformedRuns_ShouldBeRefused(string kind)
    {
        // Arrange
        byte[] runs = kind switch
        {
            "cut-header" => [1, 0, 2],
            "empty" => Run(100, 0, []),
            "overlap" => [.. Run(100, 4, [1, 2, 3, 4]), .. Run(102, 2, [5, 6])],
            "past-end" => Run(Page.Size - 2, 4, [1, 2, 3, 4]),
            "masked" => Run(6, 4, [1, 2, 3, 4]),
            _ => Run(100, 10, [1, 2, 3]),
        };
        var page = new byte[Page.Size];

        // Act
        string? problem = PageImageCodec.TryApplyRuns(runs, page);

        // Assert
        problem.ShouldNotBeNull();
    }

    private static byte[] Run(int offset, int length, byte[] bytes)
    {
        var run = new byte[PageImageCodec.RunHeaderSize + bytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(run, (ushort)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(run.AsSpan(2), (ushort)length);
        bytes.CopyTo(run, PageImageCodec.RunHeaderSize);
        return run;
    }
}
