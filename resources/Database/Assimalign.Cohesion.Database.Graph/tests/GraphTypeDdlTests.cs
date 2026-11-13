using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphTypeDdlTests
{
    private const string definition = "ALTER CURRENT GRAPH TYPE SET {(:A =>), (:B =>), (:A)-[:R => {p :: INT64 NOT NULL}]->(:B)}";

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph type endpoints: latest edge and endpoint visibility prevents stale Snapshot admission")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_StaleEndpointOrIncidentRead_ShouldRefuse(bool createRelationship)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("edge-stale", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var first = await database.CreateSessionAsync(CancellationToken.None);
        await using var stale = await database.CreateSessionAsync(CancellationToken.None);
        await first.ExecuteAsync(definition, cancellationToken: CancellationToken.None);
        await first.ExecuteAsync("INSERT (:A),(:B)", cancellationToken: CancellationToken.None);
        await using var transaction = await stale.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await first.ExecuteAsync(createRelationship ? "MATCH (a:A) REMOVE a:A" : "MATCH (a:A),(b:B) INSERT (a)-[:R {p:1}]->(b)", cancellationToken: CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await stale.ExecuteAsync(createRelationship
            ? "MATCH (a:A),(b:B) INSERT (a)-[:R {p:2}]->(b)" : "MATCH (a:A) REMOVE a:A", cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03013");
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph type validation: a writer overlapping ALTER edge validation cannot admit a violation")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task AlterGraphTypeAsync_ConcurrentViolatingWriter_ShouldConflictOrRefuse(IsolationLevel isolation)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        using var release = new ManualResetEventSlim();
        var validating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = GraphDatabaseEngine.Create("edge-concurrent", new());
        var database = await engine.CreateDatabaseAsync("graph", token);
        await using var ddl = await database.CreateSessionAsync(token);
        await using var writer = await database.CreateSessionAsync(token);
        await ddl.ExecuteAsync("ALTER CURRENT GRAPH TYPE SET {(:A =>),(:B =>),(:B)-[:R =>]->(:A)}", cancellationToken: token);
        var source = await database.CreateNodeAsync(ddl, ["B"], cancellationToken: token);
        var target = await database.CreateNodeAsync(ddl, ["A"], cancellationToken: token);
        await using var transaction = await writer.BeginTransactionAsync(isolation, token);
        int validations = 0;
        database.Catalog.BeforeGraphTypeValidation = type =>
        {
            if (type != "R") { return; }
            Interlocked.Increment(ref validations);
            validating.TrySetResult();
            release.Wait(token);
        };
        var publication = Task.Run(async () => await ddl.ExecuteAsync("ALTER CURRENT GRAPH TYPE ALTER {(:A)-[:R =>]->(:B)}", cancellationToken: token), token);
        Task<DatabaseException>? refusal = null;
        // Act
        try
        {
            await validating.Task.WaitAsync(token);
            refusal = Task.Run(async () =>
            {
                var pending = database.CreateRelationshipAsync(writer, source.Id, target.Id, "R", cancellationToken: token);
                writing.TrySetResult();
                return await Should.ThrowAsync<DatabaseException>(async () => await pending);
            }, token);
            await writing.Task.WaitAsync(token);
        }
        finally { release.Set(); }
        try
        {
            await publication;
            // Assert
            (await refusal!).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(isolation == IsolationLevel.Snapshot ? "CG04007" : "CG03013");
            validations.ShouldBe(1);
            await transaction.RollbackAsync(token);
            (await GraphCatalogIntrospectionTests.Rows(ddl, "MATCH ()-[r:R]->() RETURN r.p")).ShouldBeEmpty();
        }
        finally { database.Catalog.BeforeGraphTypeValidation = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph type endpoints: unrelated writes retain expansion and storage work")]
    [InlineData("INSERT (a:U)-[:UNKNOWN]->(a)")]
    [InlineData("MATCH (a:U) SET a.p=1")]
    [InlineData("MATCH (a:U) SET a:Extra")]
    public async Task Execute_UntypedEdgeWrite_ShouldPreserveWork(string query)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("edge-work", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:U)-[:UNKNOWN]->(a)", cancellationToken: CancellationToken.None);
        long decoded = database.Store.IncidentRecordsDecoded;
        // Act
        await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        long work = session.LastStatementMetrics!.Consumed;
        long incident = database.Store.IncidentRecordsDecoded - decoded;
        if (query.Contains("Extra", StringComparison.Ordinal))
        { await session.ExecuteAsync("MATCH (a:U) REMOVE a:Extra", cancellationToken: CancellationToken.None); }
        await session.ExecuteAsync(definition, cancellationToken: CancellationToken.None);
        decoded = database.Store.IncidentRecordsDecoded;
        await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        // Assert
        session.LastStatementMetrics!.Consumed.ShouldBe(work);
        (database.Store.IncidentRecordsDecoded - decoded).ShouldBe(incident);
        incident.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph type edge matrix: every relationship and label write rolls back violations")]
    [InlineData("CREATE (:B)-[:R {p:1}]->(:A)", "CG03013")]
    [InlineData("INSERT (:A)-[:R {p:1}]->(:A)", "CG03013")]
    [InlineData("INSERT (:A)-[:R]->(:B)", "CG03003")]
    [InlineData("MERGE (:B)-[:R {p:1}]->(:A)", "CG03013")]
    [InlineData("MERGE (a:A)-[r:R {p:1}]->(b:B) ON MATCH SET r.p='bad'", "CG03003")]
    [InlineData("MERGE (a:A)-[r:R {p:1}]->(b:B) ON MATCH SET a:C REMOVE a:A", "CG03013")]
    [InlineData("MERGE (a:A)-[r:R {p:1}]->(b:B) ON CREATE SET r.p='bad'", "CG03003")]
    [InlineData("MATCH (a:A) REMOVE a:A", "CG03013")]
    [InlineData("MATCH (b:B) REMOVE b:B", "CG03013")]
    [InlineData("MATCH (a:A)-[r:R]->(b:B) SET a:C REMOVE a:A", "CG03013")]
    [InlineData("MATCH (a:A)-[r:R]->(b:B) REMOVE r.p", "CG03003")]
    [InlineData("MATCH (a:A)-[r:R]->(b:B) SET r={}", "CG03003")]
    public async Task Execute_EdgeViolation_ShouldRollbackAndRemainAvailable(string text, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("edge-matrix", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(definition, cancellationToken: CancellationToken.None);
        if (!text.Contains("ON CREATE", StringComparison.Ordinal))
        { await session.ExecuteAsync("INSERT (:A)-[:R {p:1}]->(:B)", cancellationToken: CancellationToken.None); }
        var previous = (await GraphCatalogIntrospectionTests.Rows(session, "SHOW CURRENT GRAPH TYPE"))[0][2];
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(text, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        (await GraphCatalogIntrospectionTests.Rows(session, "SHOW CURRENT GRAPH TYPE")).ShouldHaveSingleItem()[2].ShouldBe(previous);
        await session.ExecuteAsync("INSERT (:A)-[:R {p:2,extra:TRUE}]->(:B)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:Unknown)-[:UNKNOWN]->(a)", cancellationToken: CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph type DDL: SET ADD ALTER DROP and SHOW use one canonical complete type")]
    public async Task Execute_GraphTypeCommands_ShouldPreserveSemantics()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("edge-ddl", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        (await GraphCatalogIntrospectionTests.Rows(session, "SHOW CURRENT GRAPH TYPE")).ShouldBeEmpty();
        // Act
        await session.ExecuteAsync(definition, cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("ALTER CURRENT GRAPH TYPE ADD {(:C => :I {name :: STRING})}", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("ALTER CURRENT GRAPH TYPE ALTER {(:A)-[:R => {p :: INT64}]->(:C)}", cancellationToken: CancellationToken.None);
        var row = (await GraphCatalogIntrospectionTests.Rows(session, "SHOW CURRENT GRAPH TYPE")).ShouldHaveSingleItem();
        string canonical = (string)row[2]!;
        await session.ExecuteAsync(canonical, cancellationToken: CancellationToken.None);
        // Assert
        row[0].ShouldBe("graph"); row[1].ShouldBe("OPEN");
        (await GraphCatalogIntrospectionTests.Rows(session, "SHOW CURRENT GRAPH TYPE")).ShouldHaveSingleItem()[2].ShouldBe(canonical);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ALTER CURRENT GRAPH TYPE ALTER {(:New =>)}", cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03011");
        await session.ExecuteAsync("ALTER CURRENT GRAPH TYPE DROP {(:A =>), ()-[:R =>]->()}", cancellationToken: CancellationToken.None);
        var type = (await GraphSchema.Open(database, session).GetGraphTypeAsync(CancellationToken.None))!;
        type.EdgeTypes.ShouldBeEmpty(); type.NodeTypes.Select(node => node.IdentifyingLabel).ShouldBe(["B", "C"]);
        await session.ExecuteAsync("ALTER CURRENT GRAPH TYPE SET {}", cancellationToken: CancellationToken.None);
        (await GraphCatalogIntrospectionTests.Rows(session, "SHOW CURRENT GRAPH TYPE")).ShouldHaveSingleItem()[2].ShouldBe("ALTER CURRENT GRAPH TYPE SET {}");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph type validation: existing edge endpoints and properties preserve prior rules on refusal")]
    [InlineData("ALTER CURRENT GRAPH TYPE SET {(:A =>), (:B =>), (:B)-[:R =>]->(:A)}")]
    [InlineData("ALTER CURRENT GRAPH TYPE ALTER {(:B)-[:R =>]->(:A)}")]
    [InlineData("ALTER CURRENT GRAPH TYPE ALTER {(:A)-[:R => {p :: STRING}]->(:B)}")]
    public async Task Execute_ExistingEdgeViolation_ShouldPreservePriorType(string text)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("edge-validation", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(definition, cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT (:A)-[:R {p:1}]->(:B)", cancellationToken: CancellationToken.None);
        string previous = (string)(await GraphCatalogIntrospectionTests.Rows(session, "SHOW CURRENT GRAPH TYPE"))[0][2]!;
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(text, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03010");
        (await GraphCatalogIntrospectionTests.Rows(session, "SHOW CURRENT GRAPH TYPE"))[0][2].ShouldBe(previous);
        await session.ExecuteAsync("INSERT (:A)-[:R {p:2}]->(:B)", cancellationToken: CancellationToken.None);
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph type edge declarations: independent constraints agree or refuse in both publication orders")]
    public async Task Execute_IndependentEdgeConstraint_ShouldRefuseConflictingTypes()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("edge-constraint", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(definition, cancellationToken: CancellationToken.None);
        // Act / Assert
        var first = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("CREATE CONSTRAINT c FOR ()-[r:R]-() REQUIRE r.p IS :: STRING", cancellationToken: CancellationToken.None));
        first.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03009");
        await session.ExecuteAsync("CREATE CONSTRAINT c FOR ()-[r:R]-() REQUIRE r.p IS :: INT64", cancellationToken: CancellationToken.None);
        var second = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ALTER CURRENT GRAPH TYPE ALTER {(:A)-[:R => {p :: STRING}]->(:B)}", cancellationToken: CancellationToken.None));
        second.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03009");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph type DDL: long bodies obey admitted work and memory limits")]
    [InlineData(true, "CG05004")]
    [InlineData(false, "CG05020")]
    public async Task Execute_LongTypeBody_ShouldRespectStatementLimits(bool work, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("edge-budget", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(definition, cancellationToken: CancellationToken.None);
        string text = "ALTER CURRENT GRAPH TYPE ADD {" + string.Join(",", Enumerable.Range(0, 200).Select(i => "(:N" + i + " => {p :: STRING})")) + "}";
        var limits = work ? new GraphQueryLimits { WorkUnits = 10 } : new GraphQueryLimits { MemoryBytes = 4096 };
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(GraphQueryRequest.FromGql(text, null, limits), CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        (await GraphSchema.Open(database, session).GetGraphTypeAsync(CancellationToken.None))!.NodeTypes.Count.ShouldBe(2);
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph type edge rules: typed writes and stale snapshots cannot bypass endpoints")]
    public async Task CreateRelationshipAsync_StaleTypeAndInvalidEndpoints_ShouldRefuse()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("edge-snapshot", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var stale = await database.CreateSessionAsync(CancellationToken.None);
        var source = await database.CreateNodeAsync(session, ["B"], cancellationToken: CancellationToken.None);
        var target = await database.CreateNodeAsync(session, ["A"], cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE RELATIONSHIP TYPE R", cancellationToken: CancellationToken.None);
        await using var transaction = await stale.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await session.ExecuteAsync(definition, cancellationToken: CancellationToken.None);
        // Act
        var conflict = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await database.CreateRelationshipAsync(stale, source.Id, target.Id, "R", new Dictionary<string, object?> { ["p"] = 1L }, CancellationToken.None));
        var violation = await Should.ThrowAsync<DatabaseException>(async () => await database.CreateRelationshipAsync(session, source.Id, target.Id, "R", new Dictionary<string, object?> { ["p"] = 1L }, CancellationToken.None));
        // Assert
        conflict.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG04007");
        violation.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03013");
        await transaction.RollbackAsync(CancellationToken.None);
        await database.CreateRelationshipAsync(session, target.Id, source.Id, "R", new Dictionary<string, object?> { ["p"] = 1L }, CancellationToken.None);
    }
}
