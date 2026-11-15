using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphTypeFixTests
{
    private readonly ITestOutputHelper _output;

    public GraphTypeFixTests(ITestOutputHelper output) { _output = output; }

    private static GraphType Type() => GraphType.Create([GraphNodeType.Create("A"), GraphNodeType.Create("B")],
        [GraphEdgeType.Create("R", "A", "B", [GraphPropertyType.Create("p", DatabaseType.Int64, true)])]);

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph type label additions: typed imports fit the default limits (#1806)")]
    [InlineData("UNWIND $rows AS i CREATE (a:A)-[:R {p:1}]->(b:B) SET a:Tagged", 1000)]
    [InlineData("UNWIND $rows AS i MERGE (a:A {id:i})-[:R {p:1}]->(b:B {id:i}) ON CREATE SET a:New", 1400)]
    public async Task Execute_TypedImportWithLabelAddition_ShouldCompleteAsync(string query, int count)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-import", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await GraphSchema.Open(database, session).SetGraphTypeAsync(Type(), CancellationToken.None);
        // Act
        // RANGE is outside the profile; a bound list exercises the reviewed write shape.
        await session.ExecuteAsync(query,
            new Dictionary<string, object?> { ["rows"] = Enumerable.Range(1, count).Select(i => (object?)(long)i).ToArray() }, CancellationToken.None);
        _output.WriteLine("work=" + session.LastStatementMetrics!.Consumed);
        // Assert
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH ()-[r:R]->() RETURN count(r)")).ShouldHaveSingleItem()[0].ShouldBe((long)count);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph type endpoints: typed and GQL writes report endpoint errors first (#1806)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateRelationshipAsync_TwoViolations_ShouldReportEndpointFirstAsync(bool gql)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-precedence", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await GraphSchema.Open(database, session).SetGraphTypeAsync(Type(), CancellationToken.None);
        var node = await database.CreateNodeAsync(session, ["A"], cancellationToken: CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (gql) { await session.ExecuteAsync("MATCH (a:A) INSERT (a)-[:R]->(a)", cancellationToken: CancellationToken.None); }
            else { await database.CreateRelationshipAsync(session, node.Id, node.Id, "R", cancellationToken: CancellationToken.None); }
        });
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03013");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Snapshot unchanged writes: unrelated declarations do not conflict (#1806)")]
    [InlineData("label")]
    [InlineData("key")]
    [InlineData("edge")]
    public async Task Execute_SnapshotUnchangedWrite_ShouldSucceedAsync(string change)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-snapshot", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var ddl = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, ddl);
        await schema.SetGraphTypeAsync(Type(), CancellationToken.None);
        await ddl.ExecuteAsync("INSERT (:A {id:1})-[:R {p:1}]->(:B {id:2})", cancellationToken: CancellationToken.None);
        await using var transaction = await writer.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        if (change == "label")
        { await ddl.ExecuteAsync("ALTER CURRENT GRAPH TYPE ADD {(:X =>),(:X)-[:Q =>]->(:X)}", cancellationToken: CancellationToken.None); }
        else if (change == "key")
        { await ddl.ExecuteAsync("INSERT (:A)-[:R {p:1,z:1}]->(:B)", cancellationToken: CancellationToken.None); }
        else
        { await ddl.ExecuteAsync("ALTER CURRENT GRAPH TYPE ALTER {(:A)-[:R => {note :: STRING}]->(:B)}", cancellationToken: CancellationToken.None); }
        // Act
        await writer.ExecuteAsync(change == "label" ? "MATCH (n:A) SET n:Seen" : "MERGE (a:A {id:1})-[r:R]->(b:B {id:2}) RETURN r", cancellationToken: CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        // Assert
        (await GraphCatalogIntrospectionTests.Rows(ddl, change == "label" ? "MATCH (n:Seen) RETURN count(n)" : "MATCH (a:A {id:1})-[r:R]->(b:B {id:2}) RETURN count(r)"))
            .ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Snapshot label removal: only changed selected edge declarations conflict (#1806)")]
    [InlineData("unrelated", "Temp", null)]
    [InlineData("selected", "A", "CG04007")]
    [InlineData("dropped", "A", null)]
    public async Task Execute_SnapshotLabelRemovalAfterDdl_ShouldCheckSelectedTypesAsync(string change, string label, string? code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-removal-snapshot", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var ddl = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await GraphSchema.Open(database, ddl).SetGraphTypeAsync(Type(), CancellationToken.None);
        await ddl.ExecuteAsync("INSERT (:A:Temp)-[:R {p:1}]->(:B)", cancellationToken: CancellationToken.None);
        await using var transaction = await writer.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await ddl.ExecuteAsync(change switch
        {
            "unrelated" => "ALTER CURRENT GRAPH TYPE ADD {(:X =>),(:X)-[:Q =>]->(:X)}",
            "selected" => "ALTER CURRENT GRAPH TYPE ALTER {(:A)-[:R => {p :: INT64 NOT NULL,note :: STRING}]->(:B)}",
            _ => "ALTER CURRENT GRAPH TYPE DROP {()-[:R =>]->()}",
        }, cancellationToken: CancellationToken.None);
        string query = "MATCH (n:A) REMOVE n:" + label;
        // Act / Assert
        if (code is not null)
        {
            var refusal = await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync(query, cancellationToken: CancellationToken.None));
            refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        else
        {
            await writer.ExecuteAsync(query, cancellationToken: CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        (await GraphCatalogIntrospectionTests.Rows(ddl, "MATCH (n:" + label + ") RETURN count(n)"))
            .ShouldHaveSingleItem()[0].ShouldBe(code is null ? 0L : 1L);
        (await GraphCatalogIntrospectionTests.Rows(ddl, "MATCH ()-[r:R]->() RETURN count(r)"))
            .ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Untyped writes: counters equal the comparison base (#1806)")]
    [InlineData("INSERT (a:U)-[:UNKNOWN]->(a)", 0, 0, 0, 3426, false)]
    [InlineData("MATCH (a:U) SET a:Extra", 1, 0, 0, 11510, false)]
    [InlineData("MERGE (a:U)-[:UNKNOWN]->(a)", 3, 1, 1, 25332, false)]
    [InlineData("MATCH (a:U) REMOVE a:Extra", 1, 0, 0, 11722, false)]
    [InlineData("MATCH (a:U) REMOVE a:Extra", 1, 0, 0, 11722, true)]
    public async Task Execute_UntypedWrite_ShouldMatchBaseCountersAsync(string query, long work, long records, long rangeSeeks, long peakBytes, bool typed)
    {
        // Arrange
        // At 9973a4e36, work/decodes/memory are native metrics; test-only cursor-open instrumentation supplies seeks.
        await using var engine = GraphDatabaseEngine.Create("base-counters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        if (typed) { await GraphSchema.Open(database, session).SetGraphTypeAsync(Type(), CancellationToken.None); }
        await session.ExecuteAsync(query.Contains("REMOVE", StringComparison.Ordinal) ? "INSERT (a:U:Extra)-[:UNKNOWN]->(a)"
            : "INSERT (a:U)-[:UNKNOWN]->(a)", cancellationToken: CancellationToken.None);
        long decoded = database.Store.IncidentRecordsDecoded;
        long seeks = database.Store.IncidentRangeSeeks;
        // Act
        await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        // Assert
        _output.WriteLine($"COUNTERS|{query}|work={session.LastStatementMetrics!.Consumed}|decoded={database.Store.IncidentRecordsDecoded - decoded}|seeks={database.Store.IncidentRangeSeeks - seeks}");
        session.LastStatementMetrics.Consumed.ShouldBe(work);
        (database.Store.IncidentRecordsDecoded - decoded).ShouldBe(records);
        (database.Store.IncidentRangeSeeks - seeks).ShouldBe(rangeSeeks);
        session.LastStatementMetrics.Memory.PeakRetainedBytes.ShouldBe(peakBytes);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Unrelated label removal: bulk writes retain the base memory envelope (#1806)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_UnrelatedBulkLabelRemoval_ShouldFitBaseMemoryEnvelopeAsync(bool typed)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-removal-memory", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        if (typed) { await GraphSchema.Open(database, session).SetGraphTypeAsync(Type(), CancellationToken.None); }
        await session.ExecuteAsync("UNWIND $rows AS i INSERT (:U:Extra)",
            new Dictionary<string, object?> { ["rows"] = Enumerable.Range(0, 100).Select(i => (object?)(long)i).ToArray() }, CancellationToken.None);
        // The base peaks at 155,522 B; 4 KiB admits the statement's one catalog map.
        var limits = new GraphQueryLimits { MemoryBytes = 155_522 + 4096 };
        // Act
        await session.ExecuteAsync(GraphQueryRequest.FromGql("MATCH (a:U) REMOVE a:Extra", null, limits), CancellationToken.None);
        var metrics = session.LastStatementMetrics!;
        // Assert
        metrics.Consumed.ShouldBe(100);
        metrics.Memory.ActiveLeases.ShouldBe(0);
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH (n:Extra) RETURN count(n)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH (n:U) RETURN count(n)")).ShouldHaveSingleItem()[0].ShouldBe(100L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Label removal: one stale check serves every node and both endpoint directions (#1806)")]
    public async Task Execute_RepeatedSelectedEdgeType_ShouldReadDeclarationOnceAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-removal-checks", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await GraphSchema.Open(database, session).SetGraphTypeAsync(Type(), CancellationToken.None);
        await session.ExecuteAsync("INSERT (:A:B),(:A:B),(:A:B),(:Unrelated)-[:UNKNOWN]->(:Unrelated)", cancellationToken: CancellationToken.None);
        int reads = 0;
        long seeks = database.Store.IncidentRangeSeeks;
        long decoded = database.Store.IncidentRecordsDecoded;
        database.Catalog.BeforeRecordRead = () => reads++;
        // Act
        try { await session.ExecuteAsync("MATCH (a:A) REMOVE a:A:B", cancellationToken: CancellationToken.None); }
        finally { database.Catalog.BeforeRecordRead = null; }
        // Assert: two token reads, one map declaration, its type token and one stale check.
        reads.ShouldBe(5);
        (database.Store.IncidentRangeSeeks - seeks).ShouldBe(6);
        (database.Store.IncidentRecordsDecoded - decoded).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph type label writes: additions and unrelated removals open no incident cursors (#1806)")]
    [InlineData("MATCH (n:A) SET n:Seen")]
    [InlineData("MATCH (n:A) REMOVE n:Temp")]
    public async Task Execute_UnchangedEndpointMembership_ShouldSeekNothingAsync(string query)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-label-seeks", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await GraphSchema.Open(database, session).SetGraphTypeAsync(Type(), CancellationToken.None);
        await session.ExecuteAsync("INSERT (:A:Temp)-[:R {p:1}]->(:B)", cancellationToken: CancellationToken.None);
        long seeks = database.Store.IncidentRangeSeeks;
        long decoded = database.Store.IncidentRecordsDecoded;
        // Act
        await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        // Assert
        (database.Store.IncidentRangeSeeks - seeks).ShouldBe(0);
        (database.Store.IncidentRecordsDecoded - decoded).ShouldBe(0);
        session.LastStatementMetrics!.Memory.PeakRetainedBytes.ShouldBe(11718);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph type label removal: only a source rule's outgoing range is sought (#1806)")]
    public async Task Execute_RemovedSourceLabel_ShouldSeekOnlyOutgoingTypeAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-direction", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("ALTER CURRENT GRAPH TYPE SET {(:A =>),(:B =>),(:X =>),(:A)-[:R =>]->(:B),(:X)-[:S =>]->(:B)}", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT (:A)-[:R]->(n:A:B:X {id:1})-[:S]->(:B)", cancellationToken: CancellationToken.None);
        long seeks = database.Store.IncidentRangeSeeks;
        long decoded = database.Store.IncidentRecordsDecoded;
        // Act
        await session.ExecuteAsync("MATCH (n {id:1}) REMOVE n:A", cancellationToken: CancellationToken.None);
        // Assert
        (database.Store.IncidentRangeSeeks - seeks).ShouldBe(1);
        (database.Store.IncidentRecordsDecoded - decoded).ShouldBe(0);
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH ()-[r]->() RETURN count(r)")).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph type stage order: deleting the edge permits subsequent identifying-label removal (#1806)")]
    public async Task Execute_DeleteThenRemoveLabel_ShouldCompleteAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-stage-order", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await GraphSchema.Open(database, session).SetGraphTypeAsync(Type(), CancellationToken.None);
        await session.ExecuteAsync("INSERT (:A)-[:R {p:1}]->(:B)", cancellationToken: CancellationToken.None);
        // Act
        await session.ExecuteAsync("MATCH (a:A)-[r:R]->(b:B) DELETE r REMOVE a:A", cancellationToken: CancellationToken.None);
        // Assert
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH ()-[r]->() RETURN count(r)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH (n:A) RETURN count(n)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH (n) RETURN count(n)")).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph type SHOW: every supported scalar round-trips through its explicit storage inverse (#1806)")]
    public async Task Execute_ShowAllScalarTypes_ShouldRoundTripAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-scalars", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        DatabaseType[] types = [DatabaseType.Boolean, DatabaseType.Int8, DatabaseType.Int16, DatabaseType.Int32, DatabaseType.Int64,
            DatabaseType.Float32, DatabaseType.Float64, DatabaseType.Decimal, DatabaseType.String];
        await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("A", properties:
            types.Select(type => GraphPropertyType.Create(type.ToString(), type)))]), CancellationToken.None);
        // Act
        string text = (string)(await GraphCatalogIntrospectionTests.Rows(session, "SHOW CURRENT GRAPH TYPE")).ShouldHaveSingleItem()[2]!;
        await session.ExecuteAsync(text, cancellationToken: CancellationToken.None);
        // Assert
        (await schema.GetGraphTypeAsync(CancellationToken.None))!.NodeTypes.ShouldHaveSingleItem().Properties.Select(property => (property.Name, property.Type))
            .ShouldBe(types.OrderBy(type => type.ToString(), StringComparer.Ordinal).Select(type => (type.ToString(), type)));
    }
}
