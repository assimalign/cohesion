using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphRelationshipTypeAnchorTests
{
    private readonly ITestOutputHelper _output;

    public GraphRelationshipTypeAnchorTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - Type anchors: directions, unions and self-loops match the node fallback (#1633)")]
    [InlineData("(a)-[r:Keep]->(b)", 3)]
    [InlineData("(a)<-[r:Keep]-(b)", 3)]
    [InlineData("(a)-[r:Keep]-(b)", 5)]
    [InlineData("(a)<-[r:Keep]->(b)", 5)]
    [InlineData("(a)-[r:Keep|Other|Keep]->(b)", 4)]
    public async Task Match_TypeOnlyPattern_ShouldConsumePostingsWithoutIncidentExpansionAsync(string pattern, int count)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-directions", new());
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a {k:1})-[:Keep {k:1}]->(b {k:2})-[:Keep {k:2}]->(c {k:3}), (c)-[:Keep {k:3}]->(c), (a)-[:Other {k:4}]->(b)", cancellationToken: CancellationToken.None);
        string source = "MATCH p = " + pattern + " RETURN p";

        // Act
        var indexed = await MatchAsync(db, session, source);
        var reference = await MatchAsync(db, session, source, fallback: true);
        var description = await RowsAsync(session, "EXPLAIN " + source);

        // Assert
        indexed.Paths.ShouldBe(reference.Paths, ignoreOrder: true);
        indexed.Paths.Count.ShouldBe(count);
        indexed.Budget.Physical.ShouldBe(pattern.Contains('|', StringComparison.Ordinal) ? 4 : 3);
        indexed.Budget.Viable.ShouldBe(count);
        description.Count(row => Equals(row[2], "RelationshipTypeScan")).ShouldBe(1);
        description.Any(row => row[2] is "Expand" or "NodeScan").ShouldBeFalse();
        description.Single(row => Equals(row[2], "RelationshipTypeScan"))[7].ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Interior type anchors preserve source order and path modes (#1633)")]
    [InlineData("TRAIL")]
    [InlineData("WALK")]
    [InlineData("SIMPLE")]
    [InlineData("ACYCLIC")]
    public async Task Match_InteriorTypeAnchor_ShouldExpandBothSidesInSourceOrderAsync(string mode)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-interior", new());
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT ({k:0})-[:Common]->({k:1})<-[:Keep {w:7}]-({k:2})-[:Common]->({k:3}), ()-[:Common]->()", cancellationToken: CancellationToken.None);
        string source = $"MATCH p = {mode} (a)-[:Common]->(b)<-[r:Keep {{w:7}}]-(c)-[:Common]->(d) RETURN p";

        // Act
        var indexed = await MatchAsync(db, session, source);
        var reference = await MatchAsync(db, session, source, fallback: true);
        var description = await RowsAsync(session, "EXPLAIN " + source);

        // Assert
        indexed.Paths.ShouldBe(reference.Paths);
        indexed.Paths.ShouldHaveSingleItem();
        indexed.Budget.Physical.ShouldBe(3);
        description.Single(row => Equals(row[2], "RelationshipTypeScan"))[5]!.ToString()!.ShouldStartWith("edge=1; direction=Incoming;");
        description.Where(row => Equals(row[2], "Expand")).Select(row => row[5]!.ToString()!.Split(';')[0]).ShouldBe(["edge=2", "edge=0"]);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Type anchors retain safe scans for nullable or unrestricted type expressions (#1633)")]
    [InlineData("(a)-[:Keep]->{0,1}(b)")]
    [InlineData("(a)-[:%]->(b)")]
    [InlineData("(a)-[:!Keep]->(b)")]
    [InlineData("(a)-[:Keep|!Other]->(b)")]
    public async Task Plan_UnsafeTypeLeaf_ShouldRetainFallbackAsync(string pattern)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-safe", new());
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT ()-[:Keep]->(), ()-[:Other]->()", cancellationToken: CancellationToken.None);
        string source = "MATCH p = " + pattern + " RETURN p";

        // Act
        var chosen = await MatchAsync(db, session, source);
        var fallback = await MatchAsync(db, session, source, fallback: true);
        var description = await RowsAsync(session, "EXPLAIN " + source);

        // Assert
        chosen.Paths.ShouldBe(fallback.Paths, ignoreOrder: true);
        description.Count(row => Equals(row[2], "NodeScan")).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Type anchors rebuild estimates on reopen and bound unselective property estimates (#1633)")]
    public async Task Plan_ReopenAndUnselectiveIndex_ShouldChooseTypeWithoutCountingWholeIndexAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-estimate", new());
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using (var seed = await db.CreateSessionAsync(CancellationToken.None))
        {
            await seed.ExecuteAsync("FOR k IN [" + string.Join(',', Enumerable.Range(0, 600)) + "] INSERT (:Common {shared:1})", cancellationToken: CancellationToken.None);
            await seed.ExecuteAsync("INSERT (:Common {shared:1})-[:Keep {w:1}]->()", cancellationToken: CancellationToken.None);
            await GraphSchema.Open(db, seed).CreateIndexAsync("Common", "by_shared", "shared", CancellationToken.None);
        }
        await db.DisposeAsync();

        // Act
        db = await engine.OpenDatabaseAsync("graph", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        var anchor = await AnchorAsync(session, "MATCH (a:Common {shared:1})-[r:Keep]->(b) RETURN r.w");
        var estimate = await db.RunAsync(session, operation =>
        {
            var index = db.Store.GetIndexIdentities(operation.Context.Snapshot).Single();
            return new ValueTask<long>(db.Store.EstimateIndexCount(index.Id, "Common", 1L, operation.Context.Snapshot, CancellationToken.None));
        }, CancellationToken.None);

        // Assert
        estimate.ShouldBe(601); // Incomplete after 512 entries: use retained label population.
        anchor[2].ShouldBe("RelationshipTypeScan");
        anchor[6].ShouldBe(1L);
        (await RowsAsync(session, "MATCH (a:Common {shared:1})-[r:Keep]->(b) RETURN r.w")).ShouldHaveSingleItem().ShouldBe([1L]);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Type anchor cost competes with labels and property seeks (#1633)")]
    public async Task Plan_CompetingAnchors_ShouldChooseByEstimateAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-cost", new());
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("FOR k IN [0,1,2,3,4,5,6,7,8,9] INSERT (:Common {k:k})-[:Wide]->(:End)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Common {k:20})-[:Rare]->(:End:Unique)", cancellationToken: CancellationToken.None);
        await GraphSchema.Open(db, session).CreateIndexAsync("Common", "by_k", "k", CancellationToken.None);

        // Act / Assert
        var type = await AnchorAsync(session, "MATCH (a:Common {k:0})-[:Rare]->(b) RETURN a.k");
        type[2].ShouldBe("IndexSeek"); // Equal cost preserves the existing equality-seek preference.
        type[6].ShouldBe(1L);
        (await AnchorAsync(session, "MATCH (a:Common {k:99})-[:Rare]->(b) RETURN a.k"))[6].ShouldBe(0L);
        (await AnchorAsync(session, "MATCH (a:Common)-[:Rare]->(b) RETURN a.k"))[2].ShouldBe("RelationshipTypeScan");
        (await AnchorAsync(session, "MATCH (a)-[:Wide|Rare]->(b:Unique) RETURN a.k"))[2].ShouldBe("LabelScan");
        (await AnchorAsync(session, "MATCH (a:Common {k:0})-[:Wide]->(b) RETURN a.k"))[2].ShouldBe("IndexSeek");
        // Bound nodes continue to take precedence, even when the relationship estimate is lower.
        var bound = await RowsAsync(session, "EXPLAIN MATCH (a:Common) MATCH (a)-[:Rare]->(b) RETURN a.k");
        bound.Count(row => Equals(row[2], "RelationshipTypeScan")).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Type anchors merge staged creations, replacements and deletions (#1633)")]
    public async Task Execute_StatementOverlay_ShouldReadCurrentRelationshipsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-overlay", new());
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT ()-[:Keep {w:1}]->(), ()-[:Noise]->()", cancellationToken: CancellationToken.None);

        // Act / Assert
        (await RowsAsync(session, "MATCH ()-[r:Keep]->() SET r.w=2 MATCH ()-[s:Keep]->() RETURN s.w")).ShouldHaveSingleItem().ShouldBe([2L]);
        (await RowsAsync(session, "INSERT ()-[:Keep {w:3}]->() MATCH ()-[s:Keep]->() RETURN s.w ORDER BY s.w")).Select(row => row[0]).ShouldBe([2L, 3L]);
        (await RowsAsync(session, "MATCH ()-[r:Keep]->() WHERE r.w=2 DELETE r MATCH ()-[s:Keep]->() RETURN s.w")).ShouldHaveSingleItem().ShouldBe([3L]);
        (await RowsAsync(session, "INSERT ()-[:Fresh {w:4}]->() MATCH ()-[s:Fresh]->() RETURN s.w")).ShouldHaveSingleItem().ShouldBe([4L]);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT ()-[:Keep {w:5}]->()", cancellationToken: CancellationToken.None);
        (await RowsAsync(session, "MATCH ()-[r:Keep]->() RETURN r.w ORDER BY r.w")).Select(row => row[0]).ShouldBe([3L, 5L]);
        await transaction.RollbackAsync(CancellationToken.None);
        (await RowsAsync(session, "MATCH ()-[r:Keep]->() RETURN r.w")).ShouldHaveSingleItem().ShouldBe([3L]);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Type anchor snapshots survive replacement, delete and rollback (#1633)")]
    public async Task Execute_OldSnapshot_ShouldRetainVisiblePostingsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-snapshot", new());
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var reader = await db.CreateSessionAsync(CancellationToken.None);
        await using var writer = await db.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("INSERT ()-[:Keep {w:1}]->(), ()-[:Keep {w:2}]->()", cancellationToken: CancellationToken.None);
        await using var old = await reader.BeginTransactionAsync(Transactions.IsolationLevel.Snapshot, CancellationToken.None);

        // Act
        await writer.ExecuteAsync("MATCH ()-[r:Keep]->() WHERE r.w=1 SET r.w=10", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("MATCH ()-[r:Keep]->() WHERE r.w=2 DELETE r", cancellationToken: CancellationToken.None);
        var historical = await RowsAsync(reader, "MATCH ()-[r:Keep]->() RETURN r.w ORDER BY r.w");
        var current = await RowsAsync(writer, "MATCH ()-[r:Keep]->() RETURN r.w");

        // Assert
        historical.Select(row => row[0]).ShouldBe([1L, 2L]);
        current.ShouldHaveSingleItem().ShouldBe([10L]);
        await old.RollbackAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Type anchors avoid all-node and incident work on a large unrelated graph (#1633)")]
    public async Task Match_LargeUnrelatedGraph_ShouldVisitOnlySelectedPostingsAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = GraphDatabaseEngine.Create("type-work", new());
        var db = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await db.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("FOR k IN [" + string.Join(',', Enumerable.Range(0, 2_000)) + "] INSERT ()-[:Noise]->()", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT ()-[:Keep]->(), ()-[:Keep]->(), ()-[:Keep]->()", cancellationToken: timeout.Token);
        const string source = "MATCH p = (a)-[r:Keep]->(b) RETURN p";

        for (int round = 0; round < 3; round++)
        {
            // Act
            long before = db.Store.NodeRecordsDecoded;
            long postings = db.Store.RelationshipTypeEntriesVisited;
            var indexed = await MatchAsync(db, session, source);
            long decoded = db.Store.NodeRecordsDecoded - before;
            long visits = db.Store.RelationshipTypeEntriesVisited - postings;
            before = db.Store.NodeRecordsDecoded;
            var fallback = await MatchAsync(db, session, source, fallback: true);
            long fallbackDecoded = db.Store.NodeRecordsDecoded - before;

            // Assert
            indexed.Paths.ShouldBe(fallback.Paths, ignoreOrder: true);
            indexed.Budget.Physical.ShouldBe(3);
            indexed.Budget.Viable.ShouldBe(3);
            visits.ShouldBe(3);
            decoded.ShouldBe(6);
            fallback.Budget.Physical.ShouldBe(4_009);
            fallbackDecoded.ShouldBe(4_009);
            _output.WriteLine($"Round {round + 1}: fallback -> type anchor: physical {fallback.Budget.Physical} -> {indexed.Budget.Physical}; node decodes {fallbackDecoded} -> {decoded}; selected postings {visits}.");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Type anchors release cursors and grants on early stop, cancellation and budget refusal (#1633)")]
    public async Task Execute_InterruptedTypeScan_ShouldReleaseOwnedStateAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-cleanup", new());
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("FOR k IN [0,1,2,3,4] INSERT ()-[:Keep {w:k}]->()", cancellationToken: CancellationToken.None);

        // Act / Assert
        (await RowsAsync(session, "MATCH ()-[r:Keep]->() RETURN r.w LIMIT 1")).Count.ShouldBe(1);
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        var request = GraphQueryRequest.FromGql("MATCH ()-[r:Keep]->() RETURN r.w", null, new GraphQueryLimits { WorkUnits = 1 });
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None))).Diagnostics[0].Code.ShouldBe("CG05004");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        using var cancel = new CancellationTokenSource();
        int read = 0;
        db.Store.OnRelationshipTypeEntryRead = () => { if (++read == 2) { cancel.Cancel(); } };
        try
        {
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("MATCH ()-[r:Keep]->() RETURN r.w", cancellationToken: cancel.Token));
            read.ShouldBe(2);
            session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
            session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        }
        finally { db.Store.OnRelationshipTypeEntryRead = null; }
        (await RowsAsync(session, "MATCH ()-[r:Keep]->() RETURN r.w")).Count.ShouldBe(5);
        db.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }

    private static async Task<(List<string> Paths, GraphPlanExecutor.ExpansionBudget Budget)> MatchAsync(
        GraphDatabase db, GraphDatabaseSession session, string source, bool fallback = false)
    {
        var budget = new GraphPlanExecutor.ExpansionBudget();
        var output = new List<Dictionary<string, object?>>();
        await db.RunAsync(session, async operation =>
        {
            operation.QueryBudget = budget;
            var plan = new GraphPlanner(db, operation.Context.Snapshot).Plan(GraphPathsQueryRequest.FromGql(source).Statement.GqlExpression, paths: true);
            var path = plan.MatchClauses[0].Paths[0];
            if (fallback) { path = path with { Anchor = new GraphAnchor(0, null, null, null) }; }
            await GraphPlanExecutor.MatchAsync(db, operation, path, new(StringComparer.Ordinal), output, budget, null, CancellationToken.None);
            return true;
        }, CancellationToken.None);
        return (output.Select(row => (GraphPath)row["p"]!).Select(path => string.Join(',', path.Nodes.Select(node => node.Id.Value)) + "/" +
            string.Join(',', path.Relationships.Select(edge => edge.Id.Value))).ToList(), budget);
    }

    private static async Task<object?[]> AnchorAsync(GraphDatabaseSession session, string source)
        => (await RowsAsync(session, "EXPLAIN " + source)).Single(row => row[2] is "RelationshipTypeScan" or "IndexSeek" or "LabelScan" or "NodeScan");

    private static async Task<List<object?[]>> RowsAsync(GraphDatabaseSession session, string source)
    {
        await using var result = (await session.ExecuteAsync(source, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
