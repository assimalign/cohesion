using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Repeated WHEN comparisons retain only their largest scratch (#1808)")]
    [InlineData(false)] [InlineData(true)]
    public async Task CreateIndexAsync_Fix5RepeatedWhen_ShouldFitGrant(bool escapedOrdering)
    {
        // Arrange: equality has 100,000 23-byte elements and a 544-byte constant
        // buffer; ordering has 1,000 1,822-byte elements and 5,680-byte scratch.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var fixture = await Fixture.Create();
        fixture.Catalog.IndexBuildMemoryBudget = escapedOrdering ? 4L * 1024 * 1024 : 64L * 1024 * 1024;
        string name = escapedOrdering ? string.Concat(Enumerable.Repeat("\\u0061", 300)) : "a";
        int count = escapedOrdering ? 1000 : 100000;
        string json = "{\"variants\":[" + string.Join(',', Enumerable.Repeat("{\"name\":\"" + name + "\",\"price\":1}", count)) + "]}";
        await fixture.Write("one", json);
        var keys = new[] { DocumentIndexKey.DistinctArray("variants", "price",
            DocumentIndexPredicate.Compare("name", escapedOrdering ? DocumentIndexComparison.LessThan : DocumentIndexComparison.Equal,
                escapedOrdering ? "z" : new string('x', 86))) };
        long extractionCharge = 0;
        fixture.Catalog.IndexBuildStateCaptured = (phase, _, bytes) => { if (phase == "keys") { extractionCharge = bytes; } };
        var context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        try
        {
            // Act.
            await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "ix", keys, false, false, context, guard.Token);
            var matches = await fixture.Catalog.SearchIndexAsync(fixture.Collection.Id, "ix", null, false, null, false, context.Snapshot, guard.Token);
            // Assert.
            matches.Count.ShouldBe(escapedOrdering ? 1 : 0);
            extractionCharge.ShouldBeLessThan(fixture.Catalog.IndexBuildMemoryBudget);
            _output.WriteLine($"REPEATED_WHEN|ordering={escapedOrdering}|L={Encoding.UTF8.GetByteCount(json)}|elements={count}|grant={fixture.Catalog.IndexBuildMemoryBudget}|extraction={extractionCharge}");
        }
        finally { await fixture.Coordinator.RollbackAsync(context, guard.Token); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Build shortcuts refuse invalid strings accepted by PUT (#1808)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task CreateIndexAsync_Fix5InvalidPutString_ShouldMatchStored(int shape)
    {
        // Arrange: these JSON strings contain literal six-character escapes.
        await using var engine = DocumentDatabaseEngine.Create("invalid-index-string", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        string json = shape switch
        {
            0 => "{\"variants\":[{\"name\":\"\\uD800\",\"price\":1}]}",
            1 => "{\"variants\":[{\"name\":\"a\\uD800\",\"price\":1}]}",
            2 => "{\"category\":\"" + new string('x', 3100) + "\\uD800\",\"variants\":[]}",
            _ => "{\"category\":\"" + new string('x', 3100) + "\",\"later\":\"\\uDC00\",\"variants\":[]}"
        };
        await collection.PutAsync(session, "one", Encoding.UTF8.GetBytes(json), cancellationToken: CancellationToken.None);
        string key = shape switch
        {
            0 => "DISTINCT ARRAY v.price FOR v IN variants WHEN v.name != '' END",
            1 => "DISTINCT ARRAY v.price FOR v IN variants WHEN v.name = 'b' END",
            2 => "category, DISTINCT ARRAY v.price FOR v IN variants END",
            _ => "category, later, DISTINCT ARRAY v.price FOR v IN variants END"
        };
        // Act / Assert: the unmapped GetString exception is the stored-write contract.
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.ExecuteAsync("CREATE INDEX ix ON items(" + key + ")", cancellationToken: CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Raw invalid UTF-8 refuses before a build comparison shortcut (#1808)")]
    public async Task CreateIndexAsync_Fix5InvalidUtf8_ShouldMatchStored()
    {
        // Arrange: bypass write-time JSON validation through the public raw stream.
        await using var fixture = await Fixture.Create();
        byte[] bytes = "{\"variants\":[{\"name\":\"?\",\"price\":1}]}"u8.ToArray();
        bytes[Array.IndexOf(bytes, (byte)'?')] = 0xFF;
        await WriteFix5RawAsync(fixture, bytes);
        var context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        try
        {
            // Act / Assert.
            await Should.ThrowAsync<InvalidOperationException>(async () => await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "ix",
                [DocumentIndexKey.DistinctArray("variants", "price", DocumentIndexPredicate.Compare("name", DocumentIndexComparison.Equal, "b"))],
                false, false, context, CancellationToken.None));
        }
        finally { await fixture.Coordinator.RollbackAsync(context); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Accessed imprecise numbers precede an earlier oversized key (#1808)")]
    public async Task CreateIndexAsync_Fix5ImpreciseLaterComponent_ShouldRefuseCorruptionFirst()
    {
        // Arrange: raw storage bypasses PUT's exact decimal validation.
        await using var fixture = await Fixture.Create();
        await WriteFix5RawAsync(fixture, Encoding.UTF8.GetBytes("{\"category\":\"" + new string('x', 341) +
            "\",\"later\":1.0000000000000000000000000000001}"));
        var context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        try
        {
            // Act / Assert.
            var error = await Should.ThrowAsync<StorageCorruptionException>(async () => await fixture.Catalog.CreateIndexAsync(
                fixture.Collection.Id, "ix", [DocumentIndexKey.Scalar("category"), DocumentIndexKey.Scalar("later")],
                false, false, context, CancellationToken.None));
            error.Code.ShouldBe("CK06003");
        }
        finally { await fixture.Coordinator.RollbackAsync(context); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - WHEN paths reserve their comparison-name workspace once per document (#1808)")]
    public void Read_Fix5PredicatePath_ShouldChargeCharacters()
    {
        // Arrange: numeric comparison adds no string comparison scratch.
        byte[] content = "{\"variants\":[{\"price\":1,\"flag\":1}]}"u8.ToArray();
        long Charge(DocumentIndexPredicate? predicate)
        {
            long charge = 0;
            DocumentIndexKeys.Read(content, new DocumentIndexMetadata(Guid.NewGuid(), "ix",
                [DocumentIndexKey.DistinctArray("variants", "price", predicate)], false, false, 1000),
                out _, reserve: bytes => charge += bytes).ShouldHaveSingleItem();
            return charge;
        }
        // Act / Assert.
        (Charge(DocumentIndexPredicate.Compare("flag", DocumentIndexComparison.Equal, 1m)) - Charge(null)).ShouldBe(4L * 80);
    }

    private static async Task WriteFix5RawAsync(Fixture fixture, byte[] bytes)
    {
        var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        DocumentContentReference content = default;
        await using (var stream = fixture.Storage.OpenWrite(fixture.Coordinator, writer, value =>
            { content = value; return ValueTask.CompletedTask; }, static () => ValueTask.CompletedTask, CancellationToken.None))
        { await stream.WriteAsync(bytes, CancellationToken.None); }
        await fixture.Catalog.SaveDocumentAsync(new(fixture.Collection.Id, "one", 1, content.Head, content.Length, content.Checksum), writer);
        await fixture.Coordinator.CommitAsync(writer);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Valid non-BMP strings preserve build and stored equality (#1808)")]
    [InlineData("\\uD83D\\uDE00")] [InlineData("😀")]
    [InlineData("\\\\uD800")] [InlineData("\\uD83d\\udE00")]
    public async Task CreateIndexAsync_Fix5ValidStrings_ShouldMatchStored(string raw)
    {
        // Arrange: cover WHEN, an oversized key and a key after an oversized key.
        await using var fixture = await Fixture.Create();
        string json = "{\"category\":\"" + new string('x', 3100) + raw + "\",\"later\":\"" + raw + "\",\"variants\":[{\"name\":\"" + raw + "\",\"price\":1}]}";
        await fixture.Write("one", json);
        byte[] content = Encoding.UTF8.GetBytes(json);
        var predicate = DocumentIndexPredicate.Compare("name", DocumentIndexComparison.Equal, raw == "\\\\uD800" ? "\\uD800" : "😀");
        var whenKeys = new[] { DocumentIndexKey.DistinctArray("variants", "price", predicate) };
        var metadata = new DocumentIndexMetadata(Guid.NewGuid(), "ix", whenKeys, false, false, 1000);
        // Act / Assert.
        var stored = DocumentIndexKeys.Read(content, metadata, out _);
        stored.ShouldHaveSingleItem();
        DocumentIndexKeys.Read(content, metadata, out _, reserve: _ => { }).ShouldBe(stored, ignoreOrder: true);
        var context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        try
        {
            await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "ix", whenKeys, false, false, context, CancellationToken.None);
            (await fixture.Catalog.SearchIndexAsync(fixture.Collection.Id, "ix", null, false, null, false, context.Snapshot)).ShouldHaveSingleItem();
            // Empty comprehension forms no key even though both scalar strings are accessed.
            byte[] empty = Encoding.UTF8.GetBytes(json.Replace("[{\"name\":\"" + raw + "\",\"price\":1}]", "[]", StringComparison.Ordinal));
            var oversized = new DocumentIndexMetadata(Guid.NewGuid(), "large", [DocumentIndexKey.Scalar("category"), DocumentIndexKey.Scalar("later"),
                DocumentIndexKey.DistinctArray("variants", "price")], false, false, 1000);
            DocumentIndexKeys.Read(empty, oversized, out _, reserve: _ => { }).ShouldBeEmpty();
        }
        finally { await fixture.Coordinator.RollbackAsync(context); }
    }
}
