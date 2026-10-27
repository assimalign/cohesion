using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Catalog] - WHEN scalar comparisons follow OQL type and unknown rules (#1530)")]
    [InlineData(DocumentIndexComparison.Equal, "3")]
    [InlineData(DocumentIndexComparison.NotEqual, "0,2,4,5")]
    [InlineData(DocumentIndexComparison.LessThan, "2")]
    [InlineData(DocumentIndexComparison.LessThanOrEqual, "2,3")]
    [InlineData(DocumentIndexComparison.GreaterThan, "4")]
    [InlineData(DocumentIndexComparison.GreaterThanOrEqual, "3,4")]
    public async Task CreateIndexAsync_WhenComparison_ShouldUseScalarTypeEquality(DocumentIndexComparison operation, string expected)
    {
        // Arrange: unknowns never qualify; mismatched valued kinds are unequal, but not ordered.
        await using var f = await Fixture.Create();
        await f.Write("a", "{\"items\":[{\"p\":false,\"q\":0},{\"p\":null,\"q\":1},{\"p\":1,\"q\":2},{\"p\":2.00,\"q\":3},{\"p\":3,\"q\":4},{\"p\":\"2\",\"q\":5},{\"q\":6}]}");

        // Act.
        var metadata = await CreateTypedIndexAsync(f, "ix", [DocumentIndexKey.DistinctArray("items", "q", DocumentIndexPredicate.Compare("p", operation, 2))]);
        var keys = await ReadRawKeysAsync(f, metadata, Latest());

        // Assert.
        keys.ShouldBe(expected.Split(',').Select(value => DocumentIndexKeys.Encode(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Logical WHEN nodes preserve TRUE admission and round trip (#1530)")]
    public async Task Open_LogicalWhen_ShouldPreservePredicateAndValueEquality()
    {
        // Arrange.
        await using var f = await Fixture.Create();
        await f.Write("a", "{\"items\":[{\"p\":null,\"q\":1},{\"q\":2},{\"p\":false,\"q\":3},{\"p\":\"x\",\"q\":4},{\"p\":\"y\",\"q\":5}]}");
        var when = DocumentIndexPredicate.Or(DocumentIndexPredicate.IsMissing("p"),
            DocumentIndexPredicate.And(DocumentIndexPredicate.Not(DocumentIndexPredicate.IsNull("p")),
                DocumentIndexPredicate.Compare("p", DocumentIndexComparison.GreaterThan, "w")));
        var keys = new[] { DocumentIndexKey.DistinctArray("items", "q", when) };
        var metadata = await CreateTypedIndexAsync(f, "ix", keys, includeMissing: true);
        keys[0] = DocumentIndexKey.Scalar("changed");

        // Act.
        var decoded = DocumentCatalog.Open(f.Storage, f.Coordinator).GetIndexes(f.Collection.Id, Latest()).Single();

        // Assert.
        decoded.ShouldBe(metadata);
        decoded.GetHashCode().ShouldBe(metadata.GetHashCode());
        (await ReadRawKeysAsync(f, decoded, Latest())).ShouldBe(new[] { 2m, 4m, 5m }.Select(value => DocumentIndexKeys.Encode(value)));
        var repeated = DocumentIndexPredicate.Compare("", DocumentIndexComparison.Equal, true);
        for (int i = 1; i < 32; i++) { repeated = DocumentIndexPredicate.Not(repeated); }
        Should.Throw<ArgumentException>(() => DocumentIndexPredicate.Not(repeated));
        var wide = DocumentIndexPredicate.Compare("", DocumentIndexComparison.Equal, true);
        for (int i = 0; i < 6; i++) { wide = DocumentIndexPredicate.And(wide, wide); }
        Should.Throw<ArgumentException>(() => DocumentIndexPredicate.Or(wide, wide));
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Dropped and recreated multikey definitions preserve old snapshots (#1530)")]
    public async Task DeleteIndexAsync_ArrayRecreation_ShouldRetainOldGeneration()
    {
        // Arrange.
        await using var f = await Fixture.Create();
        await f.Write("a", "{\"items\":[1,2],\"other\":[9]}");
        var metadata = await CreateTypedIndexAsync(f, "ix", [DocumentIndexKey.DistinctArray("items")]);
        var old = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await f.Catalog.DeleteIndexAsync(f.Collection.Id, "ix", writer);
        await f.Coordinator.RollbackAsync(writer);
        writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act.
        await f.Catalog.DeleteIndexAsync(f.Collection.Id, "ix", writer);
        await f.Coordinator.CommitAsync(writer);
        var replacement = await CreateTypedIndexAsync(f, "ix", [DocumentIndexKey.DistinctArray("other")]);

        // Assert.
        replacement.ObjectId.ShouldNotBe(metadata.ObjectId);
        (await f.Catalog.SearchIndexAsync(f.Collection.Id, "ix", 1m, true, 2m, true, old.Snapshot)).Single().Id.ShouldBe("a");
        (await f.Search("ix", 1m, true, 2m, true)).ShouldBeEmpty();
        (await f.Search("ix", 9m, true, 9m, true)).ShouldBe(["a"]);
        await f.Coordinator.RollbackAsync(old);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Array keys deduplicate scalar postings and retain source semantics (#1530)")]
    public async Task CreateIndexAsync_ArrayShapes_ShouldStoreDistinctScalarPostings()
    {
        // Arrange: build over existing documents, then maintain a new document.
        await using var f = await Fixture.Create();
        await f.Write("a", "{\"items\":[1,1.00,2,null,null,[],{}]}");
        await f.Write("empty", "{\"items\":[]}");
        await f.Write("missing", "{}");
        await f.Write("null", "{\"items\":null}");
        await f.Write("scalar", "{\"items\":7}");
        var metadata = await CreateTypedIndexAsync(f, "ix", [DocumentIndexKey.DistinctArray("items")]);

        // Act.
        await f.Write("b", "{\"items\":[1,2,3,3]}");
        var keys = await ReadRawKeysAsync(f, metadata, Latest());

        // Assert: 3 + 0 + 0 + 1 + 1 + 3 entries; no complex element tags.
        keys.Count.ShouldBe(8);
        keys.Count(key => key.Equals(DocumentIndexKeys.Encode(1m))).ShouldBe(2);
        keys.Count(key => key.Equals(new IndexKey(new byte[] { 1 }))).ShouldBe(3);
        (await f.Search("ix", 1m, true, 3m, true)).ShouldBe(["a", "b"]);
        (await f.Search("ix", null, true, null, true)).ShouldBe(["a", "b", "null", "scalar"]);
        metadata.Paths.ShouldBeEmpty(); // Scalar-only planning must not infer document coverage.
        metadata.Keys.Single().ArraySource.ShouldBe("items");
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - WHEN filters bind every conjunct to the same array element (#1530)")]
    public async Task CreateIndexAsync_ElementPredicate_ShouldRejectCrossElementMatches()
    {
        // Arrange.
        await using var f = await Fixture.Create();
        await f.Write("cross", "{\"items\":[{\"price\":20,\"stock\":0},{\"price\":1,\"stock\":5}]}");
        await f.Write("same", "{\"items\":[{\"price\":20,\"stock\":5},{\"price\":20.0,\"stock\":8},{\"stock\":9}]}");
        var when = DocumentIndexPredicate.And(
            DocumentIndexPredicate.Compare("price", DocumentIndexComparison.GreaterThan, 10),
            DocumentIndexPredicate.Compare("stock", DocumentIndexComparison.GreaterThan, 0));

        // Act: the filter restricts emitted prices, not just candidate documents.
        var definition = await CreateTypedIndexAsync(f, "prices", [DocumentIndexKey.DistinctArray("items", "price", when)]);
        var reopened = DocumentCatalog.Open(f.Storage, f.Coordinator);

        // Assert.
        (await f.Search("prices", 10m, false, null, false)).ShouldBe(["same"]);
        (await ReadRawKeysAsync(f, definition, Latest())).ShouldHaveSingleItem().ShouldBe(DocumentIndexKeys.Encode(20m));
        reopened.GetIndexes(f.Collection.Id, Latest()).Single().ShouldBe(definition);
        using var json = JsonDocument.Parse(f.Storage.ReadContent(Content(f.Catalog.FindDocument(f.Collection.Id, "same", Latest())!.Value)));
        json.RootElement.GetProperty("items").EnumerateArray().Count(item => item.TryGetProperty("price", out var price) && price.GetDecimal() == 20m).ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Catalog] - Array key components work at every compound position (#1530)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SearchIndexAsync_CompoundArrayKey_ShouldRespectPrefixAndRange(int position)
    {
        // Arrange.
        await using var f = await Fixture.Create();
        await f.Write("a", "{\"tenant\":7,\"tail\":9,\"items\":[2,2,4]}");
        await f.Write("b", "{\"tenant\":8,\"tail\":9,\"items\":[3]}");
        var keys = new List<DocumentIndexKey> { DocumentIndexKey.Scalar("tenant"), DocumentIndexKey.Scalar("tail") };
        keys.Insert(position, DocumentIndexKey.DistinctArray("items"));
        var metadata = await CreateTypedIndexAsync(f, "ix", keys);
        object[] prefix = position switch { 0 => [], 1 => [7m], _ => [7m, 9m] };

        // Act.
        var matches = await f.Catalog.SearchIndexAsync(f.Collection.Id, "ix", prefix, 2m, true, 4m, false, Latest());

        // Assert.
        matches.Select(document => document.Id).ShouldBe(position == 0 ? ["a", "b"] : ["a"]);
        (await ReadRawKeysAsync(f, metadata, Latest())).Count.ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Catalog] - INCLUDE MISSING maintains leading keys for scalar and array definitions (#1530, #1632)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveDocumentAsync_IncludeMissing_ShouldRetainMissingLeadingKeys(bool array)
    {
        // Arrange.
        await using var f = await Fixture.Create();
        var key = array ? DocumentIndexKey.DistinctArray("a", "p") : DocumentIndexKey.Scalar("a.p");
        var sparse = await CreateTypedIndexAsync(f, "sparse", [key]);
        var inclusive = await CreateTypedIndexAsync(f, "inclusive", [key], includeMissing: true);
        await f.Write("missing", "{}");
        await f.Write("wrong-shape", "{\"a\":null}");
        await f.Write("empty", array ? "{\"a\":[]}" : "{\"a\":{}}");
        await f.Write("projection-missing", array ? "{\"a\":[{}]}" : "{\"a\":{}}");
        await f.Write("value", array ? "{\"a\":[{\"p\":2}]}" : "{\"a\":{\"p\":2}}");
        var reader = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act: replacement of the missing key must leave its old posting visible.
        await f.Write("missing", array ? "{\"a\":[{\"p\":3}]}" : "{\"a\":{\"p\":3}}");
        var old = await ReadRawKeysAsync(f, inclusive, reader.Snapshot);
        var current = await ReadRawKeysAsync(f, inclusive, Latest());

        // Assert: empty/filtered arrays and omitted element projections have no synthetic posting.
        old.Count.ShouldBe(array ? 3 : 5);
        old.Count(item => item.Equals(new IndexKey(new byte[] { 0 }))).ShouldBe(array ? 1 : 4);
        current.Count(item => item.Equals(new IndexKey(new byte[] { 0 }))).ShouldBe(array ? 0 : 3);
        (await ReadRawKeysAsync(f, sparse, Latest())).Count.ShouldBe(array ? 3 : 2);
        DocumentCatalog.Open(f.Storage, f.Coordinator).GetIndexes(f.Collection.Id, Latest()).Single(item => item.Name == "inclusive")
            .IncludeMissing.ShouldBeTrue();
        await f.Coordinator.RollbackAsync(reader);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Replacement, delete and rollback preserve every multikey version (#1530)")]
    public async Task SaveDocumentAsync_ArrayVersions_ShouldSurviveRollbackDeletionAndPurge()
    {
        // Arrange.
        await using var f = await Fixture.Create();
        var metadata = await CreateTypedIndexAsync(f, "ix", [DocumentIndexKey.DistinctArray("items")]);
        await f.Write("a", "{\"items\":[1,1,2]}");
        var old = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act.
        await f.Write("a", "{\"items\":[2,3,3]}");
        var rollback = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await f.WriteIn(rollback, "a", "{\"items\":[4,5]}");
        await f.WriteIn(rollback, "a", "{\"items\":[5,6]}");
        await f.Coordinator.RollbackAsync(rollback);
        var deleting = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await f.Catalog.DeleteDocumentAsync(f.Collection.Id, "a", deleting);
        (await ReadRawKeysAsync(f, metadata, deleting.Snapshot)).ShouldBeEmpty();
        await f.Coordinator.RollbackAsync(deleting);

        // Assert: the overlap at 2 is still versioned, even though its value did not change.
        (await ReadRawKeysAsync(f, metadata, Latest())).OrderBy(key => key).ShouldBe(new[] { DocumentIndexKeys.Encode(2m), DocumentIndexKeys.Encode(3m) }.OrderBy(key => key));
        (await ReadRawKeysAsync(f, metadata, old.Snapshot)).OrderBy(key => key).ShouldBe(new[] { DocumentIndexKeys.Encode(1m), DocumentIndexKeys.Encode(2m) }.OrderBy(key => key));
        deleting = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await f.Catalog.DeleteDocumentAsync(f.Collection.Id, "a", deleting);
        await f.Coordinator.CommitAsync(deleting);
        (await ReadRawKeysAsync(f, metadata, Latest())).ShouldBeEmpty();
        (await f.Catalog.SearchIndexAsync(f.Collection.Id, "ix", null, true, null, true, old.Snapshot)).Single().Id.ShouldBe("a");
        await f.Coordinator.RollbackAsync(old);
        f.Coordinator.RunVersionPurgePass(default).ShouldBeGreaterThan(0);
        (await ReadRawKeysAsync(f, metadata, Latest())).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Catalog] - Recovery scrubs every abandoned array posting after splits (#1530)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverIndexesAsync_ArrayMutation_ShouldKeepOnlyCommittedPostings(bool commit)
    {
        // Arrange.
        await using var f = await Fixture.Create();
        var metadata = await CreateTypedIndexAsync(f, "ix", [DocumentIndexKey.DistinctArray("items")]);
        await f.Write("large", "{\"items\":[" + string.Join(',', Enumerable.Range(0, 240)) + "]}");
        await f.Write("deleted", "{\"items\":[500,501]}");
        var writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await f.WriteIn(writer, "large", "{\"items\":[900,901,901]}");
        await f.WriteIn(writer, "new", "{\"items\":[902,903]}");
        await f.Catalog.DeleteDocumentAsync(f.Collection.Id, "deleted", writer);
        if (commit) { await f.Coordinator.CommitAsync(writer); }
        f.Storage.WriteAheadJournal.Flush(forceDurable: false);

        // Act.
        using var storage = DocumentStorage.Open(Clone(f.Data), Clone(f.Journal), new MemoryStream(), false);
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        DocumentCatalog.EnsureIndexFormat(storage);
        var plan = coordinator.AnalyzeAndScrub();
        var catalog = DocumentCatalog.Open(storage, coordinator);
        await catalog.RecoverIndexesAsync(plan.Aborted);
        coordinator.CompleteRecovery();

        // Assert: raw trees bypass catalog filtering, exposing stray stamps or duplicate postings.
        var keys = await ReadRawKeysAsync(storage, metadata, Latest());
        keys.Count.ShouldBe(commit ? 4 : 242);
        keys.Contains(DocumentIndexKeys.Encode(901m)).ShouldBe(commit);
        keys.Contains(DocumentIndexKeys.Encode(1m)).ShouldBe(!commit);
        keys.Contains(DocumentIndexKeys.Encode(500m)).ShouldBe(!commit);
        plan.Aborted.Contains(writer.Sequence).ShouldBe(!commit);
        if (!commit) { await f.Coordinator.RollbackAsync(writer); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Catalog] - Partial multikey apply failure restores metadata, trees and roots (#1530)")]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ApplyAsync_PartialArrayFailure_ShouldRollbackAndRelease(bool build, bool cancel, bool last)
    {
        // Arrange.
        await using var f = await Fixture.Create();
        await f.Write("a", "{\"items\":[1,2,3]}");
        if (!build) { await CreateTypedIndexAsync(f, "ix", [DocumentIndexKey.DistinctArray("items")]); }
        var writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var content = await f.Storage.WriteContentAsync(f.Coordinator, writer, "{\"items\":[4,5,6]}"u8.ToArray());
        using var canceled = new CancellationTokenSource();
        int observed = 0;
        int target = last ? build ? 3 : 6 : 2;
        f.Catalog.AfterIndexPostingApplied = count =>
        {
            observed = count;
            if (count != target) { return; }
            if (cancel) { canceled.Cancel(); }
            else { throw new IOException("Injected posting failure."); }
        };

        // Act.
        try
        {
            async Task ApplyAsync()
            {
                if (build)
                { await f.Catalog.CreateIndexAsync(f.Collection.Id, "ix", [DocumentIndexKey.DistinctArray("items")], false, false, writer, canceled.Token); }
                else
                { await f.Catalog.SaveDocumentAsync(new DocumentCatalogEntry(f.Collection.Id, "a", 2, content.Head, content.Length, content.Checksum), writer, canceled.Token); }
            }
            if (cancel) { await Should.ThrowAsync<OperationCanceledException>(ApplyAsync); }
            else { await Should.ThrowAsync<IOException>(ApplyAsync); }
        }
        finally
        {
            f.Catalog.AfterIndexPostingApplied = null;
            await f.Coordinator.RollbackAsync(writer);
        }

        // Assert: following work proves the apply gate, brackets and tree latches were released.
        observed.ShouldBe(target);
        f.Catalog.FindDocument(f.Collection.Id, "a", Latest())!.Value.Version.ShouldBe(1UL);
        if (build) { await CreateTypedIndexAsync(f, "ix", [DocumentIndexKey.DistinctArray("items")]); }
        (await f.Search("ix", 1m, true, 3m, true)).ShouldBe(["a"]);
        (await f.Search("ix", 4m, true, 6m, true)).ShouldBeEmpty();
        await f.Write("b", "{\"items\":[7,8]}");
        (await f.Search("ix", 7m, true, 8m, true)).ShouldBe(["b"]);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Oversize elements and canceled builds publish nothing (#1530)")]
    public async Task CreateIndexAsync_OversizeOrCanceled_ShouldLeaveDefinitionAndPostingsUnchanged()
    {
        // Arrange.
        await using var f = await Fixture.Create();
        await CreateTypedIndexAsync(f, "ix", [DocumentIndexKey.DistinctArray("items")]);
        await f.Write("a", "{\"items\":[1,2]}");
        var writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act.
        var failure = await Should.ThrowAsync<DocumentCatalogException>(() => f.WriteIn(writer, "a", "{\"items\":[3,\"" + new string('x', 2000) + "\"]}"));
        await f.Coordinator.RollbackAsync(writer);
        writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await f.Catalog.CreateIndexAsync(f.Collection.Id, "canceled",
            [DocumentIndexKey.DistinctArray("items")], false, false, writer, canceled.Token));
        await f.Coordinator.RollbackAsync(writer);

        // Assert.
        failure.Diagnostics.Single().Code.ShouldBe("CD02026");
        f.Catalog.GetIndexes(f.Collection.Id, Latest()).Single().Name.ShouldBe("ix");
        (await f.Search("ix", 1m, true, 2m, true)).ShouldBe(["a"]);
        (await f.Search("ix", 3m, true, 3m, true)).ShouldBeEmpty();
        await f.Write("oversize", "{\"other\":[\"" + new string('x', 2000) + "\"]}");
        writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var buildFailure = await Should.ThrowAsync<DocumentCatalogException>(async () => await f.Catalog.CreateIndexAsync(f.Collection.Id, "large",
            [DocumentIndexKey.DistinctArray("other")], false, false, writer));
        await f.Coordinator.RollbackAsync(writer);
        buildFailure.Diagnostics.Single().Code.ShouldBe("CD02026");
        f.Catalog.GetIndexes(f.Collection.Id, Latest()).Single().Name.ShouldBe("ix");
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Array descriptor restrictions fail with CD03025 (#1530)")]
    public async Task CreateIndexAsync_InvalidArrayDefinition_ShouldRefuseBeforePublication()
    {
        // Arrange.
        await using var f = await Fixture.Create();
        var writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act.
        var unique = await Should.ThrowAsync<DocumentCatalogException>(async () => await f.Catalog.CreateIndexAsync(f.Collection.Id,
            "unique", [DocumentIndexKey.DistinctArray("items")], true, false, writer));
        var multiple = await Should.ThrowAsync<DocumentCatalogException>(async () => await f.Catalog.CreateIndexAsync(f.Collection.Id,
            "multiple", [DocumentIndexKey.DistinctArray("items"), DocumentIndexKey.DistinctArray("other")], false, false, writer));

        // Assert.
        unique.Diagnostics.Single().Code.ShouldBe("CD03025");
        multiple.Diagnostics.Single().Code.ShouldBe("CD03025");
        f.Catalog.GetIndexes(f.Collection.Id, writer.Snapshot).ShouldBeEmpty();
        await f.Coordinator.RollbackAsync(writer);
    }

    private static async Task<DocumentIndexMetadata> CreateTypedIndexAsync(Fixture fixture, string name,
        IReadOnlyList<DocumentIndexKey> keys, bool includeMissing = false, CancellationToken cancellationToken = default)
    {
        var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken);
        try
        {
            var result = await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, name, keys, false, includeMissing, writer, cancellationToken);
            await fixture.Coordinator.CommitAsync(writer, cancellationToken);
            return result;
        }
        finally { if (writer.State == TransactionState.Active) { await fixture.Coordinator.RollbackAsync(writer); } }
    }

    private static TransactionSnapshot Latest() => new(TransactionSequence.None, TransactionSequence.None, new TransactionSequence(ulong.MaxValue), []);

    private static Task<List<IndexKey>> ReadRawKeysAsync(Fixture fixture, DocumentIndexMetadata metadata, TransactionSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => ReadRawKeysAsync(fixture.Storage, metadata, snapshot, cancellationToken);

    private static async Task<List<IndexKey>> ReadRawKeysAsync(DocumentStorage storage, DocumentIndexMetadata metadata, TransactionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        using var registrations = storage.GetUnitIterator(1);
        long root = 0;
        while (registrations.MoveNext())
        {
            var data = registrations.Current.Data;
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.Span[18..]) == metadata.ObjectId)
            { root = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span[26..]); }
        }
        root.ShouldBeGreaterThan(0);
        var manager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = storage, TransactionSource = _ => throw new InvalidOperationException("Raw tree reads only."),
            ExistingIndexes = [new BTreeIndexRegistration(metadata.ObjectId, new IndexDefinition(metadata.Name), root)]
        });
        manager.TryGetIndex(metadata.ObjectId, metadata.Name, out var tree).ShouldBeTrue();
        var result = new List<IndexKey>();
        await using var cursor = tree.OpenCursor(snapshot, new IndexKeyRange(null, null, true, true), cancellationToken: cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken)) { result.Add(cursor.CurrentKey); }
        return result;
    }
}
