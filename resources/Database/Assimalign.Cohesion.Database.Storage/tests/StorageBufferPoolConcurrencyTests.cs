using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Multi-threaded pin, unpin, evict and write-back against a pool far smaller than the
/// working set, so every phase evicts and reloads constantly (#1157). Each page has one
/// writer, the storage contract's page-level single-writer rule; readers, an evicter and
/// a paced page writer run beside the writers. After every phase the pool's own
/// invariants are checked and every page is verified twice: through the pool (a reload
/// verifies the checksum) and on the stream after a full flush.
/// </summary>
public sealed class StorageBufferPoolConcurrencyTests
{
    private const int pageCount = 48;
    private const int capacity = 6;
    private const int writerCount = 4;
    private const int readerCount = 2;
    private const int phaseCount = 3;
    private const int operationsPerPhase = 800;

    [Theory(DisplayName = "Cohesion Test [Storage] - BufferPool: concurrent pin, unpin, evict and write-back keep every page's content and checksum")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PinUnpinEvictWriteBack_ConcurrentOnSmallPool_ShouldKeepContentsAndChecksums(int seed)
    {
        // Arrange: every page starts at version 0 on the stream.
        using var stream = new StorageStream(new SimulatedDurableFileHandle());
        stream.SetLength(pageCount * (long)Page.Size);
        using var pool = new StorageBufferPool(capacity);
        var versions = new int[pageCount];

        for (long pageId = 0; pageId < pageCount; pageId++)
        {
            using var handle = pool.Pin((PageId)pageId, stream);
            var page = handle.Page;
            page.Id = pageId;
            page.Type = PageType.Data;
            WriteVersion(handle, pageId, 0);
        }

        pool.FlushAll(stream);
        VerifyPhase(pool, stream, versions);

        for (int phase = 1; phase <= phaseCount; phase++)
        {
            // Act: writers, readers, an evicter and a paced page writer run together.
            using var stop = new CancellationTokenSource();
            var writers = Enumerable.Range(0, writerCount).Select(writer => Task.Run(() =>
            {
                var random = new Random(HashCode.Combine(seed, phase, writer));
                for (int i = 0; i < operationsPerPhase; i++)
                {
                    long pageId = writer + (writerCount * random.Next(pageCount / writerCount));
                    using var handle = pool.Pin((PageId)pageId, stream);
                    int version = versions[pageId] + 1;
                    WriteVersion(handle, pageId, version);
                    versions[pageId] = version;
                }
            })).ToArray();

            var background = new List<Task>();
            for (int reader = 0; reader < readerCount; reader++)
            {
                int readerSeed = HashCode.Combine(seed, phase, 100 + reader);
                background.Add(Task.Run(() =>
                {
                    var random = new Random(readerSeed);
                    while (!stop.IsCancellationRequested)
                    {
                        long pageId = random.Next(pageCount);
                        using var handle = pool.Pin((PageId)pageId, stream);
                        handle.Page.Id.ShouldBe(pageId);
                    }
                }));
            }

            background.Add(Task.Run(() =>
            {
                var random = new Random(HashCode.Combine(seed, phase, 200));
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        pool.Evict((PageId)random.Next(pageCount), stream);
                    }
                    catch (StorageIOException)
                    {
                        // The page is pinned by a writer or reader right now.
                    }

                    pool.FlushSome(stream, 4);
                }
            }));

            await Task.WhenAll(writers);
            stop.Cancel();
            await Task.WhenAll(background);

            // Assert
            VerifyPhase(pool, stream, versions);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: a page modified while it is being written back stays dirty")]
    public void WriteBack_PageModifiedDuringWrite_ShouldStayDirtyAndReachTheStream()
    {
        // Arrange: a writer holding a pin modifies the page while the pool writes it back
        // (the stream write is the window) and marks it dirty, as every writer does after
        // its change. The pool must not record the page clean afterwards.
        var file = new InterceptingFileHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(2L * Page.Size);
        using var pool = new StorageBufferPool(2);
        var handle = pool.Pin((PageId)0L, stream);
        WriteVersion(handle, 0, 1);
        file.OnWrite = () => WriteVersion(handle, 0, 2);

        // Act
        pool.FlushPage((PageId)0L, stream);
        file.OnWrite = null;
        handle.Dispose();

        // Assert: the second version is still owed to the stream, so eviction writes it
        // instead of dropping it, and the reload sees it under a valid checksum.
        handle.IsDirty.ShouldBeTrue();
        pool.Evict((PageId)0L, stream);
        using var reloaded = pool.Pin((PageId)0L, stream);
        ReadVersion(reloaded.Page).ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: a page written back mid-change is stamped over the bytes written")]
    public void WriteBack_PageModifiedDuringWrite_ShouldWriteAChecksumMatchingTheWrittenBytes()
    {
        // Arrange: the change lands while the pool is writing; whatever image reaches the
        // stream must verify on its own.
        var file = new InterceptingFileHandle();
        using var stream = new StorageStream(file);
        stream.SetLength(2L * Page.Size);
        using var pool = new StorageBufferPool(2);
        using var handle = pool.Pin((PageId)0L, stream);
        WriteVersion(handle, 0, 1);
        file.OnWrite = () => WriteVersion(handle, 0, 2);

        // Act
        pool.FlushPage((PageId)0L, stream);
        file.OnWrite = null;

        // Assert
        var buffer = new byte[Page.Size];
        stream.ReadPage((PageId)0L, buffer);
        Should.NotThrow(() => PageChecksum.Verify(buffer, (PageId)0L));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: the invariant check detects a negative pin count")]
    public void CheckInvariants_NegativePinCount_ShouldThrow()
    {
        // Arrange
        using var stream = new StorageStream(new SimulatedDurableFileHandle());
        stream.SetLength(Page.Size);
        using var pool = new StorageBufferPool(2);
        var handle = (StoragePageHandle)pool.Pin((PageId)0L, stream);
        pool.CheckInvariants();

        // Act
        handle.Entry.PinCount = -1;

        // Assert
        Should.Throw<InvalidOperationException>(() => pool.CheckInvariants()).Message.ShouldContain("pin count");
        handle.Entry.PinCount = 1;
        handle.Dispose();
        pool.CheckInvariants();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: the invariant check detects a resident entry marked recycled")]
    public void CheckInvariants_ResidentEntryMarkedRecycled_ShouldThrow()
    {
        // Arrange
        using var stream = new StorageStream(new SimulatedDurableFileHandle());
        stream.SetLength(Page.Size);
        using var pool = new StorageBufferPool(2);
        StorageBufferPool.BufferEntry entry;
        using (var handle = (StoragePageHandle)pool.Pin((PageId)0L, stream))
        {
            entry = handle.Entry;
        }

        // Act: the state a recycle of a still-resident entry would leave behind.
        entry.IsRecycled = true;

        // Assert
        Should.Throw<InvalidOperationException>(() => pool.CheckInvariants()).Message.ShouldContain("recycled");
        entry.IsRecycled = false;
        pool.CheckInvariants();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - BufferPool: the invariant check detects an entry whose node belongs to another list")]
    public void CheckInvariants_EntryNodeInAnotherList_ShouldThrow()
    {
        // Arrange
        using var stream = new StorageStream(new SimulatedDurableFileHandle());
        stream.SetLength(Page.Size);
        using var pool = new StorageBufferPool(2);
        var handle = (StoragePageHandle)pool.Pin((PageId)0L, stream);
        var node = handle.Entry.Node;

        // Act
        handle.Entry.Node = new LinkedList<long>().AddLast(0L);

        // Assert
        Should.Throw<InvalidOperationException>(() => pool.CheckInvariants()).Message.ShouldContain("access list");
        handle.Entry.Node = node;
        handle.Dispose();
        pool.CheckInvariants();
    }

    private static void VerifyPhase(StorageBufferPool pool, StorageStream stream, int[] versions)
    {
        pool.CheckInvariants();

        // Through the pool: a resident page shows the latest version; an evicted one
        // reloads from the stream, which verifies its checksum on the way in.
        for (long pageId = 0; pageId < pageCount; pageId++)
        {
            using var handle = pool.Pin((PageId)pageId, stream);
            handle.PinCount.ShouldBe(1);
            AssertVersion(handle.Page, pageId, versions[pageId]);
        }

        // On the stream: after a full flush every page carries a valid checksum and
        // the latest version.
        pool.FlushAll(stream);
        var buffer = new byte[Page.Size];
        for (long pageId = 0; pageId < pageCount; pageId++)
        {
            stream.ReadPage((PageId)pageId, buffer);
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(Page.ChecksumFieldOffset)).ShouldNotBe(0u);
            PageChecksum.Verify(buffer, (PageId)pageId);
            unsafe
            {
                fixed (byte* pointer = buffer)
                {
                    AssertVersion(new Page(pointer), pageId, versions[pageId]);
                }
            }
        }

        pool.CheckInvariants();
    }

    private static void WriteVersion(IStoragePageHandle handle, long pageId, int version)
    {
        var body = handle.Page.AsBodySpan();
        body.Fill(Pattern(pageId, version));
        BinaryPrimitives.WriteInt32LittleEndian(body, version);
        handle.MarkDirty();
    }

    private static int ReadVersion(Page page) => BinaryPrimitives.ReadInt32LittleEndian(page.AsBodySpan());

    private static void AssertVersion(Page page, long pageId, int version)
    {
        page.Id.ShouldBe(pageId);
        var body = page.AsBodySpan();
        ReadVersion(page).ShouldBe(version, $"page {pageId}");
        byte expected = Pattern(pageId, version);
        body[sizeof(int)..].IndexOfAnyExcept(expected).ShouldBe(-1, $"page {pageId} version {version} body");
    }

    private static byte Pattern(long pageId, int version) => (byte)((pageId * 31) + (version * 7) + 1);

    /// <summary>
    /// A simulated durable handle that runs a callback at the start of every write, before
    /// any byte is stored: the window in which a racing writer changes a page whose image
    /// is already on its way to the stream.
    /// </summary>
    private sealed class InterceptingFileHandle : IFileSystemFileHandle
    {
        private readonly SimulatedDurableFileHandle _inner = new();

        public Action? OnWrite { get; set; }

        public long Length => _inner.Length;

        public bool SupportsDurableFlush => _inner.SupportsDurableFlush;

        public int Read(Span<byte> buffer, long offset) => _inner.Read(buffer, offset);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, offset, cancellationToken);

        public void Write(ReadOnlySpan<byte> buffer, long offset)
        {
            OnWrite?.Invoke();
            _inner.Write(buffer, offset);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span, offset);
            return default;
        }

        public void SetLength(long length) => _inner.SetLength(length);

        public void Flush(bool durable) => _inner.Flush(durable);

        public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
            => _inner.FlushAsync(durable, cancellationToken);

        public void Dispose() => _inner.Dispose();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
