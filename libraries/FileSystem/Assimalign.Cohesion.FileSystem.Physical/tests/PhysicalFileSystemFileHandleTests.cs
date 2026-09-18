using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;

namespace Assimalign.Cohesion.FileSystem.Physical.Tests;

public sealed class PhysicalFileSystemFileHandleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CohesionHandleTests", Guid.NewGuid().ToString("N"));
    private readonly PhysicalFileSystem _fileSystem;

    public PhysicalFileSystemFileHandleTests()
    {
        Directory.CreateDirectory(_root);
        _fileSystem = new PhysicalFileSystem(new PhysicalFileSystemOptions { Root = _root });
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Handle: Positional writes extend and reads stop at EOF")]
    public void Write_AtOffset_ShouldExtendAndReadShortAtEnd()
    {
        var file = _fileSystem.CreateFile("handle.bin");
        using var handle = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        handle.Write(new byte[] { 10, 20, 30 }, 7);

        handle.Length.ShouldBe(10);
        var buffer = new byte[8];
        handle.Read(buffer, 8).ShouldBe(2);
        buffer.ShouldBe(new byte[] { 20, 30, 0, 0, 0, 0, 0, 0 });
        handle.Read(buffer, 10).ShouldBe(0);
        handle.Read(buffer, 100).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Handle: Non-sequential operations preserve explicit offsets")]
    public void ReadWrite_NonSequentialOffsets_ShouldRemainIndependent()
    {
        using var handle = _fileSystem.CreateFile("handle.bin")
            .OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        handle.Write(new byte[] { 50, 51 }, 10);
        handle.Write(new byte[] { 10, 11 }, 0);
        handle.Write(new byte[] { 30, 31 }, 5);

        var buffer = new byte[2];
        handle.Read(buffer, 10).ShouldBe(2);
        buffer.ShouldBe(new byte[] { 50, 51 });
        handle.Read(buffer, 0).ShouldBe(2);
        buffer.ShouldBe(new byte[] { 10, 11 });
        handle.Read(buffer, 5).ShouldBe(2);
        buffer.ShouldBe(new byte[] { 30, 31 });
        handle.Length.ShouldBe(12);
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Handle: SetLength truncates and extends")]
    public void SetLength_TruncateAndExtend_ShouldUpdateLength()
    {
        using var handle = _fileSystem.CreateFile("handle.bin")
            .OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Write(new byte[] { 1, 2, 3, 4 }, 0);

        handle.SetLength(2);
        handle.Length.ShouldBe(2);
        var buffer = new byte[4];
        handle.Read(buffer, 0).ShouldBe(2);
        buffer.ShouldBe(new byte[] { 1, 2, 0, 0 });

        handle.SetLength(16);
        handle.Length.ShouldBe(16);
        handle.Read(buffer, 12).ShouldBe(4);
        handle.SetLength(0);
        handle.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Handle: Flush provides the advertised durability")]
    public async Task Flush_Durable_ShouldSucceedAndPreserveData()
    {
        var file = _fileSystem.CreateFile("handle.bin");
        await using (var handle = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            handle.SupportsDurableFlush.ShouldBeTrue();
            handle.Write(new byte[] { 7, 8, 9 }, 3);
            Should.NotThrow(() => handle.Flush(durable: false));
            Should.NotThrow(() => handle.Flush(durable: true));
            await handle.FlushAsync(durable: false, CancellationToken.None);
            await handle.FlushAsync(durable: true, CancellationToken.None);
        }

        using var reopened = file.OpenHandle(FileMode.Open, FileAccess.Read, FileShare.None);
        var buffer = new byte[3];
        reopened.Length.ShouldBe(6);
        reopened.Read(buffer, 3).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 7, 8, 9 });
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Handle: Async operations preserve offsets and EOF behavior")]
    public async Task ReadWriteAsync_NonSequentialOffsets_ShouldMatchSynchronousBehavior()
    {
        await using var handle = _fileSystem.CreateFile("handle.bin")
            .OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await handle.WriteAsync(new byte[] { 50, 51 }, 10, cancellation.Token);
        await handle.WriteAsync(new byte[] { 10, 11 }, 0, cancellation.Token);
        await handle.WriteAsync(new byte[] { 30, 31 }, 5, cancellation.Token);

        var buffer = new byte[2];
        (await handle.ReadAsync(buffer, 10, cancellation.Token)).ShouldBe(2);
        buffer.ShouldBe(new byte[] { 50, 51 });
        (await handle.ReadAsync(buffer, 0, cancellation.Token)).ShouldBe(2);
        buffer.ShouldBe(new byte[] { 10, 11 });
        (await handle.ReadAsync(buffer, 5, cancellation.Token)).ShouldBe(2);
        buffer.ShouldBe(new byte[] { 30, 31 });
        (await handle.ReadAsync(buffer, 11, cancellation.Token)).ShouldBe(1);
        buffer[0].ShouldBe((byte)51);
        (await handle.ReadAsync(buffer, 12, cancellation.Token)).ShouldBe(0);
        handle.Length.ShouldBe(12);
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Handle: Async operations honor cancellation without changing data")]
    public async Task AsyncOperations_Canceled_ShouldThrowAndLeaveDataUnchanged()
    {
        await using var handle = _fileSystem.CreateFile("handle.bin")
            .OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Write(new byte[] { 1, 2, 3 }, 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var buffer = new byte[] { 99, 99, 99 };

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await handle.ReadAsync(buffer, 0, cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await handle.WriteAsync(new byte[] { 4, 5 }, 8, cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await handle.FlushAsync(durable: false, cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await handle.FlushAsync(durable: true, cancellation.Token));

        buffer.ShouldBe(new byte[] { 99, 99, 99 });
        handle.Length.ShouldBe(3);
        handle.Read(buffer, 0).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Theory(DisplayName = "Cohesion Test [PhysicalFileSystem] - Handle: Disposal releases the file and rejects further operations")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_ActiveHandle_ShouldReleaseFileAndRejectOperations(bool asynchronously)
    {
        var file = _fileSystem.CreateFile("handle.bin");
        var handle = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Write(new byte[] { 1 }, 0);

        if (asynchronously)
        {
            await handle.DisposeAsync();
        }
        else
        {
            handle.Dispose();
        }

        Should.Throw<ObjectDisposedException>(() => { _ = handle.Length; });
        Should.Throw<ObjectDisposedException>(() => { _ = handle.SupportsDurableFlush; });
        Should.Throw<ObjectDisposedException>(() => handle.Read(new byte[1], 0));
        Should.Throw<ObjectDisposedException>(() => handle.Write(new byte[] { 2 }, 0));
        Should.Throw<ObjectDisposedException>(() => handle.SetLength(0));
        Should.Throw<ObjectDisposedException>(() => handle.Flush(false));
        Should.Throw<ObjectDisposedException>(() => handle.Flush(true));
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await handle.ReadAsync(new byte[1], 0, CancellationToken.None));
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await handle.WriteAsync(new byte[] { 2 }, 0, CancellationToken.None));
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await handle.FlushAsync(false, CancellationToken.None));
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await handle.FlushAsync(true, CancellationToken.None));
        handle.Dispose();
        await handle.DisposeAsync();

        using var reopened = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        reopened.Length.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Handle: Concurrent sync and async operations cannot move one another's offsets")]
    public async Task ReadWrite_ConcurrentOffsets_ShouldNotInterfere()
    {
        await using var handle = _fileSystem.CreateFile("handle.bin")
            .OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        handle.SetLength(32 * 128);

        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(async () =>
        {
            long offset = (31 - index) * 128;
            var contents = Enumerable.Repeat((byte)(index + 1), 128).ToArray();
            for (int iteration = 0; iteration < 8; iteration++)
            {
                if (index % 2 == 0)
                {
                    handle.Write(contents, offset);
                }
                else
                {
                    await handle.WriteAsync(contents, offset, cancellation.Token);
                }

                var buffer = new byte[128];
                if (index % 2 == 0)
                {
                    (await handle.ReadAsync(buffer, offset, cancellation.Token)).ShouldBe(128);
                }
                else
                {
                    handle.Read(buffer, offset).ShouldBe(128);
                }
                buffer.ShouldBe(contents);
            }
        }, cancellation.Token)));

        handle.Length.ShouldBe(4096);
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Handle: Invalid offsets and lengths are rejected")]
    public async Task ReadWriteSetLength_NegativeArguments_ShouldThrow()
    {
        await using var handle = _fileSystem.CreateFile("handle.bin")
            .OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Should.Throw<ArgumentOutOfRangeException>(() => handle.Read(new byte[1], -1));
        Should.Throw<ArgumentOutOfRangeException>(() => handle.Write(new byte[] { 1 }, -1));
        Should.Throw<ArgumentOutOfRangeException>(() => handle.SetLength(-1));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await handle.ReadAsync(new byte[1], -1, CancellationToken.None));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await handle.WriteAsync(new byte[] { 1 }, -1, CancellationToken.None));
        handle.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Handle: Append opening keeps positional write offsets")]
    public void OpenHandle_Append_ShouldUseExplicitWriteOffsets()
    {
        var file = _fileSystem.CreateFile("handle.bin");
        using (var initial = file.OpenHandle(FileMode.Open, FileAccess.Write, FileShare.None))
        {
            initial.Write(new byte[] { 1, 2, 3 }, 0);
        }

        using (var append = file.OpenHandle(FileMode.Append, FileAccess.Write, FileShare.None))
        {
            append.Write(new byte[] { 9 }, 0);
            append.Length.ShouldBe(3);
        }

        using var read = file.OpenHandle(FileMode.Open, FileAccess.Read, FileShare.None);
        var buffer = new byte[3];
        read.Read(buffer, 0).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 9, 2, 3 });
        Should.Throw<ArgumentException>(() => file.OpenHandle(FileMode.Append, FileAccess.ReadWrite, FileShare.ReadWrite));
    }

    [Theory(DisplayName = "Cohesion Test [PhysicalFileSystem] - Handle: Read-only providers reject mutating opens")]
    [InlineData(FileMode.Open, FileAccess.Write)]
    [InlineData(FileMode.Create, FileAccess.ReadWrite)]
    [InlineData(FileMode.Truncate, FileAccess.Write)]
    public void OpenHandle_ReadOnlyProvider_ShouldRejectMutatingModes(FileMode mode, FileAccess access)
    {
        _fileSystem.CreateFile("handle.bin");
        using var readOnly = new PhysicalFileSystem(new PhysicalFileSystemOptions { Root = _root, IsReadOnly = true });
        var file = readOnly.GetFile("handle.bin");

        Should.Throw<InvalidOperationException>(() => file.OpenHandle(mode, access, FileShare.None));

        using var handle = file.OpenHandle(FileMode.Open, FileAccess.Read, FileShare.None);
        handle.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Handle: Read-only access cannot write or resize")]
    public async Task OpenHandle_ReadAccess_ShouldRejectWritesAndResizing()
    {
        var file = _fileSystem.CreateFile("handle.bin");
        await using var handle = file.OpenHandle(FileMode.Open, FileAccess.Read, FileShare.None);

        // Windows reports access denied; Unix can report EBADF as IOException for a read-only descriptor.
        var writeError = Should.Throw<Exception>(() => handle.Write(new byte[] { 1 }, 0));
        (writeError is UnauthorizedAccessException or IOException).ShouldBeTrue();
        var lengthError = Should.Throw<Exception>(() => handle.SetLength(1));
        (lengthError is UnauthorizedAccessException or IOException).ShouldBeTrue();
        var asyncError = await Should.ThrowAsync<Exception>(async () =>
            await handle.WriteAsync(new byte[] { 1 }, 0, CancellationToken.None));
        (asyncError is UnauthorizedAccessException or IOException).ShouldBeTrue();
        handle.Length.ShouldBe(0);
    }

    public void Dispose()
    {
        _fileSystem.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
