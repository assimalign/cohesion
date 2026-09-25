using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Storage.Tests;

public sealed class BlobStorageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8064)]
    [InlineData(8065)]
    [InlineData(1050000)]
    public async Task ChunkedContent_RoundTripsAcrossArbitraryStreamBoundaries(int length)
    {
        using var storage = Create();
        await using var coordinator = Coordinate(storage);
        byte[] content = Pattern(length);
        BlobContentReference reference = await WriteAsync(storage, coordinator, content);

        storage.Model.ShouldBe(StorageModel.Blob);
        reference.Length.ShouldBe(length);
        using var read = storage.OpenRead(reference);
        read.CanSeek.ShouldBeFalse();
        var actual = new MemoryStream();
        await read.CopyToAsync(actual, 997);
        actual.ToArray().ShouldBe(content);
        coordinator.PairedTransactionCount.ShouldBe(0);
    }

    [Fact]
    public async Task ContentChecksum_UsesIeeeCrc32()
    {
        using var storage = Create();
        await using var coordinator = Coordinate(storage);
        var reference = await WriteAsync(storage, coordinator, Encoding.ASCII.GetBytes("123456789"));
        reference.Checksum.ShouldBe(0xcbf43926U);
    }

    [Fact]
    public async Task Flush_PersistsChunksWithoutPublishingContent()
    {
        using var storage = Create();
        await using var coordinator = Coordinate(storage);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        int published = 0;
        await using (var upload = storage.OpenWrite(coordinator, writer, async _ =>
        {
            published++;
            await coordinator.CommitAsync(writer);
        }, () => coordinator.RollbackAsync(writer)))
        {
            await upload.WriteAsync(Pattern(9000));
            await upload.FlushAsync();
            Count(storage).ShouldBe(2);
            published.ShouldBe(0);
            writer.State.ShouldBe(TransactionState.Active);
        }
        published.ShouldBe(1);
        writer.State.ShouldBe(TransactionState.Committed);
    }

    [Fact]
    public async Task CancelledWrite_RollsBackEveryPreviouslyWrittenChunkAndCannotPublish()
    {
        using var storage = Create();
        await using var coordinator = Coordinate(storage);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        bool published = false;
        var upload = storage.OpenWrite(coordinator, writer, _ =>
        {
            published = true;
            return default;
        }, () => coordinator.RollbackAsync(writer));
        await upload.WriteAsync(Pattern(2_000_000));
        Count(storage).ShouldBeGreaterThan(128);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            upload.WriteAsync(new byte[1], new CancellationToken(true)).AsTask());
        await upload.DisposeAsync();
        await upload.DisposeAsync();
        published.ShouldBeFalse();
        Count(storage).ShouldBe(0);
        storage.PageManager.FreePageCount.ShouldBeGreaterThan(128);
        writer.State.ShouldBe(TransactionState.RolledBack);
    }

    [Fact]
    public async Task LifetimeCancellationAtDispose_DoesNotPublishEvenAnEmptyUpload()
    {
        using var storage = Create();
        await using var coordinator = Coordinate(storage);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        using var cancellation = new CancellationTokenSource();
        bool published = false;
        var upload = storage.OpenWrite(coordinator, writer, _ =>
        {
            published = true;
            return default;
        }, () => coordinator.RollbackAsync(writer), cancellation.Token);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => upload.DisposeAsync().AsTask());
        published.ShouldBeFalse();
        writer.State.ShouldBe(TransactionState.RolledBack);
    }

    [Fact]
    public async Task Delete_ReclaimsAllChunksOnlyAfterSnapshotReleasesThem()
    {
        using var storage = Create();
        await using var coordinator = Coordinate(storage);
        byte[] content = Pattern(2_000_000);
        var reference = await WriteAsync(storage, coordinator, content);
        int records = Count(storage);
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var deleting = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await storage.TombstoneContentAsync(coordinator, deleting, reference);
        await coordinator.CommitAsync(deleting);
        coordinator.RunVersionPurgePass(default).ShouldBe(0);
        Count(storage).ShouldBe(records);
        using (var read = storage.OpenRead(reference))
        {
            var result = new MemoryStream();
            await read.CopyToAsync(result);
            result.ToArray().ShouldBe(content);
        }
        await coordinator.CommitAsync(reader);
        coordinator.RunVersionPurgePass(default).ShouldBe(records);
        Count(storage).ShouldBe(0);
        storage.PageManager.FreePageCount.ShouldBe(records);

        long pages = storage.PageManager.PageCount;
        await WriteAsync(storage, coordinator, content);
        storage.PageManager.PageCount.ShouldBe(pages);
    }

    [Fact]
    public async Task CrashRecovery_RedoesCommittedContentAndScrubsAbandonedChunksAcrossBatches()
    {
        var data = new MemoryStream();
        var journal = new MemoryStream();
        using var storage = BlobStorage.Create(data, journal, new MemoryStream(), "crash");
        await using var coordinator = Coordinate(storage);
        byte[] content = Pattern(100_000);
        var committed = await WriteAsync(storage, coordinator, content);
        int committedChunks = Count(storage);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var abandoned = storage.OpenWrite(coordinator, writer, _ => default, () => coordinator.RollbackAsync(writer));
        await abandoned.WriteAsync(Pattern(2_000_000));
        // Drain journal buffering into the in-memory crash image. MemoryStream
        // has no durable-flush contract; physical persistence is tested separately.
        storage.WriteAheadJournal.Flush(forceDurable: false);

        // Capture the serialized storage bytes before any stream or coordinator disposal.
        using var recovered = BlobStorage.Open(Clone(data), Clone(journal), new MemoryStream(), false);
        await using var recoveredCoordinator = Coordinate(recovered);
        recoveredCoordinator.AnalyzeAndScrub();
        recoveredCoordinator.CompleteRecovery();
        Count(recovered).ShouldBe(committedChunks);
        recovered.PageManager.FreePageCount.ShouldBeGreaterThan(128);
        using var read = recovered.OpenRead(committed);
        var actual = new MemoryStream();
        await read.CopyToAsync(actual);
        actual.ToArray().ShouldBe(content);

        // Clean up the original process after the independent crash image was verified.
        await coordinator.RollbackAsync(writer);
    }

    [Fact]
    public async Task Read_RejectsPageChecksumCorruption()
    {
        var data = new MemoryStream();
        using var storage = BlobStorage.Create(data, new MemoryStream(), new MemoryStream(), "crc");
        await using var coordinator = Coordinate(storage);
        var reference = await WriteAsync(storage, coordinator, Pattern(20_000));
        coordinator.Checkpoint();
        var bytes = data.ToArray();
        var (page, _) = BlobStorage.UnpackLocation(reference.Head);
        bytes[(int)((long)page * Page.Size + Page.HeaderSize + 28)] ^= 0xff;
        using var reopened = BlobStorage.Open(new MemoryStream(bytes), new MemoryStream(), new MemoryStream());
        using var read = reopened.OpenRead(reference);
        Should.Throw<StorageCorruptionException>(() => read.ReadByte());
    }

    [Fact]
    public async Task MetadataOwnerScan_DoesNotReadBlobPages()
    {
        using var storage = Create();
        await using var coordinator = Coordinate(storage);
        await WriteAsync(storage, coordinator, Pattern(50_000));
        using var iterator = storage.GetUnitIterator(0);
        iterator.MoveNext().ShouldBeFalse();
    }

    private static BlobStorage Create() => BlobStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "test");
    private static TransactionCoordinator Coordinate(BlobStorage storage) => new(storage, storage.WriteAheadJournal, storage.Records);
    private static MemoryStream Clone(MemoryStream source)
    {
        var clone = new MemoryStream();
        source.WriteTo(clone);
        clone.Position = 0;
        return clone;
    }
    private static byte[] Pattern(int count)
    {
        var content = new byte[count];
        for (int index = 0; index < content.Length; index++)
        {
            content[index] = (byte)((index * 31) % 251);
        }
        return content;
    }
    private static int Count(BlobStorage storage)
    {
        int count = 0;
        using var iterator = storage.GetUnitIterator();
        while (iterator.MoveNext())
        {
            count++;
        }
        return count;
    }
    private static async Task<BlobContentReference> WriteAsync(BlobStorage storage, TransactionCoordinator coordinator, byte[] content)
    {
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        BlobContentReference reference = default;
        await using (var upload = storage.OpenWrite(coordinator, writer, async value =>
        {
            reference = value;
            await coordinator.CommitAsync(writer);
        }, () => coordinator.RollbackAsync(writer)))
        {
            for (int offset = 0; offset < content.Length; offset += 1237)
            {
                await upload.WriteAsync(content.AsMemory(offset, Math.Min(1237, content.Length - offset)));
            }
        }
        return reference;
    }
}
