using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphTextIndexTests
{
    private readonly ITestOutputHelper _output;

    public GraphTextIndexTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Graph] - Text indexes equal seeded ordinal label scans across Unicode and scalar kinds (#1644)")]
    public async Task Execute_SeededDifferential_ShouldEqualEveryLabelScanAsync()
    {
        // Arrange: deterministic strings include every short length, supplementary scalars and combining sequences.
        await using var engine = GraphDatabaseEngine.Create("text-differential", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var referenceDatabase = await engine.CreateDatabaseAsync("reference", CancellationToken.None);
        await using var reference = await referenceDatabase.CreateSessionAsync(CancellationToken.None);
        var random = new Random(1644);
        string[] alphabet = ["a", "b", "c", "A", "é", "e", "\u0301", "😀", "𐐀", "\0"];
        var values = new List<object?> { null, false, 42L, 1.25m, "", "a", "ab", "abc", "ABC", "😀😀", "😀😀😀", "e\u0301", "é", "abcXbcd", "abcd", "aaaa", "aaa", "x😀ab" };
        for (int i = 0; i < 64; i++)
        { values.Add(string.Concat(Enumerable.Range(0, random.Next(16)).Select(_ => alphabet[random.Next(alphabet.Length)]))); }
        for (int i = 0; i < values.Count; i++)
        {
            var parameters = new Dictionary<string, object?> { ["k"] = (long)i, ["p"] = values[i] };
            await session.ExecuteAsync("INSERT (:N {k:$k,p:$p})", parameters, CancellationToken.None);
            await reference.ExecuteAsync("INSERT (:N {k:$k,p:$p})", parameters, CancellationToken.None);
        }
        await session.ExecuteAsync("INSERT (:N {k:999})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Noise {k:1000,p:'abcd'})", cancellationToken: CancellationToken.None);
        await reference.ExecuteAsync("INSERT (:N {k:999}),(:Noise {k:1000,p:'abcd'})", cancellationToken: CancellationToken.None);
        var patterns = new List<object?> { null, false, 42L, "", "a", "ab", "abc", "abcd", "aaa", "aaaa", "ABC", "😀", "😀😀", "😀😀😀", "e\u0301", "é", "\0", "😀ab", "missing" };
        foreach (string value in values.OfType<string>())
        {
            string[] scalars = value.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
            patterns.Add(string.Concat(scalars.Take(random.Next(0, scalars.Length + 1))));
            int start = random.Next(scalars.Length + 1);
            patterns.Add(string.Concat(scalars.Skip(start)));
            patterns.Add(string.Concat(scalars.Skip(start).Take(random.Next(scalars.Length - start + 1))));
        }
        string[] operators = ["=", "CONTAINS", "ENDS WITH", "STARTS WITH"];
        var expected = new Dictionary<(int Pattern, string Operator), object?[]>();
        var costs = new Dictionary<(int Pattern, string Operator), (long Work, long Memory)>();
        string Statement(string operation, int pattern) => pattern % 2 == 0 ? Query(operation) : Query(operation).Replace(" ORDER BY n.k", "", StringComparison.Ordinal);
        foreach (string operation in operators)
        {
            for (int i = 0; i < patterns.Count; i++)
            {
                expected[(i, operation)] = (await RowsAsync(session, Statement(operation, i), patterns[i])).Select(row => row[0]).ToArray();
                costs[(i, operation)] = (session.LastStatementMetrics!.Physical, session.LastStatementMetrics.Memory.PeakRetainedBytes);
            }
        }

        // Act: every statement and parameter executes unchanged against the populated text index.
        await session.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        foreach (string operation in operators)
        {
            for (int i = 0; i < patterns.Count; i++)
            {
                var actual = (await RowsAsync(session, Statement(operation, i), patterns[i])).Select(row => row[0]);
                actual.ShouldBe(expected[(i, operation)], $"operator={operation}; pattern={patterns[i]}");
                // One fixed anchor may add at most one work unit and 128 retained bytes.
                session.LastStatementMetrics!.Physical.ShouldBeLessThanOrEqualTo(costs[(i, operation)].Work + 1);
                session.LastStatementMetrics.Memory.PeakRetainedBytes.ShouldBeLessThanOrEqualTo(costs[(i, operation)].Memory + 128);
            }
        }

        // Assert: two supplementary characters are two code points, while three permit a seek.
        foreach (var (pattern, access) in new[] { ("", "LabelScan"), ("ab", "LabelScan"), ("😀😀", "LabelScan"), ("😀😀😀", "TextIndexSeek"), ("abc", "TextIndexSeek") })
        {
            foreach (string operation in operators)
            {
                var plan = await RowsAsync(session, "EXPLAIN " + Query(operation), pattern);
                var anchor = plan.Single(row => Equals(row[2], access));
                if (access == "TextIndexSeek")
                {
                    anchor[4].ShouldBe("text_p");
                    anchor[5]!.ToString()!.ShouldContain("decision=execution");
                    anchor[5]!.ToString()!.ShouldContain("parameter=True");
                    anchor[5]!.ToString()!.ShouldContain("recheck=stored_value");
                }
                else { anchor[4].ShouldBeNull(); }
            }
        }
        // Both abc and bcd occur in abcXbcd; the stored recheck must reject it for abcd.
        (await RowsAsync(session, Query("CONTAINS"), "abcd")).Select(row => row[0]).ShouldBe([14L]);
        (await RowsAsync(session, "MATCH (n:N {p:'abcd'}) RETURN n.k ORDER BY n.k")).Select(row => row[0]).ShouldBe([14L]);
        (await RowsAsync(session, "MATCH (n:N) WHERE n.p CONTAINS 'abcd' OR n.k=0 RETURN n.k ORDER BY n.k")).Select(row => row[0]).ShouldBe([0L, 14L]);
        await GraphTextIndexReviewTests.ReplayWritesAndCompareAsync(reference, session, database);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Conjunctive text predicates stay distinct by variable and property (#1644)")]
    public async Task Execute_MultipleTextPredicates_ShouldKeepIndependentScanParityAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-conjunction", Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1,p:'abcdef',q:'uvwxyz'}),(:N {k:2,p:'abcd',q:'uvwx'}),(:N {k:3,p:'abcXbcd',q:'uvwx'})", cancellationToken: CancellationToken.None);
        string[] queries =
        [
            "MATCH (n:N) WHERE n.p CONTAINS 'abcd' AND n.q ENDS WITH 'wxyz' RETURN n.k AS first_key,n.k AS second_key ORDER BY first_key",
            "MATCH (n:N) WHERE n.q CONTAINS 'uvw' AND n.p ENDS WITH 'bcd' RETURN n.k AS first_key,n.k AS second_key ORDER BY first_key",
            "MATCH (n:N) WHERE n.q='uvwx' AND 'abcd'=n.p RETURN n.k AS first_key,n.k AS second_key ORDER BY first_key",
            "MATCH (a:N),(b:N) WHERE a.p CONTAINS 'abcd' AND b.p ENDS WITH 'bcd' RETURN a.k,b.k ORDER BY a.k,b.k",
        ];
        (long, long)[][] expected = [[(1, 1)], [(2, 2), (3, 3)], [(2, 2)], [(1, 2), (1, 3), (2, 2), (2, 3)]];

        // Act / Assert: run each unchanged statement before and after both populated indexes exist.
        for (int indexed = 0; indexed < 2; indexed++)
        {
            if (indexed != 0)
            {
                await session.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
                await session.ExecuteAsync("CREATE TEXT INDEX text_q FOR (n:N) ON (n.q)", cancellationToken: CancellationToken.None);
            }
            for (int i = 0; i < queries.Length; i++)
            { (await RowsAsync(session, queries[i])).Select(row => ((long)row[0]!, (long)row[1]!)).ShouldBe(expected[i]); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Text index updates preserve old snapshots, own writes, rollback and staged matches (#1644)")]
    public async Task Execute_MvccAndOverlay_ShouldPreserveEveryReadViewAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-mvcc", Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await using var old = await database.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("INSERT (:N {k:1,p:'oldabc'})", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        await using var snapshot = await old.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);

        // Act / Assert: removal of old trigrams remains invisible to an old snapshot.
        await writer.ExecuteAsync("MATCH (n:N) SET n.p='newxyz'", cancellationToken: CancellationToken.None);
        (await RowsAsync(old, Query("CONTAINS"), "abc")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await RowsAsync(writer, Query("CONTAINS"), "abc")).ShouldBeEmpty();
        (await RowsAsync(writer, Query("CONTAINS"), "xyz")).ShouldHaveSingleItem();
        await using (var transaction = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None))
        {
            await writer.ExecuteAsync("MATCH (n:N) SET n.p='ownabc'", cancellationToken: CancellationToken.None);
            (await RowsAsync(writer, Query("CONTAINS"), "abc")).ShouldHaveSingleItem();
            (await RowsAsync(writer, Query("CONTAINS"), "xyz")).ShouldBeEmpty();
            await transaction.RollbackAsync(CancellationToken.None);
        }
        (await RowsAsync(writer, Query("ENDS WITH"), "xyz")).ShouldHaveSingleItem();
        (await RowsAsync(writer, "MATCH (n:N) SET n.p='stageabc' MATCH (m:N) WHERE m.p CONTAINS 'abc' RETURN m.k")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await RowsAsync(writer, "INSERT (:N {k:2,p:'stageabc'}) MATCH (m:N) WHERE m.p ENDS WITH 'abc' RETURN m.k ORDER BY m.k")).Select(row => row[0]).ShouldBe([1L, 2L]);
        await writer.ExecuteAsync("MATCH (n:N {k:1}) REMOVE n:N", cancellationToken: CancellationToken.None);
        (await RowsAsync(writer, Query("CONTAINS"), "abc")).ShouldHaveSingleItem()[0].ShouldBe(2L);
        await writer.ExecuteAsync("MATCH (n:N) DELETE n", cancellationToken: CancellationToken.None);
        (await RowsAsync(writer, Query("CONTAINS"), "abc")).ShouldBeEmpty();
        (await RowsAsync(old, Query("CONTAINS"), "abc")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        await snapshot.RollbackAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Snapshot writers maintain both pinned retired text trees and replacements (#1644)")]
    [InlineData(false)] [InlineData(true)]
    public async Task Execute_SnapshotWriterAfterDrop_ShouldSeeOwnWritesAndMaintainReplacementAsync(bool commit)
    {
        // Arrange: the writer's snapshot precedes DROP and the replacement build.
        await using var engine = GraphDatabaseEngine.Create("text-retired-writer", Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var current = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await current.ExecuteAsync("INSERT (:N {k:1,p:'oldabc'})", cancellationToken: CancellationToken.None);
        await current.ExecuteAsync("CREATE TEXT INDEX old_text FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        await using var transaction = await writer.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await current.ExecuteAsync("DROP INDEX old_text", cancellationToken: CancellationToken.None);
        await current.ExecuteAsync("CREATE TEXT INDEX new_text FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);

        // Act / Assert: both trees need the writer's new postings, with its actual owner stamp.
        await writer.ExecuteAsync("MATCH (n:N {k:1}) SET n.p='newxyz'", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("INSERT (:N {k:2,p:'ownabc'})", cancellationToken: CancellationToken.None);
        (await RowsAsync(writer, Query("CONTAINS"), "abc")).ShouldHaveSingleItem()[0].ShouldBe(2L);
        (await RowsAsync(writer, Query("CONTAINS"), "xyz")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await RowsAsync(current, Query("CONTAINS"), "abc")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        if (commit) { await transaction.CommitAsync(CancellationToken.None); }
        else { await transaction.RollbackAsync(CancellationToken.None); }
        (await RowsAsync(current, Query("CONTAINS"), "abc")).ShouldHaveSingleItem()[0].ShouldBe(commit ? 2L : 1L);
        (await RowsAsync(current, Query("CONTAINS"), "xyz")).Count.ShouldBe(commit ? 1 : 0);
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Text and exact indexes coexist, conditional DDL stays idempotent and drop reclaims its tree (#1644)")]
    public async Task Execute_CoexistingIndexesAndDrop_ShouldReclaimOnlyRetiredPagesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-drop", Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("INSERT (:N {k:1,p:'abcdef'})", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("CREATE INDEX exact_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        (await writer.ExecuteAsync("CREATE TEXT INDEX other IF NOT EXISTS FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(0);
        var schema = GraphSchema.Open(database, writer);
        (await schema.GetIndexesAsync(CancellationToken.None)).Select(index => index.IsText).ShouldBe([false, true]);
        await using var old = await database.CreateSessionAsync(CancellationToken.None);
        await using var snapshot = await old.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        ulong textId = database.Store.GetIndexIdentities(snapshot.Context.Snapshot).Single(index => index.Text).Id;
        int pages = database.DataStorage.GetOwnerIndexPages(textId).Count;
        pages.ShouldBeGreaterThan(0);
        long reclaimed = database.Store.IndexOwnerPagesReclaimed;

        // Act / Assert: an exact equality seek wins a tied text estimate; old snapshots pin DROP.
        var equalityPlan = await RowsAsync(writer, "EXPLAIN MATCH (n:N {p:'abcdef'}) RETURN n.k");
        equalityPlan.Single(row => Equals(row[2], "IndexSeek"))[4].ShouldBe("exact_p");
        await writer.ExecuteAsync("DROP INDEX text_p", cancellationToken: CancellationToken.None);
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        database.DataStorage.GetOwnerIndexPages(textId).Count.ShouldBe(pages);
        (await RowsAsync(old, Query("CONTAINS"), "abc")).ShouldHaveSingleItem();
        (await RowsAsync(writer, Query("CONTAINS"), "abc")).ShouldHaveSingleItem();
        await snapshot.RollbackAsync(CancellationToken.None);
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        database.DataStorage.GetOwnerIndexPages(textId).ShouldBeEmpty();
        (database.Store.IndexOwnerPagesReclaimed - reclaimed).ShouldBe(pages);
        (await RowsAsync(writer, "MATCH (n:N {p:'abcdef'}) RETURN n.k")).ShouldHaveSingleItem();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Text index statement work and memory refusals roll back builds and writes (#1644)")]
    [InlineData(true, false)] [InlineData(false, false)]
    [InlineData(true, true)] [InlineData(false, true)]
    public async Task Execute_ExhaustedBudget_ShouldPublishNoPartialIndexAsync(bool work, bool build)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-budget", Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string value = UniqueAscii(1024);
        if (build) { await session.ExecuteAsync("INSERT (:N {k:1,p:$pattern})", Bind(value), CancellationToken.None); }
        else { await session.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None); }
        var request = GraphQueryRequest.FromGql(build ? "CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)" : "INSERT (:N {k:1,p:$pattern})", Bind(value),
            work ? new GraphQueryLimits { WorkUnits = 100 } : new GraphQueryLimits { MemoryBytes = 64 * 1024 });

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(work ? "CG05004" : "CG05020");
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        if (build)
        {
            (await GraphSchema.Open(database, session).GetIndexesAsync(CancellationToken.None)).ShouldBeEmpty();
            await session.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
            (await RowsAsync(session, Query("CONTAINS"), value)).ShouldHaveSingleItem();
        }
        else { (await RowsAsync(session, "MATCH (n:N) RETURN n.k")).ShouldBeEmpty(); }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Build preflight and partial text updates roll back on work exhaustion (#1644)")]
    [InlineData(false)] [InlineData(true)]
    public async Task Execute_WorkExhaustion_ShouldRestoreTreeAndRowsAsync(bool update)
    {
        // Arrange: 1500 units refuse backfill before allocation, or an update during old-token removal.
        await using var engine = GraphDatabaseEngine.Create("text-partial", Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string value = UniqueAscii(1024);
        await session.ExecuteAsync("INSERT (:N {k:1,p:$pattern})", Bind(value), CancellationToken.None);
        if (update) { await session.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None); }
        var request = GraphQueryRequest.FromGql(update ? "MATCH (n:N) SET n.p=$pattern" : "CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)",
            Bind(new string('z', 1024)), new GraphQueryLimits { WorkUnits = 1500 });

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        if (!update)
        {
            (await GraphSchema.Open(database, session).GetIndexesAsync(CancellationToken.None)).ShouldBeEmpty();
            await session.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        }
        (await RowsAsync(session, Query("="), value)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await RowsAsync(session, Query("CONTAINS"), "zzz")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Multi-node text deletes retain all posting undo charges until statement completion (#1644)")]
    public async Task Execute_DeleteMemoryBudget_ShouldRetainChargesAndRestorePriorBracketsAsync()
    {
        // Arrange: each node fits the grant; their combined undo postings do not.
        await using var engine = GraphDatabaseEngine.Create("text-delete-budget", Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("FOR k IN [1,2,3] INSERT (:N {k:k,p:$pattern})", Bind(UniqueAscii(128)), CancellationToken.None);
        var request = GraphQueryRequest.FromGql("MATCH (n:N) DELETE n", null, new GraphQueryLimits { MemoryBytes = 64 * 1024 });

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));

        // Assert: extraction has passed the first node, and rollback restores all earlier physical brackets.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05020");
        session.LastStatementMetrics!.Physical.ShouldBeGreaterThan(128);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        (await RowsAsync(session, Query("STARTS WITH"), "aaa")).Select(row => row[0]).ShouldBe([1L, 2L, 3L]);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Typed text-index writes use engine statement budgets (#1644)")]
    [InlineData(false)] [InlineData(true)]
    public async Task CreateNodeAsync_TypedTextMaintenance_ShouldRespectDefaultWorkGrantAsync(bool delete)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-typed-budget", Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        var properties = new Dictionary<string, object?> { ["k"] = 1L, ["p"] = UniqueAscii(1024) };
        GraphNode? node = delete ? await database.CreateNodeAsync(session, ["N"], properties, CancellationToken.None) : null;
        engine.EngineOptions.QueryWorkBudget = 100;

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (delete) { await database.DeleteNodeAsync(session, node!.Value.Id, CancellationToken.None); }
            else { await database.CreateNodeAsync(session, ["N"], properties, CancellationToken.None); }
        });

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        engine.EngineOptions.QueryWorkBudget = 1_000_000;
        (await RowsAsync(session, "MATCH (n:N) RETURN n.k")).Count.ShouldBe(delete ? 1 : 0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Dropped text trees reclaim bounded owner batches until every page is freed (#1644)")]
    public async Task DropIndexAsync_LargeTree_ShouldBoundEachMaintenancePassAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-bounded-drop", Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string value = UniqueAscii(4096) + UniqueAscii(2800).ToUpperInvariant();
        await session.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("FOR k IN [1,2] INSERT (:N {k:k,p:$pattern})", Bind(value), CancellationToken.None);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        ulong id;
        try { id = database.Store.GetIndexIdentities(context.Snapshot).Single().Id; }
        finally { await database.Coordinator.RollbackAsync(context, CancellationToken.None); }
        int pages = database.DataStorage.GetOwnerIndexPages(id).Count;
        pages.ShouldBeGreaterThan(GraphStore.IndexReclaimPageLimit);
        await session.ExecuteAsync("DROP INDEX text_p", cancellationToken: CancellationToken.None);
        long reclaimed = database.Store.IndexOwnerPagesReclaimed;

        // Act / Assert: use owner counts and exact progress, never elapsed time.
        int remaining = pages;
        int passes = 0;
        while (remaining > 0)
        {
            database.Coordinator.RunVersionPurgePass(CancellationToken.None);
            int next = database.DataStorage.GetOwnerIndexPages(id).Count;
            (remaining - next).ShouldBeInRange(1, GraphStore.IndexReclaimPageLimit);
            remaining = next;
            passes++;
            passes.ShouldBeLessThanOrEqualTo(pages);
        }
        passes.ShouldBeGreaterThan(1);
        (database.Store.IndexOwnerPagesReclaimed - reclaimed).ShouldBe(pages);
        database.Coordinator.RunVersionPurgePass(CancellationToken.None); // Verify stable completion.
        (await RowsAsync(session, "MATCH (n:N) RETURN n.k ORDER BY n.k")).Select(row => row[0]).ShouldBe([1L, 2L]);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Text index 1 KiB cost is bounded and a 1 MiB value fails before postings (#1644)")]
    public async Task Execute_LongValues_ShouldMeasurePagesAndRefuseOversizedRecordAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-size", Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        ulong id;
        try { id = database.Store.GetIndexIdentities(context.Snapshot).Single().Id; }
        finally { await database.Coordinator.RollbackAsync(context, CancellationToken.None); }
        int before = database.DataStorage.GetOwnerIndexPages(id).Count;
        database.DataStorage.WriteBackDirtyPages(int.MaxValue);
        string value = UniqueAscii(1024);

        // Act
        await session.ExecuteAsync("INSERT (:N {k:1,p:$pattern})", Bind(value), CancellationToken.None);

        // Assert: unique ASCII trigrams maximize this input's posting count.
        int entries = GraphTextTrigrams.Keys(value, CancellationToken.None).Count();
        entries.ShouldBe(1022);
        int pages = database.DataStorage.GetOwnerIndexPages(id).Count;
        int pagesWritten = database.DataStorage.WriteBackDirtyPages(int.MaxValue);
        pagesWritten.ShouldBeGreaterThanOrEqualTo(pages);
        (await CountEntriesAsync(database, id)).ShouldBe(entries);
        _output.WriteLine($"1 KiB: entries={entries}; tree pages={pages}; new pages={pages - before}; pages written (all owners)={pagesWritten}; work={session.LastStatementMetrics!.Physical}; peak retained={session.LastStatementMetrics.Memory.PeakRetainedBytes}");
        (await RowsAsync(session, Query("CONTAINS"), value)).ShouldHaveSingleItem();
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("INSERT (:N {k:2,p:$pattern})", Bind(new string('x', 1024 * 1024)), CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02009");
        int rejectedPagesWritten = database.DataStorage.WriteBackDirtyPages(int.MaxValue);
        _output.WriteLine($"1 MiB: refused CG02009 before text postings; entries=0; new tree pages=0; pages written (all owners)={rejectedPagesWritten}; work={session.LastStatementMetrics!.Physical}; peak retained={session.LastStatementMetrics.Memory.PeakRetainedBytes}");
        database.DataStorage.GetOwnerIndexPages(id).Count.ShouldBe(pages);
        (await RowsAsync(session, "MATCH (n:N) RETURN n.k")).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    internal static long ReadIndexRoot(GraphDatabase database, ulong id)
    {
        long root = 0;
        using (var registrations = database.DataStorage.GetUnitIterator(1))
        {
            while (registrations.MoveNext())
            {
                var bytes = registrations.Current.Data;
                if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span.Slice(18)) == id)
                { root = BinaryPrimitives.ReadInt64LittleEndian(bytes.Span.Slice(26)); break; }
            }
        }
        root.ShouldBeGreaterThan(0);
        return root;
    }

    internal static async Task<int> CountEntriesAsync(GraphDatabase database, ulong id)
        => (await CountEntriesByTokenAsync(database, id)).Values.Sum();

    internal static List<(ulong Id, long Root)> Registrations(GraphDatabase database)
    {
        var result = new List<(ulong, long)>();
        using var records = database.DataStorage.GetUnitIterator(1);
        while (records.MoveNext())
        {
            var bytes = records.Current.Data.Span;
            result.Add((BinaryPrimitives.ReadUInt64LittleEndian(bytes[18..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[26..])));
        }
        return result;
    }

    internal static async Task<Dictionary<string, int>> CountEntriesByTokenAsync(GraphDatabase database, ulong id)
    {
        long root = ReadIndexRoot(database, id);
        var manager = BTreeIndexManager.Create(new()
        {
            Storage = database.DataStorage,
            TransactionSource = _ => throw new InvalidOperationException("Read-only measurement"),
            ExistingIndexes = [new(id, new IndexDefinition("graph"), root)],
        });
        manager.TryGetIndex(id, "graph", out var index).ShouldBeTrue();
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        try
        {
            var entries = new Dictionary<string, int>(StringComparer.Ordinal);
            await using var cursor = index.OpenCursor(context.Snapshot, IndexKeyRange.All);
            while (await cursor.MoveNextAsync(CancellationToken.None))
            {
                string key = Convert.ToHexString(cursor.CurrentKey.Encoded.Span[..^8]);
                entries[key] = entries.GetValueOrDefault(key) + 1;
            }
            return entries;
        }
        finally { await database.Coordinator.RollbackAsync(context, CancellationToken.None); }
    }

    internal static GraphDatabaseEngineOptions Quiet() => new()
    { CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) };

    internal static string UniqueAscii(int size)
    {
        // De Bruijn B(16,3): every consecutive triple is distinct over the measured prefix.
        var result = new StringBuilder();
        int[] a = new int[49];
        void Generate(int t, int period)
        {
            if (t > 3)
            { if (3 % period == 0) { for (int j = 1; j <= period; j++) { result.Append((char)('a' + a[j])); } } return; }
            a[t] = a[t - period]; Generate(t + 1, period);
            for (int j = a[t - period] + 1; j < 16; j++) { a[t] = j; Generate(t + 1, t); }
        }
        Generate(1, 1);
        return result.ToString(0, size);
    }

    internal static Dictionary<string, object?> Bind(object? value) => new() { ["pattern"] = value };
    internal static string Query(string operation) => $"MATCH (n:N) WHERE n.p {operation} $pattern RETURN n.k ORDER BY n.k";

    internal static async Task<List<object?[]>> RowsAsync(GraphDatabaseSession session, string source, object? pattern = null)
    {
        await using var result = (await session.ExecuteAsync(source, Bind(pattern), CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
