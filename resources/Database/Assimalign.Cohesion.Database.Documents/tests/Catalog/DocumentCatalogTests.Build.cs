using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Catalog] - Build buffers one document's postings independently of collection size (#1530)")]
    [InlineData(1)]
    [InlineData(12)]
    public async Task CreateIndexAsync_ArrayBuild_ShouldBoundBufferedPostings(int documents)
    {
        // Arrange.
        await using var f = await Fixture.Create();
        string json = "{\"items\":[" + string.Join(',', Enumerable.Range(0, 128)) + "]}";
        for (int i = 0; i < documents; i++) { await f.Write($"doc{i}", json); }
        int peak = 0, prepared = 0, applied = 0;
        f.Catalog.IndexBuildPostingsBuffered = count => { peak = Math.Max(peak, count); prepared++; };
        f.Catalog.AfterIndexPostingApplied = count => applied = count;

        // Act.
        var metadata = await CreateTypedIndexAsync(f, "ix", [DocumentIndexKey.DistinctArray("items")]);

        // Assert: the staging bound counts keys, excluding persisted tree pages and source metadata.
        prepared.ShouldBe(documents);
        peak.ShouldBe(128);
        applied.ShouldBe(documents * 128);
        (await ReadRawKeysAsync(f, metadata, Latest())).Count.ShouldBe(documents * 128);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Build extracts postings outside the catalog monitor (#1530)")]
    public async Task CreateIndexAsync_PostingExtraction_ShouldReleaseCatalogMonitor()
    {
        // Arrange.
        await using var f = await Fixture.Create();
        await f.Write("a", "{\"items\":[1,2,3]}");
        int reads = 0, lockedReads = 0;
        f.Catalog.BeforeIndexKeysRead = held => { reads++; if (held) { lockedReads++; } };

        // Act.
        await CreateTypedIndexAsync(f, "ix", [DocumentIndexKey.DistinctArray("items")]);

        // Assert.
        reads.ShouldBe(1);
        lockedReads.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Catalog] - Oversize definitions refuse before extraction or physical writes (#1530)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateIndexAsync_OversizeDefinition_ShouldRefuseBeforeBuild(bool predicate)
    {
        // Arrange: both large WHEN constants and large path lists encode beyond 8,092 bytes.
        await using var f = await Fixture.Create();
        await f.Write("a", "{\"items\":[1,2,3],\"value\":1}");
        var keys = predicate
            ? new[] { DocumentIndexKey.DistinctArray("items", "", DocumentIndexPredicate.Compare("", DocumentIndexComparison.NotEqual, new string('x', 8100))) }
            : Enumerable.Range(0, 32).Select(i => DocumentIndexKey.Scalar(new string('p', 260) + i)).ToArray();
        int reads = 0, applied = 0;
        f.Catalog.BeforeIndexKeysRead = _ => reads++;
        f.Catalog.AfterIndexPostingApplied = _ => applied++;
        var writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        byte[] before = f.Data.ToArray();

        // Act.
        var failure = await Should.ThrowAsync<DocumentCatalogException>(async () =>
            await f.Catalog.CreateIndexAsync(f.Collection.Id, "large", keys, false, false, writer));

        // Assert.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD03026");
        reads.ShouldBe(0);
        applied.ShouldBe(0);
        f.Data.ToArray().ShouldBe(before);
        f.Catalog.GetIndexes(f.Collection.Id, writer.Snapshot).ShouldBeEmpty();
        await f.Coordinator.RollbackAsync(writer);
        await CreateTypedIndexAsync(f, "small", [DocumentIndexKey.Scalar("value")]);
        (await f.Search("small", 1m, true, 1m, true)).ShouldBe(["a"]);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Leading array INCLUDE MISSING is refused before publication (#1530, #1632)")]
    public async Task CreateIndexAsync_LeadingArrayIncludeMissing_ShouldRefuseUndefinedCoverage()
    {
        // Arrange.
        await using var f = await Fixture.Create();
        await f.Write("empty", "{\"tags\":[]}");
        var writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act.
        var failure = await Should.ThrowAsync<DocumentCatalogException>(async () =>
            await f.Catalog.CreateIndexAsync(f.Collection.Id, "ix", [DocumentIndexKey.DistinctArray("tags")], false, true, writer));
        var metadataFailure = Should.Throw<DocumentCatalogException>(() =>
            new DocumentIndexMetadata(f.Collection.Id, "ix", [DocumentIndexKey.DistinctArray("tags")], false, true, 1));

        // Assert.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD03025");
        metadataFailure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD03025");
        f.Catalog.GetIndexes(f.Collection.Id, writer.Snapshot).ShouldBeEmpty();
        await f.Coordinator.RollbackAsync(writer);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - INCLUDE MISSING remains valid with a scalar leading key and a trailing array (#1530)")]
    public async Task CreateIndexAsync_ScalarLeadingArrayIncludeMissing_ShouldRetainSupportedDefinition()
    {
        // Arrange.
        await using var f = await Fixture.Create();
        await f.Write("a", "{\"items\":[1,2]}");

        // Act.
        var metadata = await CreateTypedIndexAsync(f, "ix",
            [DocumentIndexKey.Scalar("tenant"), DocumentIndexKey.DistinctArray("items")], includeMissing: true);
        var reopened = DocumentCatalog.Open(f.Storage, f.Coordinator).GetIndexes(f.Collection.Id, Latest()).Single();

        // Assert.
        (await ReadRawKeysAsync(f, metadata, Latest())).Count.ShouldBe(2);
        reopened.ShouldBe(metadata);
        reopened.IncludeMissing.ShouldBeTrue();
    }
}
