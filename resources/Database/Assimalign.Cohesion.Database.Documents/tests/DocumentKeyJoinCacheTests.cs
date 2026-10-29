using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentKeyJoinCacheTests
{
    private readonly ITestOutputHelper _output;

    public DocumentKeyJoinCacheTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Documents] - Key joins: 100,000 distinct lookups fit the default statement grant")]
    public async Task ExecuteAsync_DistinctStreamingKeys_ShouldBoundCachePeakAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("join-stream-cache", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("rhs", CancellationToken.None);
        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None))
        {
            for (int index = 0; index < 100_000; index++)
            {
                await collection.PutAsync(session, "r" + index.ToString("D6", CultureInfo.InvariantCulture),
                    "{\"n\":1}"u8.ToArray(), cancellationToken: CancellationToken.None);
            }
            await transaction.CommitAsync(CancellationToken.None);
        }
        var request = DocumentQueryRequest.FromOql("SELECT RAW 1 FROM rhs AS a JOIN rhs AS b ON META(b).id = META(a).id WHERE b.n = 0");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            await using var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation,
                request.Statement, null, CancellationToken.None);
            int count = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }
            count.ShouldBe(0);
            session.LastQueryKeyLookups.ShouldBe(100_000);
            session.LastQueryDocumentsRead.ShouldBe(200_000);
            _output.WriteLine($"100,000-row streaming join peak: {memory.PeakBytes:N0} B; cache limit: {engine.EngineOptions.StatementMemoryBudget / 8:N0} B.");
            memory.PeakBytes.ShouldBeLessThan(40L * 1024 * 1024);
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Key joins: tiny-cache eviction matches the nested-loop oracle")]
    [InlineData("JOIN", false, false)]
    [InlineData("LEFT JOIN", true, false)]
    [InlineData("NEST", false, true)]
    [InlineData("LEFT NEST", true, true)]
    public async Task ExecuteAsync_EvictedKeys_ShouldMatchOracleAsync(string form, bool left, bool nest)
    {
        // Arrange: a 4 KiB cache cannot hold this cycle of hits, misses and NEST arrays.
        var builder = DocumentDatabaseEngine.CreateBuilder("join-eviction-oracle");
        builder.Options.StatementMemoryBudget = 32 * 1024;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        for (int index = 0; index < 16; index++)
        {
            await collection.PutAsync(session, "r" + index, Encoding.UTF8.GetBytes("{\"n\":" + index + "}"), cancellationToken: CancellationToken.None);
        }
        var expected = new List<string>();
        var keys = Enumerable.Range(0, 48).Select(index => index % 24 == 22 ? null : index % 24 == 23 ? "MISSING" : "r" + index % 24).ToArray();
        foreach (string? key in keys)
        {
            // Independent nested-loop reference, including the lookup-NEST unknown table.
            var matches = Enumerable.Range(0, 16).Where(index => key == "r" + index).ToArray();
            if (matches.Length == 0 && !left) { continue; }
            expected.Add(matches.Length > 0 ? (nest ? "[{\"n\":" + matches[0] + "}]" : "{\"n\":" + matches[0] + "}") :
                nest && key is not (null or "MISSING") ? "[]" : "MISSING");
        }
        string source = "[" + string.Join(",", keys.Select(key => key is null ? "NULL" : key == "MISSING" ? key : "'" + key + "'")) + "]";
        var request = DocumentQueryRequest.FromOql("SELECT RAW b FROM " + source + " AS k " + form + " items AS b ON META(b).id = k");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            await using var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation,
                request.Statement, null, CancellationToken.None);
            var actual = new List<string>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { actual.Add(row.IsMissing(0) || row.GetValue(0) is null ? "MISSING" : ((JsonElement)row.GetValue(0)!).GetRawText()); }
            actual.ShouldBe(expected);
            session.LastQueryKeyLookups.ShouldBe(44);
            await result.DisposeAsync();
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Key joins: eviction preserves blocked row charges")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_BlockedRowsAfterEviction_ShouldKeepLiveValuesChargedAsync(bool nest, bool group)
    {
        // Arrange: the cache evicts while ordering/grouping retains the joined values.
        var builder = DocumentDatabaseEngine.CreateBuilder("join-blocked-cache");
        builder.Options.StatementMemoryBudget = 256 * 1024;
        long observed = 0;
        DocumentAggregateMemory? statementMemory = null;
        builder.Functions.Add(DocumentScalarFunction.Create<string, string>("observe_key", key =>
        {
            if (key == "r099") { observed = statementMemory!.Bytes; }
            return key;
        }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        for (int index = 0; index < 100; index++)
        {
            await collection.PutAsync(session, "r" + index.ToString("D3", CultureInfo.InvariantCulture),
                Encoding.UTF8.GetBytes("{\"n\":" + index + ",\"pad\":\"" + new string('p', 180) + "\"}"), cancellationToken: CancellationToken.None);
        }
        string keys = "[" + string.Join(",", Enumerable.Range(0, 100).Select(index => "'r" + index.ToString("D3", CultureInfo.InvariantCulture) + "'")) + ", 'r000']";
        var request = DocumentQueryRequest.FromOql((group ? "SELECT RAW COUNT(*) FROM " : "SELECT RAW b FROM ") + keys + " AS k " + (nest ? "NEST" : "JOIN") +
            " items AS b ON META(b).id = observe_key(k)" + (group ? " GROUP BY k" : " ORDER BY k"));
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            statementMemory = operation.GetStatementMemory(CancellationToken.None);
            await using var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation,
                request.Statement, null, CancellationToken.None);
            int count = 0;
            long groupedCount = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                if (group) { groupedCount += row.GetInt64(0); }
                else
                {
                    var value = (JsonElement)row.GetValue(0)!;
                    var document = nest ? value[0] : value;
                    document.GetProperty("pad").GetString().ShouldBe(new string('p', 180));
                    document.GetProperty("n").GetInt32().ShouldBeInRange(0, 99);
                }
                count++;
            }
            count.ShouldBe(group ? 100 : 101);
            if (group) { groupedCount.ShouldBe(101); }
            session.LastQueryKeyLookups.ShouldBe(101);
            observed.ShouldBeGreaterThan(64 * 1024);
            await result.DisposeAsync();
            statementMemory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Key joins: oversized lookups bypass caching while active frames remain charged")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_OversizedActiveFrames_ShouldKeepChargesAcrossEvictionAsync(bool nest)
    {
        // Arrange: big cannot fit the 4 KiB cache, but fits in an active row.
        var builder = DocumentDatabaseEngine.CreateBuilder("join-active-cache");
        builder.Options.StatementMemoryBudget = 32 * 1024;
        DocumentAggregateMemory? memory = null;
        long observed = 0;
        int calls = 0;
        builder.Functions.Add(DocumentScalarFunction.Create<string, string, bool>("observe_live", (first, second) =>
        {
            first.ShouldBe(new string('p', 2000));
            second.ShouldBe(first);
            observed = memory!.Bytes;
            calls++;
            return false;
        }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "big", Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('p', 2000) + "\"}"), cancellationToken: CancellationToken.None);
        for (int index = 0; index < 32; index++)
        { await collection.PutAsync(session, "r" + index, "{\"n\":1}"u8.ToArray(), cancellationToken: CancellationToken.None); }
        string keys = "[" + string.Join(",", Enumerable.Range(0, 32).Select(index => "'r" + index + "'")) + "]";
        var request = DocumentQueryRequest.FromOql("SELECT RAW 1 FROM [1] AS a " + (nest ? "NEST" : "JOIN") +
            " items AS b ON META(b).id = 'big' UNNEST " + keys + " AS k JOIN items AS c ON META(c).id = k" +
            " JOIN items AS d ON META(d).id = 'big' WHERE observe_live(" + (nest ? "b[0].pad" : "b.pad") + ", d.pad)");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            memory = operation.GetStatementMemory(CancellationToken.None);
            await using var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation,
                request.Statement, null, CancellationToken.None);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { throw new InvalidOperationException("The predicate rejects every row."); }
            calls.ShouldBe(32);
            session.LastQueryKeyLookups.ShouldBe(65);
            observed.ShouldBeGreaterThan(nest ? 14_000 : 8_000);
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Key joins: local UNNEST masks captured metadata receivers")]
    [InlineData("SELECT RAW META(x).id", "UNNEST [2] AS x JOIN items AS b ON META(b).id = 'a'")]
    [InlineData("SELECT RAW b", "UNNEST [2] AS x JOIN items AS b ON META(b).id = META(x).id")]
    [InlineData("SELECT RAW META(x).id", "JOIN items AS b ON META(b).id = 'a' UNNEST [2] AS x")]
    [InlineData("SELECT RAW META(x).id", "NEST items AS b ON META(b).id = 'a' UNNEST [2] AS x")]
    public async Task ExecuteAsync_UnnestShadowingCapture_ShouldRefuseMetadataAsync(string projection, string bindings)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("join-shadow-meta", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        string query = "SELECT RAW (" + projection + " FROM [x] AS z " + bindings + ") FROM items AS x LIMIT 1";
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01035");
        error.Diagnostics.Single().Start.ShouldNotBeNull();
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Key joins: JOIN and NEST declarations shadow captured metadata with local values")]
    [InlineData("JOIN items AS x ON META(x).id = 'b'", "[\"b\"]")]
    [InlineData("NEST items AS x ON META(x).id = 'b'", "[null]")]
    [InlineData("UNNEST [META(x).id] AS k JOIN items AS x ON META(x).id = k", "[\"a\"]")]
    public async Task ExecuteAsync_LocalJoinShadowingCapture_ShouldUseLocalMetadataAsync(string bindings, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("join-shadow-local", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT RAW (SELECT RAW META(x).id FROM [x] AS z " + bindings + ") FROM items AS x LIMIT 1");
        // Assert
        ((JsonElement)rows.ShouldHaveSingleItem().GetValue(0)!).GetRawText().ShouldBe(expected);
    }
}
