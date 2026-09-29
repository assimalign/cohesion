using Shouldly;

namespace Assimalign.Cohesion.FileSystem.Aggregate.Tests;

public class AggregateFileSystemFileHandleTests
{
    [Theory(DisplayName = "Cohesion Test [AggregateFileSystem] - OpenHandle: Should preserve positional reads and extending writes")]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenHandle_NonSequentialOffsets_ShouldPreserveIndependentPositions(bool physical)
    {
        using var fixture = new MountedFiles();
        using var handle = fixture.CreateFile(physical).OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Length.ShouldBe(0);

        handle.Write(new byte[] { 40, 41, 42 }, 40);
        handle.Write(new byte[] { 1, 2, 3 }, 0);
        handle.Write(new byte[] { 20, 21, 22 }, 20);

        handle.Length.ShouldBe(43);
        var buffer = new byte[3];
        handle.Read(buffer, 20).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 20, 21, 22 });
        handle.Read(buffer, 0).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 1, 2, 3 });
        handle.Read(buffer, 40).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 40, 41, 42 });

        var tail = new byte[8];
        handle.Read(tail, 41).ShouldBe(2);
        tail[..2].ShouldBe(new byte[] { 41, 42 });
        handle.Read(tail, 43).ShouldBe(0);
        handle.Length.ShouldBe(43);
    }

    [Theory(DisplayName = "Cohesion Test [AggregateFileSystem] - SetLength: Should truncate and extend the resolved file")]
    [InlineData(false)]
    [InlineData(true)]
    public void SetLength_TruncateAndExtend_ShouldUpdateLengthAndContents(bool physical)
    {
        using var fixture = new MountedFiles();
        using var handle = fixture.CreateFile(physical).OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Write(new byte[] { 1, 2, 3, 4 }, 0);

        handle.SetLength(2);

        handle.Length.ShouldBe(2);
        var buffer = new byte[4];
        handle.Read(buffer, 0).ShouldBe(2);
        buffer[..2].ShouldBe(new byte[] { 1, 2 });

        handle.SetLength(8);

        handle.Length.ShouldBe(8);
        handle.Read(buffer, 2).ShouldBe(4);
        buffer.ShouldBe(new byte[4]);
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Flush: Should preserve durability of each resolved mount")]
    public async Task Flush_NestedNonDurableMount_ShouldNeverInheritOuterDurability()
    {
        using var fixture = new MountedFiles();
        await using var physical = fixture.CreateFile(physical: true)
            .OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var memory = fixture.CreateFile(physical: false)
            .OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        physical.Write(new byte[] { 1 }, 0);
        memory.Write(new byte[] { 2 }, 0);

        physical.SupportsDurableFlush.ShouldBeTrue();
        memory.SupportsDurableFlush.ShouldBeFalse();
        Should.NotThrow(() => physical.Flush(durable: true));
        await Should.NotThrowAsync(() => physical.FlushAsync(durable: true, CancellationToken.None).AsTask());
        Should.Throw<NotSupportedException>(() => memory.Flush(durable: true));
        await Should.ThrowAsync<NotSupportedException>(() => memory.FlushAsync(durable: true, CancellationToken.None).AsTask());
        Should.NotThrow(() => physical.Flush(durable: false));
        Should.NotThrow(() => memory.Flush(durable: false));
        await Should.NotThrowAsync(() => physical.FlushAsync(durable: false, CancellationToken.None).AsTask());
        await Should.NotThrowAsync(() => memory.FlushAsync(durable: false, CancellationToken.None).AsTask());
    }

    [Theory(DisplayName = "Cohesion Test [AggregateFileSystem] - Async I/O: Should preserve offsets and short reads")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncIo_NonSequentialOffsets_ShouldMatchSynchronousBehavior(bool physical)
    {
        using var fixture = new MountedFiles();
        await using var handle = fixture.CreateFile(physical).OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await handle.WriteAsync(new byte[] { 30, 31, 32 }, 30, CancellationToken.None);
        await handle.WriteAsync(new byte[] { 1, 2, 3 }, 0, CancellationToken.None);
        await handle.WriteAsync(new byte[] { 10, 11, 12 }, 10, CancellationToken.None);

        handle.Length.ShouldBe(33);
        var buffer = new byte[3];
        (await handle.ReadAsync(buffer, 10, CancellationToken.None)).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 10, 11, 12 });
        (await handle.ReadAsync(buffer, 0, CancellationToken.None)).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 1, 2, 3 });
        (await handle.ReadAsync(buffer, 30, CancellationToken.None)).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 30, 31, 32 });
        var tail = new byte[8];
        (await handle.ReadAsync(tail, 31, CancellationToken.None)).ShouldBe(2);
        tail[..2].ShouldBe(new byte[] { 31, 32 });
        (await handle.ReadAsync(tail, 33, CancellationToken.None)).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [AggregateFileSystem] - Async I/O: Should support concurrent offset operations")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncIo_ConcurrentOffsets_ShouldKeepWritesAndReadsIndependent(bool physical)
    {
        using var fixture = new MountedFiles();
        await using var handle = fixture.CreateFile(physical).OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        int[] offsets = [192, 0, 128, 64];
        var payloads = offsets.Select(offset => Enumerable.Repeat((byte)(offset / 64 + 1), 64).ToArray()).ToArray();

        await Task.WhenAll(offsets.Select((offset, index) =>
            handle.WriteAsync(payloads[index], offset, CancellationToken.None).AsTask()));
        var buffers = offsets.Select(_ => new byte[64]).ToArray();
        var reads = await Task.WhenAll(offsets.Select((offset, index) =>
            handle.ReadAsync(buffers[index], offset, CancellationToken.None).AsTask()));

        handle.Length.ShouldBe(256);
        reads.ShouldAllBe(count => count == 64);
        for (var index = 0; index < buffers.Length; index++)
        {
            buffers[index].ShouldBe(payloads[index]);
        }
    }

    [Theory(DisplayName = "Cohesion Test [AggregateFileSystem] - Async I/O: Should honor cancellation without modifying content")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncIo_CanceledToken_ShouldCancelWithoutModifyingFile(bool physical)
    {
        using var fixture = new MountedFiles();
        await using var handle = fixture.CreateFile(physical).OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Write(new byte[] { 1, 2, 3 }, 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var buffer = new byte[3];

        await Should.ThrowAsync<OperationCanceledException>(() => handle.ReadAsync(buffer, 0, cancellation.Token).AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() => handle.WriteAsync(new byte[] { 9 }, 10, cancellation.Token).AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() => handle.FlushAsync(durable: false, cancellation.Token).AsTask());

        buffer.ShouldBe(new byte[3]);
        handle.Length.ShouldBe(3);
        handle.Read(buffer, 0).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Theory(DisplayName = "Cohesion Test [AggregateFileSystem] - Dispose: Should release the resolved handle and reject further I/O")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Dispose_SynchronousOrAsynchronous_ShouldReleaseHandleAndRejectFurtherIo(bool physical, bool asynchronously)
    {
        using var fixture = new MountedFiles();
        var file = fixture.CreateFile(physical);
        var handle = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Write(new byte[] { 1, 2, 3 }, 0);

        if (asynchronously)
        {
            await handle.DisposeAsync();
        }
        else
        {
            handle.Dispose();
        }

        Should.Throw<ObjectDisposedException>(() => { _ = handle.Length; });
        Should.Throw<ObjectDisposedException>(() => handle.Read(new byte[3], 0));
        Should.Throw<ObjectDisposedException>(() => handle.Write(new byte[] { 4 }, 0));
        Should.Throw<ObjectDisposedException>(() => handle.SetLength(0));
        Should.Throw<ObjectDisposedException>(() => handle.Flush(durable: false));
        await Should.ThrowAsync<ObjectDisposedException>(() => handle.ReadAsync(new byte[3], 0, CancellationToken.None).AsTask());
        await Should.ThrowAsync<ObjectDisposedException>(() => handle.WriteAsync(new byte[] { 4 }, 0, CancellationToken.None).AsTask());
        await Should.ThrowAsync<ObjectDisposedException>(() => handle.FlushAsync(durable: false, CancellationToken.None).AsTask());

        using var reopened = file.OpenHandle(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var buffer = new byte[3];
        reopened.Read(buffer, 0).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 1, 2, 3 });
        handle.Dispose();
        await handle.DisposeAsync();
    }

    private sealed class MountedFiles : IDisposable
    {
        private readonly DirectoryInfo _directory = System.IO.Directory.CreateTempSubdirectory("cohesion-aggregate-handle-");
        private readonly PhysicalFileSystem _physical;
        private readonly AggregateFileSystem _aggregate;

        public MountedFiles()
        {
            _physical = new PhysicalFileSystem(_directory.FullName);
            _aggregate = new AggregateFileSystemBuilder()
                .Mount("/", _physical, ownsFileSystem: true)
                .Mount("/cache", new InMemoryFileSystem(new InMemoryFileSystemOptions()), ownsFileSystem: true)
                .Build();
        }

        public IFileSystemFile CreateFile(bool physical)
        {
            if (!physical)
            {
                return _aggregate.CreateFile("/cache/pages.bin");
            }

            // Aggregate's existing path router gives Physical a rooted provider-relative path
            // that Physical rejects. Enumerate the root to obtain the real aggregate wrapper
            // without changing that unrelated routing behavior in this additive handle step.
            _physical.CreateFile("pages.bin");
            return _aggregate.RootDirectory.GetFiles().Single(file => file.Name.ToString() == "pages.bin");
        }

        public void Dispose()
        {
            _aggregate.Dispose();
            _directory.Delete(recursive: true);
        }
    }
}
