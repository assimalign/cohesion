using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Storage.Tests;

public sealed class DocumentContentTests
{
    [Theory]
    [InlineData("{\"nested\":{\"values\":[1,true,null,\"hello\"]},\"empty\":{}}")]
    [InlineData("[{},[],\"text\",-2.5,false]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("12.3400")]
    [InlineData("\"snow \\u2603\"")]
    public async Task JsonValuesPreserveExactBytes(string json)
    {
        using var storage = Create();
        await using var coordinator = Coordinate(storage);
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var bytes = Encoding.UTF8.GetBytes(json);
        var content = await storage.WriteContentAsync(coordinator, context, bytes);
        await coordinator.CommitAsync(context);
        storage.ReadContent(content).ToArray().ShouldBe(bytes);
        storage.Model.ShouldBe(StorageModel.Document);
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("[1,]")]
    [InlineData("1e100")]
    [InlineData("1e-100")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("{\"a\":NaN}")]
    [InlineData("")]
    public async Task InvalidDocumentWritesNoChunks(string json)
    {
        using var storage = Create();
        await using var coordinator = Coordinate(storage);
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await Should.ThrowAsync<JsonException>(() => storage.WriteContentAsync(coordinator, context, Encoding.UTF8.GetBytes(json)).AsTask());
        Count(storage).ShouldBe(0);
        await coordinator.RollbackAsync(context);
    }

    [Fact]
    public async Task LargeNestedDocumentUsesStampedChunkChainAndSurvivesCrash()
    {
        var data = new MemoryStream();
        var journal = new MemoryStream();
        using var storage = DocumentStorage.Create(data, journal, new MemoryStream(), "crash");
        await using var coordinator = Coordinate(storage);
        byte[] bytes = Encoding.UTF8.GetBytes("{\"items\":[\"" + new string('x', 1_200_000) + "\"]}");
        var committed = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var content = await storage.WriteContentAsync(coordinator, committed, bytes);
        await coordinator.CommitAsync(committed);
        int count = Count(storage);
        count.ShouldBeGreaterThan(128);
        var abandoned = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await storage.WriteContentAsync(coordinator, abandoned, Encoding.UTF8.GetBytes("{\"partial\":true}"));
        storage.WriteAheadJournal.Flush(true);

        using var recovered = DocumentStorage.Open(Clone(data), Clone(journal), new MemoryStream(), false);
        await using var recovery = Coordinate(recovered);
        recovery.AnalyzeAndScrub();
        recovery.CompleteRecovery();
        Count(recovered).ShouldBe(count);
        recovered.ReadContent(content).ToArray().ShouldBe(bytes);
        using var iterator = recovered.GetUnitIterator();
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            var stamps = RecordVersionStamp.ReadStamps(unit.Data.Span);
            stamps.Writer.ShouldBe(committed.Sequence);
            stamps.Deleter.ShouldBe(TransactionSequence.None);
            unit.Data.Span[16].ShouldBe((byte)3);
            unit.Data.Span[17].ShouldBe((byte)1);
        }
        await coordinator.RollbackAsync(abandoned);
    }

    [Fact]
    public async Task ReplacementRollbackRestoresOldChunksAndPurgeHonorsSnapshots()
    {
        using var storage = Create();
        await using var coordinator = Coordinate(storage);
        var write = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var old = await storage.WriteContentAsync(coordinator, write, Encoding.UTF8.GetBytes("[1,2,3]"));
        await coordinator.CommitAsync(write);
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var remove = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await storage.TombstoneContentAsync(coordinator, remove, old);
        await coordinator.RollbackAsync(remove);
        storage.ReadContent(old).ToArray().ShouldBe(Encoding.UTF8.GetBytes("[1,2,3]"));
        var deleting = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await storage.TombstoneContentAsync(coordinator, deleting, old);
        await coordinator.CommitAsync(deleting);
        coordinator.RunVersionPurgePass(default).ShouldBe(0);
        await coordinator.CommitAsync(reader);
        coordinator.RunVersionPurgePass(default).ShouldBe(1);
        Count(storage).ShouldBe(0);
    }

    [Fact]
    public async Task ChecksumMismatchRejectsContent()
    {
        using var storage = Create();
        await using var coordinator = Coordinate(storage);
        var write = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var content = await storage.WriteContentAsync(coordinator, write, Encoding.UTF8.GetBytes("{\"x\":1}"));
        await coordinator.CommitAsync(write);
        Should.Throw<StorageCorruptionException>(() => storage.ReadContent(content with { Checksum = content.Checksum ^ 1 }));
    }

    private static DocumentStorage Create() => DocumentStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "test");
    private static TransactionCoordinator Coordinate(DocumentStorage storage) => new(storage, storage.WriteAheadJournal, storage.Records);
    private static int Count(DocumentStorage storage)
    {
        using var iterator = storage.GetUnitIterator();
        int count = 0;
        while (iterator.MoveNext()) { count++; }
        return count;
    }
    private static MemoryStream Clone(MemoryStream source)
    {
        var clone = new MemoryStream();
        source.WriteTo(clone);
        clone.Position = 0;
        return clone;
    }
}
