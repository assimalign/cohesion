using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

using Assimalign.Cohesion.Database.Storage.Units;

public class StorageStreamTests
{
    [Fact(DisplayName = "Cohesion Test [StorageStream] - WritePage/ReadPage: Should roundtrip page bytes")]
    public void WritePageAndReadPage_ShouldRoundtripPageBytes()
    {
        using var stream = StorageStream.FromInMemory();
        var pageId = (PageId)0L;

        var writeBuffer = new byte[Page.Size];
        for (int i = 0; i < writeBuffer.Length; i++)
        {
            writeBuffer[i] = (byte)(i % 251);
        }

        stream.WritePage(pageId, writeBuffer);

        var readBuffer = new byte[Page.Size];
        stream.ReadPage(pageId, readBuffer);

        Assert.Equal(writeBuffer, readBuffer);
    }

    [Fact(DisplayName = "Cohesion Test [StorageStream] - WritePage: Should seek to page offset")]
    public void WritePage_ShouldSeekToPageOffset()
    {
        using var stream = StorageStream.FromInMemory();

        var page0 = new byte[Page.Size];
        var page1 = new byte[Page.Size];
        page0[0] = 10;
        page1[0] = 20;

        stream.WritePage((PageId)0L, page0);
        stream.WritePage((PageId)1L, page1);

        var read0 = new byte[Page.Size];
        var read1 = new byte[Page.Size];

        stream.ReadPage((PageId)0L, read0);
        stream.ReadPage((PageId)1L, read1);

        Assert.Equal(10, read0[0]);
        Assert.Equal(20, read1[0]);
        Assert.Equal(Page.Size * 2, stream.Length);
    }

    [Fact(DisplayName = "Cohesion Test [StorageStream] - ReadPage: Should throw on truncated stream")]
    public void ReadPage_ShouldThrowOnTruncatedStream()
    {
        using var memoryStream = new MemoryStream(new byte[16]);
        using var stream = new StorageStream(memoryStream);

        var buffer = new byte[Page.Size];

        Assert.Throws<StorageIOException>(() => stream.ReadPage((PageId)0L, buffer));
    }

    [Fact(DisplayName = "Cohesion Test [StorageStream] - WritePageAsync/ReadPageAsync: Should roundtrip page bytes")]
    public async Task WritePageAsyncAndReadPageAsync_ShouldRoundtripPageBytes()
    {
        await using var stream = StorageStream.FromInMemory();
        var pageId = (PageId)2L;

        var writeBuffer = new byte[Page.Size];
        Random.Shared.NextBytes(writeBuffer);

        await stream.WritePageAsync(pageId, writeBuffer);

        var readBuffer = new byte[Page.Size];
        await stream.ReadPageAsync(pageId, readBuffer);

        Assert.Equal(writeBuffer, readBuffer);
    }

    [Fact(DisplayName = "Cohesion Test [StorageStream] - FromFile: Should create and persist file-backed stream")]
    public void FromFile_ShouldCreateAndPersistFileBackedStream()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cohesion-storage-{Guid.NewGuid():N}.db");

        try
        {
            using (var stream = StorageStream.FromFile(path))
            {
                var buffer = new byte[Page.Size];
                buffer[123] = 77;
                stream.WritePage((PageId)0L, buffer);
                stream.Flush();
            }

            using (var stream = StorageStream.FromFile(path))
            {
                var readBuffer = new byte[Page.Size];
                stream.ReadPage((PageId)0L, readBuffer);
                Assert.Equal(77, readBuffer[123]);
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [StorageStream] - SetLength: Should not lose page writes that race the in-memory buffer growing")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SetLength_ConcurrentWithWritePage_ShouldKeepEveryWrite(int seed)
    {
        // Arrange: growing an in-memory stream past its capacity copies the old array into
        // a new one. A page write that lands in the old array after its region was copied
        // is gone, so extension must be serialized with page I/O (#1157 review). The
        // extender grows the stream page by page across several capacity doublings while
        // the writer stamps pages the stream already covers.
        const int initialPages = 16;
        const int finalPages = 4096;
        using var stream = StorageStream.FromInMemory();
        stream.SetLength(initialPages * (long)Page.Size);
        long covered = initialPages;
        int extended = 0;
        var lastStamp = new long[finalPages];

        // Act
        var extender = Task.Run(() =>
        {
            for (long page = initialPages; page < finalPages; page++)
            {
                stream.SetLength((page + 1) * Page.Size);
                Volatile.Write(ref covered, page + 1);
            }

            Volatile.Write(ref extended, 1);
        });

        var writer = Task.Run(() =>
        {
            var random = new Random(seed);
            var buffer = new byte[Page.Size];
            long stamp = 0;
            while (Volatile.Read(ref extended) == 0)
            {
                int page = random.Next((int)Volatile.Read(ref covered));
                stamp++;
                BinaryPrimitives.WriteInt64LittleEndian(buffer, stamp);
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(Page.Size - sizeof(long)), stamp);
                stream.WritePage((PageId)(long)page, buffer);
                lastStamp[page] = stamp;
            }
        });

        await Task.WhenAll(extender, writer);

        // Assert: every page holds the last stamp written to it, at both ends.
        stream.Length.ShouldBe(finalPages * (long)Page.Size);
        var read = new byte[Page.Size];
        var lost = new List<string>();
        for (int page = 0; page < finalPages; page++)
        {
            if (lastStamp[page] == 0)
            {
                continue;
            }

            stream.ReadPage((PageId)(long)page, read);
            long head = BinaryPrimitives.ReadInt64LittleEndian(read);
            long tail = BinaryPrimitives.ReadInt64LittleEndian(read.AsSpan(Page.Size - sizeof(long)));
            if (head != lastStamp[page] || tail != lastStamp[page])
            {
                lost.Add($"page {page} holds {head}/{tail}, last written {lastStamp[page]}");
            }
        }

        lost.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [StorageStream] - SetLength: Should grow a legacy stream and report its length")]
    public void SetLength_OverLegacyStream_ShouldGrowAndReportTheLength()
    {
        // Arrange
        using var memory = new MemoryStream();
        using var stream = new StorageStream(memory);

        // Act
        stream.SetLength(3L * Page.Size);

        // Assert
        stream.Length.ShouldBe(3L * Page.Size);
        memory.Length.ShouldBe(3L * Page.Size);
    }
}
