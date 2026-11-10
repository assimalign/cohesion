using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Escaped equality admits pooled comparison workspace before decoding (#1808)")]
    public void Read_Fix4EscapedEquality_ShouldReserveBeforeComparing()
    {
        // Arrange: the raw operand is five bytes beyond six times the constant's
        // UTF-8 length. ValueEquals can still attempt unescaping at this boundary.
        byte[] content = Encoding.UTF8.GetBytes("{\"variants\":[{\"name\":\"" +
            string.Concat(Enumerable.Repeat("\\u0001", 300)) + "aaaaa\",\"price\":1}]}");
        DocumentIndexMetadata Metadata(string constant) => new(Guid.NewGuid(), "ix",
            [DocumentIndexKey.DistinctArray("variants", "price",
                DocumentIndexPredicate.Compare("name", DocumentIndexComparison.Equal, constant))], false, false, 1000);
        long baseline = 0;
        DocumentIndexKeys.Read(content, Metadata("x"), out _, reserve: bytes => baseline += bytes).ShouldBeEmpty();
        var metadata = Metadata(new string('\u0800', 100));
        long charge = 0, limit = baseline + 1024;
        var refused = new OperationCanceledException();
        // Act / Assert: one KiB cannot admit both pooled comparison buffers.
        var error = Should.Throw<OperationCanceledException>(() => DocumentIndexKeys.Read(content, metadata, out _, reserve: bytes =>
        {
            if (bytes > limit - charge) { throw refused; }
            charge += bytes;
        }));
        error.ShouldBeSameAs(refused);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Accessed build numbers round-trip by the stored-content rule (#1808)")]
    [InlineData("1.0000000000000000000000000000001")]
    [InlineData("1e-29")] [InlineData("1e100")]
    [InlineData("-0.000e-100")] [InlineData("1.2300e2")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001")]
    [InlineData("-1000.000")] [InlineData("0e999999999999999999999")]
    public void Read_Fix4AccessedNumber_ShouldMatchValidation(string number)
    {
        // Arrange: exercise scalar, array source/projection and WHEN operand access.
        byte[] content = Encoding.UTF8.GetBytes("{\"v\":" + number + ",\"variants\":[{\"n\":" + number + ",\"price\":1}]}");
        bool valid = true;
        try { DocumentStorage.ValidateContent(content); } catch (JsonException) { valid = false; }
        foreach (var key in new[] { DocumentIndexKey.Scalar("v"), DocumentIndexKey.DistinctArray("v"), DocumentIndexKey.DistinctArray("variants", "n"),
            DocumentIndexKey.DistinctArray("variants", "price", DocumentIndexPredicate.Compare("n", DocumentIndexComparison.Equal, 1m)) })
        {
            var metadata = new DocumentIndexMetadata(Guid.NewGuid(), "ix", [key], false, false, 1000);
            // Act / Assert.
            if (valid) { DocumentIndexKeys.Read(content, metadata, out _, reserve: _ => { }); }
            else
            {
                var error = Should.Throw<Exception>(() => DocumentIndexKeys.Read(content, metadata, out _, reserve: _ => { }));
                (error is JsonException or FormatException).ShouldBeTrue();
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Build WHEN strings stay within charged allocations (#1808)")]
    [InlineData(false)] [InlineData(true)]
    public async Task CreateIndexAsync_Fix4WhenString_ShouldBoundAllocations(bool ordering)
    {
        // Arrange: the content and fixture exist before the build measurement.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var fixture = await Fixture.Create();
        fixture.Catalog.IndexBuildMemoryBudget = 64L * 1024 * 1024;
        string json = "{\"variants\":[{\"name\":\"" + new string('a', 20 * 1024 * 1024) + "\",\"price\":1}]}";
        await fixture.Write("one", json);
        var keys = new[] { DocumentIndexKey.DistinctArray("variants", "price",
            DocumentIndexPredicate.Compare("name", ordering ? DocumentIndexComparison.LessThan : DocumentIndexComparison.Equal, "x")) };
        long extractionCharge = 0;
        fixture.Catalog.IndexBuildStateCaptured = (phase, _, bytes) => { if (phase == "keys") { extractionCharge = bytes; } };
        var context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        try
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetTotalAllocatedBytes(true);
            // Act.
            Exception? failure = null;
            try { await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "ix", keys, false, false, context, guard.Token); }
            catch (DocumentCatalogException error) { failure = error; }
            long allocated = GC.GetTotalAllocatedBytes(true) - before;
            // Assert: the content plus 32 MiB initial DOM reservation are charged
            // before either comparison. One MiB covers fixed catalog/JIT work.
            long predecodeCharge = 32L + Encoding.UTF8.GetByteCount(json) + 32L * 1024 * 1024;
            _output.WriteLine($"WHEN_ALLOCATION|ordering={ordering}|allocated={allocated}|predecodeCharge={predecodeCharge}|extractionCharge={extractionCharge}");
            allocated.ShouldBeLessThanOrEqualTo(predecodeCharge + 1024 * 1024);
            if (ordering) { failure.ShouldBeOfType<DocumentCatalogException>().Code.ShouldBe("CD05018"); }
            else { failure.ShouldBeNull(); extractionCharge.ShouldBeGreaterThanOrEqualTo(predecodeCharge); }
        }
        finally { await fixture.Coordinator.RollbackAsync(context, guard.Token); GC.KeepAlive(json); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Oversized scalar components refuse only formed keys in builds and PUT (#1808)")]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task CreateIndexAsync_Fix4ArrayComposite_ShouldMatchPut(bool element, bool put)
    {
        // Arrange.
        await using var fixture = await Fixture.Create();
        string json = "{\"category\":\"" + new string('x', 341) + "\",\"variants\":" + (element ? "[{\"price\":1}]" : "[]") + "}";
        var keys = new[] { DocumentIndexKey.Scalar("category"), DocumentIndexKey.DistinctArray("variants", "price") };
        var context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        try
        {
            if (!put) { await fixture.Write("one", json); }
            if (put) { await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "ix", keys, false, false, context, CancellationToken.None); }
            // Act / Assert.
            async Task Act()
            {
                if (put) { await fixture.WriteIn(context, "one", json); }
                else { await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "ix", keys, false, false, context, CancellationToken.None); }
            }
            // Refresh visibility after the separately committed document write.
            if (!put) { await fixture.Coordinator.RollbackAsync(context); context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot); }
            if (element) { (await Should.ThrowAsync<DocumentCatalogException>(Act)).Code.ShouldBe("CD02026"); }
            else
            {
                await Act();
                (await fixture.Catalog.SearchIndexAsync(fixture.Collection.Id, "ix", null, false, null, false,
                    context.Snapshot, CancellationToken.None)).ShouldBeEmpty();
            }
        }
        finally { await fixture.Coordinator.RollbackAsync(context, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UNIQUE non-scalar refusal precedes composite size in builds and PUT (#1808)")]
    [InlineData(false)] [InlineData(true)]
    public async Task CreateIndexAsync_Fix4UniquePrecedence_ShouldMatchPut(bool put)
    {
        // Arrange.
        await using var fixture = await Fixture.Create();
        string json = "{\"a\":\"" + new string('x', 341) + "\",\"b\":{\"k\":1}}";
        if (!put) { await fixture.Write("one", json); }
        var context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var keys = new[] { DocumentIndexKey.Scalar("a"), DocumentIndexKey.Scalar("b") };
        try
        {
            if (put) { await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "ix", keys, true, false, context, CancellationToken.None); }
            // Act / Assert.
            var error = await Should.ThrowAsync<DocumentConstraintViolationException>(async () =>
            {
                if (put) { await fixture.WriteIn(context, "one", json); }
                else { await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "ix", keys, true, false, context, CancellationToken.None); }
            });
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02021");
        }
        finally { await fixture.Coordinator.RollbackAsync(context); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Escaped WHEN equality matches stored ordinal comparison (#1808)")]
    [InlineData("x", "x")] [InlineData("\\u0078", "x")]
    [InlineData("\\uD83D\\uDE00", "😀")] [InlineData("\\n\\\"\\\\", "\n\"\\")]
    [InlineData("\\u0061\\u0062", "ac")] [InlineData("é", "e\u0301")]
    public void Read_Fix4EscapedWhen_ShouldMatchStored(string raw, string constant)
    {
        // Arrange.
        byte[] content = Encoding.UTF8.GetBytes("{\"variants\":[{\"name\":\"" + raw + "\",\"price\":1}]}");
        foreach (var comparison in new[] { DocumentIndexComparison.Equal, DocumentIndexComparison.NotEqual })
        {
            var metadata = new DocumentIndexMetadata(Guid.NewGuid(), "ix", [DocumentIndexKey.DistinctArray("variants", "price",
                DocumentIndexPredicate.Compare("name", comparison, constant))], false, false, 1000);
            // Act / Assert.
            var stored = DocumentIndexKeys.Read(content, metadata, out _);
            DocumentIndexKeys.Read(content, metadata, out _, reserve: _ => { }).ShouldBe(stored, ignoreOrder: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Ordinary WHEN maintenance keeps TryGetProperty semantics (#1808)")]
    public void Read_Fix4StoredWhen_ShouldKeepLastProperty()
    {
        // Arrange: raw extraction isolates the stored walk from write validation.
        byte[] content = Encoding.UTF8.GetBytes("{\"variants\":[{\"name\":\"a\",\"name\":\"x\",\"price\":1}]}");
        var metadata = new DocumentIndexMetadata(Guid.NewGuid(), "ix", [DocumentIndexKey.DistinctArray("variants", "price",
            DocumentIndexPredicate.Compare("name", DocumentIndexComparison.Equal, "x"))], false, false, 1000);
        // Act / Assert.
        DocumentIndexKeys.Read(content, metadata, out _).ShouldHaveSingleItem();
    }
}
