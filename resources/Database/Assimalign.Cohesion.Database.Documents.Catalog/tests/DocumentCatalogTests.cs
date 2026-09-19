using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed class DocumentCatalogTests
{
    [Fact]
    public async Task CollectionOwnershipAndVersionsSurviveReopen()
    {
        await using var fixture = await Fixture.Create();
        var owner = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await fixture.Catalog.SaveCollectionAsync(fixture.Collection with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "OrdersSchema" }, owner);
        await fixture.Coordinator.CommitAsync(owner);
        await fixture.Write("one", "{\"old\":1}");
        await fixture.Write("one", "{\"new\":[true,{},null]}");
        fixture.Coordinator.Checkpoint();

        using var reopened = DocumentStorage.Open(Clone(fixture.Data), Clone(fixture.Journal), new MemoryStream(), false);
        await using var coordinator = new TransactionCoordinator(reopened, reopened.WriteAheadJournal, reopened.Records);
        var plan = coordinator.AnalyzeAndScrub();
        var catalog = DocumentCatalog.Open(reopened, coordinator);
        await catalog.RecoverIndexesAsync(plan.Aborted);
        coordinator.CompleteRecovery();
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        catalog.FindCollection("items", reader.Snapshot)!.Value.OwningSchema.ShouldBe("OrdersSchema");
        catalog.FindCollection("items", reader.Snapshot)!.Value.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        var document = catalog.FindDocument(fixture.Collection.Id, "one", reader.Snapshot)!.Value;
        document.Version.ShouldBe(2UL);
        Encoding.UTF8.GetString(reopened.ReadContent(Content(document)).Span).ShouldBe("{\"new\":[true,{},null]}");
        await coordinator.CommitAsync(reader);
    }

    [Fact]
    public async Task MixedShapesNestedPathsAndArraySubscriptsUseScalarIndexes()
    {
        await using var f = await Fixture.Create();
        await f.Write("a", "{\"person\":{\"age\":10},\"items\":[{\"value\":\"a\"}]}");
        await f.Write("b", "{\"person\":{\"age\":20.0},\"items\":[{\"value\":\"b\"}]}");
        await f.Write("c", "{\"other\":99}");
        await f.Write("d", "{\"person\":null,\"items\":[]}");
        await f.Write("e", "[1,2,3]");
        await f.CreateIndex("age", "person.age");
        await f.CreateIndex("item", "items[0].value");
        (await f.Search("age", 10m, true, 20m, false)).ShouldBe(["a"]);
        (await f.Search("age", 20m, true, 20m, true)).ShouldBe(["b"]);
        (await f.Search("item", "a", false, null, false)).ShouldBe(["b"]);
        (await f.Search("age", null, true, null, true)).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task InsertReplaceDeleteAndRollbackMaintainIndexVersions()
    {
        await using var f = await Fixture.Create();
        await f.CreateIndex("age", "age");
        await f.Write("a", "{\"age\":1}");
        var reader = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await f.Write("a", "{\"age\":2}");
        (await f.Search("age", 1m, true, 1m, true)).ShouldBeEmpty();
        var before = await f.Catalog.SearchIndexAsync(f.Collection.Id, "age", 1m, true, 1m, true, reader.Snapshot);
        before.Select(document => document.Id).ShouldBe(["a"]);

        var writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await f.WriteIn(writer, "a", "{\"age\":3}");
        await f.Coordinator.RollbackAsync(writer);
        (await f.Search("age", 2m, true, 2m, true)).ShouldBe(["a"]);
        (await f.Search("age", 3m, true, 3m, true)).ShouldBeEmpty();

        var deleting = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var document = f.Catalog.FindDocument(f.Collection.Id, "a", deleting.Snapshot)!.Value;
        await f.Catalog.DeleteDocumentAsync(f.Collection.Id, "a", deleting);
        await f.Storage.TombstoneContentAsync(f.Coordinator, deleting, Content(document));
        await f.Coordinator.RollbackAsync(deleting);
        (await f.Search("age", 2m, true, 2m, true)).ShouldBe(["a"]);

        deleting = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await f.Catalog.DeleteDocumentAsync(f.Collection.Id, "a", deleting);
        await f.Storage.TombstoneContentAsync(f.Coordinator, deleting, Content(document));
        await f.Coordinator.CommitAsync(deleting);
        (await f.Search("age", null, true, null, true)).ShouldBeEmpty();
        await f.Coordinator.CommitAsync(reader);
        f.Coordinator.RunVersionPurgePass(default).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task CrashKeepsCommittedIndexedDocumentsAndScrubsPartialUpdateAfterRootSplit()
    {
        await using var f = await Fixture.Create();
        await f.CreateIndex("value", "value");
        var committed = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        for (int i = 0; i < 220; i++)
        {
            await f.WriteIn(committed, $"doc{i:D3}", $"{{\"value\":{i}}}");
        }
        await f.Coordinator.CommitAsync(committed);
        var abandoned = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await f.WriteIn(abandoned, "doc001", "{\"value\":900}");
        await f.WriteIn(abandoned, "partial", "{\"value\":901}");
        // Drain journal buffering into the in-memory recovery image; MemoryStream
        // has no durable-flush contract.
        f.Storage.WriteAheadJournal.Flush(forceDurable: false);

        using var reopened = DocumentStorage.Open(Clone(f.Data), Clone(f.Journal), new MemoryStream(), false);
        await using var coordinator = new TransactionCoordinator(reopened, reopened.WriteAheadJournal, reopened.Records);
        var plan = coordinator.AnalyzeAndScrub();
        var catalog = DocumentCatalog.Open(reopened, coordinator);
        await catalog.RecoverIndexesAsync(plan.Aborted);
        coordinator.CompleteRecovery();
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        catalog.GetDocuments(f.Collection.Id, null, reader.Snapshot).Count.ShouldBe(220);
        catalog.FindDocument(f.Collection.Id, "partial", reader.Snapshot).ShouldBeNull();
        var result = await catalog.SearchIndexAsync(f.Collection.Id, "value", 200m, true, null, false, reader.Snapshot);
        result.Count.ShouldBe(20);
        (await catalog.SearchIndexAsync(f.Collection.Id, "value", 1m, true, 1m, true, reader.Snapshot)).Single().Id.ShouldBe("doc001");
        await coordinator.CommitAsync(reader);
        await f.Coordinator.RollbackAsync(abandoned);
    }

    [Fact]
    public async Task IndexDropRollbackAndRecreationPreserveDefinitionVisibility()
    {
        await using var f = await Fixture.Create();
        await f.Write("a", "{\"value\":1}");
        await f.CreateIndex("value", "value");
        var reader = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var drop = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await f.Catalog.DeleteIndexAsync(f.Collection.Id, "value", drop);
        await f.Coordinator.RollbackAsync(drop);
        (await f.Search("value", 1m, true, 1m, true)).ShouldBe(["a"]);
        drop = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await f.Catalog.DeleteIndexAsync(f.Collection.Id, "value", drop);
        await f.Coordinator.CommitAsync(drop);
        await f.CreateIndex("value", "value");
        (await f.Catalog.SearchIndexAsync(f.Collection.Id, "value", 1m, true, 1m, true, reader.Snapshot)).Single().Id.ShouldBe("a");
        (await f.Search("value", 1m, true, 1m, true)).ShouldBe(["a"]);
        await f.Coordinator.CommitAsync(reader);
    }

    [Fact]
    public async Task IndexStringRangeMatchesOrdinalUtf16AndNumericScaleEquality()
    {
        await using var f = await Fixture.Create();
        await f.Write("supplementary", "{\"text\":\"😀\",\"number\":1.00}");
        await f.Write("bmp", "{\"text\":\"\\ue000\",\"number\":1}");
        await f.CreateIndex("text", "text");
        await f.CreateIndex("number", "number");
        (await f.Search("text", "😀", true, "\ue000", false)).ShouldBe(["supplementary"]);
        (await f.Search("number", 1m, true, 1m, true)).ShouldBe(["bmp", "supplementary"]);
    }

    [Fact]
    public async Task OversizedIndexValueFailsBeforeMetadataReplacement()
    {
        await using var f = await Fixture.Create();
        await f.CreateIndex("value", "value");
        await f.Write("a", "{\"value\":\"small\"}");
        var writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await Should.ThrowAsync<DocumentCatalogException>(() => f.WriteIn(writer, "a", "{\"value\":\"" + new string('x', 2000) + "\"}"));
        await f.Coordinator.RollbackAsync(writer);
        (await f.Search("value", "small", true, "small", true)).ShouldBe(["a"]);
    }

    private static DocumentContentReference Content(DocumentCatalogEntry document) => new(document.HeadLocation, document.Length, document.Checksum);
    private static MemoryStream Clone(MemoryStream source)
    {
        var clone = new MemoryStream();
        source.WriteTo(clone);
        clone.Position = 0;
        return clone;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryStream Data { get; } = new();
        internal MemoryStream Journal { get; } = new();
        internal DocumentStorage Storage { get; }
        internal TransactionCoordinator Coordinator { get; }
        internal IDocumentCatalog Catalog { get; }
        internal DocumentCollectionMetadata Collection { get; } = new(Guid.NewGuid(), "items");

        private Fixture()
        {
            Storage = DocumentStorage.Create(Data, Journal, new MemoryStream(), "test");
            Coordinator = new TransactionCoordinator(Storage, Storage.WriteAheadJournal, Storage.Records);
            Catalog = DocumentCatalog.Open(Storage, Coordinator);
        }

        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
            await fixture.Catalog.SaveCollectionAsync(fixture.Collection, writer);
            await fixture.Coordinator.CommitAsync(writer);
            return fixture;
        }

        internal async Task CreateIndex(string name, string path)
        {
            var writer = await Coordinator.BeginAsync(IsolationLevel.Snapshot);
            await Catalog.CreateIndexAsync(Collection.Id, name, path, writer);
            await Coordinator.CommitAsync(writer);
        }

        internal async Task Write(string id, string json)
        {
            var writer = await Coordinator.BeginAsync(IsolationLevel.Snapshot);
            await WriteIn(writer, id, json);
            await Coordinator.CommitAsync(writer);
        }

        internal async Task WriteIn(ITransactionContext writer, string id, string json)
        {
            var previous = Catalog.FindDocument(Collection.Id, id, writer.Snapshot);
            var content = await Storage.WriteContentAsync(Coordinator, writer, Encoding.UTF8.GetBytes(json));
            await Catalog.SaveDocumentAsync(new DocumentCatalogEntry(Collection.Id, id, previous is { } old ? old.Version + 1 : 1,
                content.Head, content.Length, content.Checksum), writer);
            if (previous is { } prior)
            {
                await Storage.TombstoneContentAsync(Coordinator, writer, Content(prior));
            }
        }

        internal async Task<string[]> Search(string name, object? lower, bool includeLower, object? upper, bool includeUpper)
        {
            var reader = await Coordinator.BeginAsync(IsolationLevel.Snapshot);
            var matches = await Catalog.SearchIndexAsync(Collection.Id, name, lower, includeLower, upper, includeUpper, reader.Snapshot);
            await Coordinator.CommitAsync(reader);
            return matches.Select(document => document.Id).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            Storage.Dispose();
        }
    }
}
