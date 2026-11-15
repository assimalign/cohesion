using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
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

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph type endpoints: typed and GQL writes report endpoint errors first (#1806)")]
    public async Task CreateRelationshipAsync_TwoViolations_ShouldReportEndpointFirstAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-precedence", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await GraphSchema.Open(database, session).SetGraphTypeAsync(Type(), CancellationToken.None);
        var node = await database.CreateNodeAsync(session, ["A"], cancellationToken: CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await database.CreateRelationshipAsync(session, node.Id, node.Id, "R", cancellationToken: CancellationToken.None));
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

    [Theory(DisplayName = "Cohesion Test [Graph] - Untyped writes: counters equal the batch base (#1806)")]
    [InlineData("INSERT (a:U)-[:UNKNOWN]->(a)", 0, 0, 0)]
    [InlineData("MATCH (a:U) SET a:Extra", 1, 0, 0)]
    [InlineData("MERGE (a:U)-[:UNKNOWN]->(a)", 3, 1, 1)]
    public async Task Execute_UntypedWrite_ShouldMatchBaseCountersAsync(string query, long work, long records, long rangeSeeks)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("base-counters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:U)-[:UNKNOWN]->(a)", cancellationToken: CancellationToken.None);
        long decoded = database.Store.IncidentRecordsDecoded;
        long seeks = database.Store.IncidentRangeSeeks;
        // Act
        await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        // Assert
        _output.WriteLine($"COUNTERS|{query}|work={session.LastStatementMetrics!.Consumed}|decoded={database.Store.IncidentRecordsDecoded - decoded}|seeks={database.Store.IncidentRangeSeeks - seeks}");
        session.LastStatementMetrics.Consumed.ShouldBe(work);
        (database.Store.IncidentRecordsDecoded - decoded).ShouldBe(records);
        (database.Store.IncidentRangeSeeks - seeks).ShouldBe(rangeSeeks);
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
        (await schema.GetGraphTypeAsync(CancellationToken.None))!.NodeTypes.ShouldHaveSingleItem().Properties.Select(property => property.Type)
            .Order().ShouldBe(types.Order());
    }
}
