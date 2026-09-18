using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.FileSystem.Tests;

public class InMemoryFileHandleTests
{
    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - Read and Write: Should use independent offsets and extend the file")]
    public void ReadWrite_NonSequentialOffsets_ShouldPreserveContentAndLength()
    {
        using var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("pages.bin");
        using var handle = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        handle.Write(new byte[] { 8, 9, 10 }, 8);
        handle.Write(new byte[] { 1, 2, 3 }, 1);
        handle.Write(new byte[] { 5, 6 }, 5);

        handle.Length.ShouldBe(11);
        file.Size.Length.ShouldBe(11);
        var buffer = new byte[3];
        handle.Read(buffer, 8).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 8, 9, 10 });
        handle.Read(buffer, 1).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 1, 2, 3 });
        var all = new byte[11];
        handle.Read(all, 0).ShouldBe(11);
        all.ShouldBe(new byte[] { 0, 1, 2, 3, 0, 5, 6, 0, 8, 9, 10 });
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - Read: Should return only available bytes at end of file")]
    public void Read_NearEndOfFile_ShouldReturnShortRead()
    {
        using var fileSystem = CreateFileSystem();
        using var handle = fileSystem.CreateFile("pages.bin").OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Write(new byte[] { 1, 2, 3, 4 }, 0);
        var buffer = new byte[4];

        handle.Read(buffer, 3).ShouldBe(1);
        buffer.ShouldBe(new byte[] { 4, 0, 0, 0 });
        handle.Read(buffer, 4).ShouldBe(0);
        handle.Read(buffer, long.MaxValue).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - SetLength: Should truncate and zero fill extension")]
    public void SetLength_TruncateAndExtend_ShouldUpdateContentAndLength()
    {
        using var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("pages.bin");
        using var handle = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Write(new byte[] { 1, 2, 3, 4 }, 0);

        handle.SetLength(2);
        handle.Length.ShouldBe(2);
        file.Size.Length.ShouldBe(2);
        handle.SetLength(6);

        handle.Length.ShouldBe(6);
        file.Size.Length.ShouldBe(6);
        var buffer = new byte[6];
        handle.Read(buffer, 0).ShouldBe(6);
        buffer.ShouldBe(new byte[] { 1, 2, 0, 0, 0, 0 });
        fileSystem.SpaceUsed.Length.ShouldBe(6);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - Flush: Should reject durable flush explicitly")]
    public async Task Flush_DurableRequested_ShouldThrowNotSupportedException()
    {
        using var fileSystem = CreateFileSystem();
        await using var handle = fileSystem.CreateFile("pages.bin").OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Write(new byte[] { 1, 2, 3 }, 0);

        handle.SupportsDurableFlush.ShouldBeFalse();
        Should.NotThrow(() => handle.Flush(durable: false));
        await handle.FlushAsync(durable: false, CancellationToken.None);
        Should.Throw<NotSupportedException>(() => handle.Flush(durable: true));
        await Should.ThrowAsync<NotSupportedException>(async () => await handle.FlushAsync(durable: true, CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - Async: Should preserve positional reads writes and lengths")]
    public async Task ReadWriteAsync_NonSequentialOffsets_ShouldMatchSynchronousOperations()
    {
        using var fileSystem = CreateFileSystem();
        await using var handle = fileSystem.CreateFile("pages.bin").OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await handle.WriteAsync(new byte[] { 7, 8, 9 }, 7, CancellationToken.None);
        await handle.WriteAsync(new byte[] { 1, 2, 3 }, 1, CancellationToken.None);
        handle.Length.ShouldBe(10);
        var buffer = new byte[3];
        (await handle.ReadAsync(buffer, 7, CancellationToken.None)).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 7, 8, 9 });
        (await handle.ReadAsync(buffer, 1, CancellationToken.None)).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 1, 2, 3 });

        buffer.AsSpan().Clear();
        (await handle.ReadAsync(buffer, 9, CancellationToken.None)).ShouldBe(1);
        buffer.ShouldBe(new byte[] { 9, 0, 0 });
        (await handle.ReadAsync(buffer, 10, CancellationToken.None)).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - Async: Should honor cancellation without changing file or buffer")]
    public async Task AsyncOperations_PreCanceledToken_ShouldCancelWithoutSideEffects()
    {
        using var fileSystem = CreateFileSystem();
        await using var handle = fileSystem.CreateFile("pages.bin").OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Write(new byte[] { 1, 2, 3 }, 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var buffer = new byte[] { 9, 9, 9 };

        await Should.ThrowAsync<OperationCanceledException>(async () => await handle.ReadAsync(buffer, 0, cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await handle.WriteAsync(new byte[] { 7 }, 10, cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await handle.FlushAsync(durable: false, cancellation.Token));

        buffer.ShouldBe(new byte[] { 9, 9, 9 });
        handle.Length.ShouldBe(3);
        handle.Read(buffer, 0).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - Dispose: Should release sharing registration and reject operations")]
    public async Task Dispose_OpenedHandle_ShouldReleaseFileAndThrowForLaterOperations()
    {
        using var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("pages.bin");
        var handle = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Should.Throw<IOException>(() => file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        Should.Throw<IOException>(() => file.Open(FileMode.Open, FileAccess.Read, FileShare.Read));

        handle.Dispose();
        handle.Dispose();

        Should.Throw<ObjectDisposedException>(() => _ = handle.Length);
        Should.Throw<ObjectDisposedException>(() => _ = handle.SupportsDurableFlush);
        Should.Throw<ObjectDisposedException>(() => handle.Read(new byte[1], 0));
        Should.Throw<ObjectDisposedException>(() => handle.Write(new byte[1], 0));
        Should.Throw<ObjectDisposedException>(() => handle.SetLength(1));
        Should.Throw<ObjectDisposedException>(() => handle.Flush(durable: false));
        Should.Throw<ObjectDisposedException>(() => handle.Flush(durable: true));
        await Should.ThrowAsync<ObjectDisposedException>(async () => await handle.ReadAsync(new byte[1], 0, CancellationToken.None));
        await Should.ThrowAsync<ObjectDisposedException>(async () => await handle.WriteAsync(new byte[1], 0, CancellationToken.None));
        await Should.ThrowAsync<ObjectDisposedException>(async () => await handle.FlushAsync(durable: false, CancellationToken.None));

        using var reopened = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        reopened.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - DisposeAsync: Should release the handle for a stream open")]
    public async Task DisposeAsync_OpenedHandle_ShouldReleaseSharingRegistration()
    {
        using var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("pages.bin");
        var handle = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await handle.DisposeAsync();
        await handle.DisposeAsync();

        Should.Throw<ObjectDisposedException>(() => _ = handle.Length);
        using var stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.CanWrite.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - OpenHandle: Should honor stream sharing restrictions")]
    public void OpenHandle_ExclusiveStreamOpen_ShouldRejectUntilStreamCloses()
    {
        using var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("pages.bin");
        using (var stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Should.Throw<IOException>(() => file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }

        using var handle = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - Concurrent I/O: Should preserve independent pages across handles")]
    public async Task ReadWrite_ConcurrentHandles_ShouldPreserveIndependentOffsets()
    {
        using var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("pages.bin");
        using var first = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var second = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            var handle = index % 2 == 0 ? first : second;
            var payload = Enumerable.Repeat((byte)index, 16).ToArray();
            var buffer = new byte[16];
            for (var iteration = 0; iteration < 20; iteration++)
            {
                handle.Write(payload, index * 16);
                handle.Read(buffer, index * 16).ShouldBe(16);
                buffer.ShouldBe(payload);
            }
        })));

        first.Length.ShouldBe(512);
        second.Length.ShouldBe(512);
        fileSystem.SpaceUsed.Length.ShouldBe(512);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - Access: Should enforce read-only and write-only handles")]
    public void OpenHandle_AccessRestrictions_ShouldRejectUnsupportedOperations()
    {
        using var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("pages.bin");
        using var reader = file.OpenHandle(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var writer = file.OpenHandle(FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        Should.Throw<NotSupportedException>(() => reader.Write(new byte[1], 0));
        Should.Throw<NotSupportedException>(() => reader.SetLength(1));
        Should.Throw<NotSupportedException>(() => writer.Read(new byte[1], 0));
        writer.Write(new byte[] { 4 }, 0);
        var buffer = new byte[1];
        reader.Read(buffer, 0).ShouldBe(1);
        buffer[0].ShouldBe((byte)4);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - Write: Should preserve content and quota when capacity is exhausted")]
    public void Write_InsufficientSpace_ShouldLeaveContentAndQuotaUnchanged()
    {
        using var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions { Size = 8 });
        using var handle = fileSystem.CreateFile("pages.bin").OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Write(new byte[] { 1, 2, 3 }, 0);

        Should.Throw<FileSystemException>(() => handle.Write(new byte[] { 4 }, 8)).Code.ShouldBe(FileSystemErrorCode.NotEnoughSpace);

        handle.Length.ShouldBe(3);
        fileSystem.SpaceUsed.Length.ShouldBe(3);
        var buffer = new byte[3];
        handle.Read(buffer, 0).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Theory(DisplayName = "Cohesion Test [InMemoryFileHandle] - OpenHandle: Should truncate with Create and Truncate modes")]
    [InlineData(FileMode.Create)]
    [InlineData(FileMode.Truncate)]
    public void OpenHandle_TruncatingMode_ShouldClearTheFile(FileMode mode)
    {
        using var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("pages.bin");
        using (var handle = file.OpenHandle(FileMode.Open, FileAccess.Write, FileShare.None))
        {
            handle.Write(new byte[] { 1, 2, 3 }, 0);
        }

        using var truncated = file.OpenHandle(mode, FileAccess.ReadWrite, FileShare.None);

        truncated.Length.ShouldBe(0);
        fileSystem.SpaceUsed.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - OpenHandle: Append mode should still use explicit write offsets")]
    public void OpenHandle_AppendMode_ShouldWriteAtTheSuppliedOffset()
    {
        using var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("pages.bin");
        using (var handle = file.OpenHandle(FileMode.Open, FileAccess.Write, FileShare.None))
        {
            handle.Write(new byte[] { 1, 2, 3 }, 0);
        }

        using (var append = file.OpenHandle(FileMode.Append, FileAccess.Write, FileShare.None))
        {
            append.Write(new byte[] { 9 }, 0);
            append.Length.ShouldBe(3);
        }

        using var reader = file.OpenHandle(FileMode.Open, FileAccess.Read, FileShare.None);
        var buffer = new byte[3];
        reader.Read(buffer, 0).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 9, 2, 3 });
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileHandle] - Shared buffer: Positional operations should preserve stream position")]
    public void ReadWrite_SharedStream_ShouldLeaveStreamPositionUnchanged()
    {
        using var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("pages.bin");
        using var stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var handle = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Write(new byte[] { 1, 2, 3, 4 });
        stream.Position = 2;

        handle.Write(new byte[] { 9 }, 0);
        handle.Read(new byte[1], 3).ShouldBe(1);

        stream.Position.ShouldBe(2);
        stream.ReadByte().ShouldBe(3);
        stream.Position.ShouldBe(3);
        handle.Length.ShouldBe(4);
    }

    private static InMemoryFileSystem CreateFileSystem()
    {
        return new InMemoryFileSystem(new InMemoryFileSystemOptions());
    }
}
