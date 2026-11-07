using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphTextIndexReviewTests
{
    private readonly ITestOutputHelper _output;

    public GraphTextIndexReviewTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - Text anchor work and memory never exceed label scans (#1644)")]
    [InlineData("A", 60_000)] [InlineData("B", 73)] [InlineData("C", 300_000)] [InlineData("memory", 1)]
    public async Task Execute_CommonAnchor_ShouldFitLabelScanGrantAsync(string sequence, int count)
    {
        // Arrange: the review's exact cardinalities, with setup outside the measured grant.
        await using var engine = GraphDatabaseEngine.Create("text-review", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string value = sequence switch
        {
            "A" => "https://www.example.com/",
            "B" => GraphTextIndexTests.UniqueAscii(4096) + GraphTextIndexTests.UniqueAscii(2800).ToUpperInvariant(),
            "C" => "the cat",
            _ => GraphTextIndexTests.UniqueAscii(258),
        };
        string pattern = sequence switch { "A" => "https://www.", "C" => "the zebra", _ => value };
        string operation = sequence switch { "A" => "STARTS WITH", "C" => "CONTAINS", _ => "=" };
        string property = sequence == "A" ? "url" : "p";
        await PopulateAsync(session, count, value, sequence == "A", property);
        string query = $"MATCH (n:N) WHERE n.{property} {operation} $pattern RETURN n.k";
        var request = GraphQueryRequest.FromGql(query, GraphTextIndexTests.Bind(pattern),
            sequence == "memory" ? new GraphQueryLimits { MemoryBytes = 64 * 1024 } : new GraphQueryLimits());
        var scan = await ReadAsync(session, request);
        long scanWork = session.LastStatementMetrics!.Physical;
        long scanMemory = session.LastStatementMetrics.Memory.PeakRetainedBytes;
        engine.EngineOptions.QueryWorkBudget = 100_000_000;
        engine.EngineOptions.QueryMemoryBudget = 2L * 1024 * 1024 * 1024;
        await session.ExecuteAsync($"CREATE TEXT INDEX t FOR (n:N) ON (n.{property})", cancellationToken: CancellationToken.None);
        engine.EngineOptions.QueryWorkBudget = 1_000_000;
        engine.EngineOptions.QueryMemoryBudget = 256L * 1024 * 1024;

        // Act
        List<object?>? seek = null;
        DatabaseException? failure = null;
        try { seek = await ReadAsync(session, request); }
        catch (DatabaseException error) { failure = error; }

        // Assert: the only permitted extra retained memory is one fixed anchor/cursor allowance.
        _output.WriteLine($"{sequence}: scan rows={scan.Count}, work={scanWork}, memory={scanMemory}; index rows={seek?.Count}, work={session.LastStatementMetrics!.Physical}, memory={session.LastStatementMetrics.Memory.PeakRetainedBytes}, error={failure?.Diagnostics.FirstOrDefault()?.Code}");
        failure.ShouldBeNull();
        seek.ShouldBe(scan);
        session.LastStatementMetrics.Physical.ShouldBeLessThanOrEqualTo(scanWork + 1);
        session.LastStatementMetrics.Memory.PeakRetainedBytes.ShouldBeLessThanOrEqualTo(scanMemory + 128);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Unseekable text predicates execute the complete label-scan predicate (#1644)")]
    [InlineData(false)] [InlineData(true)]
    public async Task Execute_UnseekablePattern_ShouldPreserveScanErrorsAsync(bool surrogate)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-fallback", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1}),(:N {k:2,p:5,q:'x'})", cancellationToken: CancellationToken.None);
        string pattern = surrogate ? "\uD83Dxyz" : "ab";
        string query = $"MATCH (n:N) WHERE n.p CONTAINS '{pattern}' AND n.q + 1 > 0 RETURN n.k";
        var scanError = await Should.ThrowAsync<DatabaseException>(async () => await GraphTextIndexTests.RowsAsync(session, query));
        scanError.Diagnostics[0].Code.ShouldBe("CG02013");
        await session.ExecuteAsync("CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);

        // Act / Assert
        var plan = await GraphTextIndexTests.RowsAsync(session, "EXPLAIN " + query);
        plan.ShouldContain(row => Equals(row[2], "LabelScan"));
        if (surrogate) { plan.ShouldAllBe(row => !(row[5] == null ? "" : row[5]!.ToString()!).Contains("shorter_than_3_code_points", StringComparison.Ordinal)); }
        var indexError = await Should.ThrowAsync<DatabaseException>(async () => await GraphTextIndexTests.RowsAsync(session, query));
        indexError.Diagnostics[0].Code.ShouldBe("CG02013");
        string overlayQuery = $"INSERT (:Overlay {{k:2,p:5,q:'x'}}) MATCH (n:Overlay) WHERE n.p CONTAINS '{pattern}' AND n.q + 1 > 0 RETURN n.k";
        var overlayScan = await Should.ThrowAsync<DatabaseException>(async () => await GraphTextIndexTests.RowsAsync(session, overlayQuery));
        overlayScan.Diagnostics[0].Code.ShouldBe("CG02013");
        await session.ExecuteAsync("CREATE TEXT INDEX overlay_p FOR (n:Overlay) ON (n.p)", cancellationToken: CancellationToken.None);
        var overlayIndex = await Should.ThrowAsync<DatabaseException>(async () => await GraphTextIndexTests.RowsAsync(session, overlayQuery));
        overlayIndex.Diagnostics[0].Code.ShouldBe("CG02013");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Every maintained text write form equals an independent scan database (#1644)")]
    public async Task Execute_PostBuildDifferential_ShouldPreserveEveryWriteFormAsync()
    {
        // Arrange: both databases receive identical statements; only one builds an index.
        await using var engine = GraphDatabaseEngine.Create("text-maintained", GraphTextIndexTests.Quiet());
        var scanDatabase = await engine.CreateDatabaseAsync("scan", CancellationToken.None);
        var indexedDatabase = await engine.CreateDatabaseAsync("indexed", CancellationToken.None);
        await using var scan = await scanDatabase.CreateSessionAsync(CancellationToken.None);
        await using var indexed = await indexedDatabase.CreateSessionAsync(CancellationToken.None);
        await indexed.ExecuteAsync("CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        await ReplayWritesAndCompareAsync(scan, indexed, indexedDatabase);
    }

    internal static async Task ReplayWritesAndCompareAsync(GraphDatabaseSession scan, GraphDatabaseSession indexed, GraphDatabase indexedDatabase)
    {
        string[] writes =
        [
            "INSERT (:N {k:1,p:'startabcdefend'}),(:Other {k:2,p:'suffixabcdef'}),(:N:Other {k:3,p:'midabcdefmiddle'})",
            "CREATE (:N {k:4,p:'createdabc'})",
            "INSERT (:N {k:12,p:'😀éabc𐐀end'})",
            "MERGE (n:N {p:'mergedabc'}) ON CREATE SET n.k=5 ON MATCH SET n.p='matchedabcdef'",
            "MERGE (n:N {p:'mergedabc'}) ON CREATE SET n.k=5 ON MATCH SET n.p='matchedabcdef'",
            "MATCH (n:N {k:1}) SET n.p='changedabcdef'",
            "MATCH (n:N {k:1}) SET n = {k:1,p:'replacedabcdef'}",
            "MATCH (n:N {k:1}) SET n += {p:'patchedabcdef'}",
            "MATCH (n:N {k:4}) REMOVE n.p",
            "MATCH (n:Other {k:2}) SET n:N",
            "MATCH (n:N {k:3}) REMOVE n:N",
            "MATCH (n:N {k:1}) SET n.p=42",
            "MATCH (n:N {k:1}) SET n.p='restoredabcdef'",
            "INSERT (:N {k:6,p:'detachabc'})-[:R]->(:Target {k:7})",
            "MATCH (n:N {k:6}) DETACH DELETE n",
            "INSERT (:N {k:8,p:''}),(:N {k:9,p:'a'}),(:N {k:10,p:'😀😀'})",
            "MATCH (n:N {k:4}) SET n += {p:'clearabc'}",
            "MATCH (n:N {k:4}) SET n = {}",
        ];

        // Act / Assert: each phase exercises suffixes, interiors, Boolean forms, paging and null extension.
        foreach (string write in writes)
        {
            await scan.ExecuteAsync(write, cancellationToken: CancellationToken.None);
            await indexed.ExecuteAsync(write, cancellationToken: CancellationToken.None);
            await CompareAsync();
        }
        await using var scanTransaction = await scan.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await using var indexTransaction = await indexed.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        foreach (string write in new[]
        {
            "MATCH (n:N {k:1}) SET n.p='firstabc'", "SAVEPOINT original",
            "MATCH (n:N {k:1}) SET n.p='secondxyz'", "MATCH (n:N {k:1}) SET n.p='firstabc'",
            "ROLLBACK TO original", "MERGE (n:N {p:'snapshotabc'}) ON CREATE SET n.k=11",
        })
        {
            await scan.ExecuteAsync(write, cancellationToken: CancellationToken.None);
            await indexed.ExecuteAsync(write, cancellationToken: CancellationToken.None);
            await CompareAsync();
        }
        await scanTransaction.RollbackAsync(CancellationToken.None);
        await indexTransaction.RollbackAsync(CancellationToken.None);
        await CompareAsync();

        // Count postings as well as rows: recheck can hide a missed maintenance deletion.
        for (int pass = 0; pass < 16; pass++) { indexedDatabase.Coordinator.RunVersionPurgePass(CancellationToken.None); }
        var expectedTokens = (await GraphTextIndexTests.RowsAsync(scan, "MATCH (n:N) RETURN n.p"))
            .Select(row => row[0]).OfType<string>().SelectMany(value => Internal.GraphTextTrigrams.Keys(value, CancellationToken.None))
            .GroupBy(key => Convert.ToHexString(key.Encoded.Span)).ToDictionary(group => group.Key, group => group.Count());
        var context = await indexedDatabase.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        ulong id;
        try { id = indexedDatabase.Store.GetIndexIdentities(context.Snapshot).Single(index => index.Label == "N" && index.Property == "p" && index.Text).Id; }
        finally { await indexedDatabase.Coordinator.RollbackAsync(context, CancellationToken.None); }
        var actualTokens = await GraphTextIndexTests.CountEntriesByTokenAsync(indexedDatabase, id);
        actualTokens.OrderBy(pair => pair.Key).ShouldBe(expectedTokens.OrderBy(pair => pair.Key));

        async Task CompareAsync()
        {
            foreach (string query in new[]
            {
                "MATCH (n:N) WHERE n.p CONTAINS 'abcdef' RETURN n.k",
                "MATCH (n:N) WHERE n.p ENDS WITH 'abc' RETURN n.k",
                "MATCH (n:N) WHERE n.p CONTAINS '😀é' RETURN n.k",
                "MATCH (n:N) WHERE n.p ENDS WITH '𐐀end' RETURN n.k",
                "MATCH (n:N) WHERE n.p STARTS WITH 'first' RETURN n.k SKIP 0 LIMIT 2",
                "MATCH (n:N) WHERE NOT n.p CONTAINS 'abc' RETURN n.k",
                "MATCH (n:N) OPTIONAL MATCH (m:N) WHERE m.p CONTAINS 'abcdef' RETURN n.k,m.k ORDER BY n.k,m.k SKIP 1 LIMIT 10",
                "MATCH (n:N) WHERE n.p='' OR n.p='a' OR n.p='😀😀' RETURN n.k",
            })
            {
                var expected = await GraphTextIndexTests.RowsAsync(scan, query);
                var actual = await GraphTextIndexTests.RowsAsync(indexed, query);
                actual.Select(row => string.Join("|", row)).ShouldBe(expected.Select(row => string.Join("|", row)), query);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - A pre-build Snapshot keeps its scan and a later-position seek preserves rows (#1644)")]
    public async Task Execute_PreBuildSnapshotAndLaterAnchor_ShouldKeepScanParityAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-later", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await using var old = await database.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("INSERT (:A {k:1})-[:R]->(:N {k:2,p:'abcdef'}),(:A {k:3}),(:A {k:4})", cancellationToken: CancellationToken.None);
        await using var snapshot = await old.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        const string query = "MATCH (a:A)-[:R]->(b:N) WHERE b.p CONTAINS 'abc' RETURN a.k,b.k";
        var expected = await GraphTextIndexTests.RowsAsync(old, query);

        // Act / Assert
        await writer.ExecuteAsync("CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        (await GraphTextIndexTests.RowsAsync(old, "EXPLAIN " + query)).ShouldNotContain(row => Equals(row[2], "TextIndexSeek"));
        (await GraphTextIndexTests.RowsAsync(writer, "EXPLAIN " + query)).ShouldContain(row => Equals(row[2], "TextIndexSeek") && row[5]!.ToString()!.Contains("node=1", StringComparison.Ordinal));
        (await GraphTextIndexTests.RowsAsync(writer, query)).Select(row => string.Join("|", row)).ShouldBe(expected.Select(row => string.Join("|", row)));
        (await GraphTextIndexTests.RowsAsync(old, query)).ShouldHaveSingleItem();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - A seekable conjunct wins over a short text candidate (#1644)")]
    [InlineData(false)] [InlineData(true)]
    public async Task Execute_SeekableConjunct_ShouldChooseTextSeekAsync(bool secondProperty)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-conjunct", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1,p:'abcdef',q:'abcdef'})", cancellationToken: CancellationToken.None);
        string query = $"MATCH (n:N) WHERE n.p CONTAINS 'ab' AND n.{(secondProperty ? "q" : "p")} CONTAINS 'abcdef' RETURN n.k";
        var expected = await GraphTextIndexTests.RowsAsync(session, query);
        await session.ExecuteAsync("CREATE TEXT INDEX p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TEXT INDEX q FOR (n:N) ON (n.q)", cancellationToken: CancellationToken.None);

        // Act / Assert
        (await GraphTextIndexTests.RowsAsync(session, "EXPLAIN " + query)).ShouldContain(row => Equals(row[2], "TextIndexSeek"));
        (await GraphTextIndexTests.RowsAsync(session, query)).ShouldHaveSingleItem()[0].ShouldBe(expected.Single()[0]);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - An unseekable later text candidate cannot displace a tied label scan (#1644)")]
    public async Task Execute_UnseekableLaterCandidate_ShouldKeepSourceOrderScanAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-tie", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:A {k:1})-[:R]->(:N {p:'abc'})", cancellationToken: CancellationToken.None);
        const string query = "MATCH (a:A)-[:R]->(b:N) WHERE b.p CONTAINS 'ab' RETURN a.k";
        var before = await GraphTextIndexTests.RowsAsync(session, query);
        await session.ExecuteAsync("CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);

        // Act / Assert
        var plan = await GraphTextIndexTests.RowsAsync(session, "EXPLAIN " + query);
        plan.Single(row => Equals(row[2], "LabelScan"))[5]!.ToString()!.ShouldContain("node=0", Case.Sensitive);
        (await GraphTextIndexTests.RowsAsync(session, query)).ShouldHaveSingleItem()[0].ShouldBe(before.Single()[0]);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - TEXT is valid only for a programmatic CREATE INDEX (#1644)")]
    [InlineData(true)] [InlineData(false)]
    public async Task Execute_InvalidTextSchemaAst_ShouldRefuseAsync(bool drop)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-ast", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var command = new GqlSchemaCommand
        {
            Kind = drop ? GqlSchemaObjectKind.Index : GqlSchemaObjectKind.Constraint, Drop = drop, IsText = true, Name = "t",
            Definition = drop ? null : "N", Variable = drop ? null : "n", Property = drop ? null : "p",
        };
        var expression = new GqlQueryExpression(Array.Empty<GqlStage>()) { SchemaCommand = command };

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(expression)), CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Snapshot MERGE uses the same bounded anchor and latest-view probe (#1644)")]
    [InlineData(false)] [InlineData(true)]
    public async Task Execute_SnapshotMerge_ShouldFitScanGrantAsync(bool drop)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-merge-cost", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var ddl = await database.CreateSessionAsync(CancellationToken.None);
        string value = GraphTextIndexTests.UniqueAscii(4096) + GraphTextIndexTests.UniqueAscii(2800).ToUpperInvariant();
        await session.ExecuteAsync("INSERT (:N {k:1,p:$pattern})", GraphTextIndexTests.Bind(value), CancellationToken.None);
        var request = GraphQueryRequest.FromGql("MERGE (n:N {p:$pattern}) RETURN n.k", GraphTextIndexTests.Bind(value), new GraphQueryLimits { WorkUnits = 100 });
        await using (var scanTransaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None))
        {
            (await ReadAsync(session, request)).ShouldBe([1L]);
            await scanTransaction.RollbackAsync(CancellationToken.None);
        }
        long scanWork = session.LastStatementMetrics!.Physical;
        long scanMemory = session.LastStatementMetrics.Memory.PeakRetainedBytes;
        await ddl.ExecuteAsync("CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        if (drop) { await ddl.ExecuteAsync("DROP INDEX t", cancellationToken: CancellationToken.None); }

        // Act / Assert: the Snapshot's second probe uses latest visibility under the writer lock.
        (await ReadAsync(session, request)).ShouldBe([1L]);
        session.LastStatementMetrics!.Physical.ShouldBeLessThanOrEqualTo(scanWork + 1);
        session.LastStatementMetrics.Memory.PeakRetainedBytes.ShouldBeLessThanOrEqualTo(scanMemory + 128);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Short values create no unreadable text postings (#1644)")]
    public async Task Execute_ShortValues_ShouldWriteNoTokensAndKeepRowsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-short", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1,p:''}),(:N {k:2,p:'a'}),(:N {k:3,p:'ab'}),(:N {k:4,p:'😀😀'})", cancellationToken: CancellationToken.None);
        await using var snapshot = await session.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        ulong id = database.Store.GetIndexIdentities(snapshot.Context.Snapshot).Single().Id;

        // Act / Assert
        (await GraphTextIndexTests.CountEntriesAsync(database, id)).ShouldBe(0);
        foreach (string value in new[] { "", "a", "ab", "😀😀" })
        { (await GraphTextIndexTests.RowsAsync(session, GraphTextIndexTests.Query("="), value)).ShouldHaveSingleItem(); }
    }

    internal static async Task PopulateAsync(GraphDatabaseSession session, int count, string value, bool appendKey = false, string property = "p")
    {
        for (int start = 0; start < count; start += 100)
        {
            var parameters = new Dictionary<string, object?>
            { ["keys"] = Enumerable.Range(start, Math.Min(100, count - start)).Select(k => (object?)(long)k).ToArray(), ["value"] = value };
            await session.ExecuteAsync(appendKey ? $"FOR k IN $keys INSERT (:N {{k:k,{property}:$value || CAST(k AS STRING)}})"
                : $"FOR k IN $keys INSERT (:N {{k:k,{property}:$value}})", parameters, CancellationToken.None);
        }
    }

    private static async Task<List<object?>> ReadAsync(GraphDatabaseSession session, GraphQueryRequest request)
    {
        await using var result = (await session.ExecuteAsync(request, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(row.GetValue(0)); }
        return rows;
    }
}
