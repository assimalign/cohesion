using System;
using System.IO;
using System.Threading.Tasks;
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
}
