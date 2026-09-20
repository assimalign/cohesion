using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

/// <summary>Tests scalar GQL and mutations through the production graph server and typed client.</summary>
public sealed class GraphClientWireTests
{
    /// <summary>CREATE and MATCH preserve scalar values, columns, directions, and filtering over the wire.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - CREATE and MATCH round-trip scalar projections")]
    public async Task QueryAsync_CreateAndMatch_ShouldReturnScalarRows()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);

        // Act
        (await connection.ExecuteAsync(
            "CREATE (a:Person {name: 'Alice', age: 42, active: TRUE, optional: NULL})-[r:KNOWS {weight: 2}]->(b:Person {name: 'Bob', age: 17})",
            cancellationToken: harness.Token)).ShouldBe(3);
        var result = await connection.QueryAsync(
            "MATCH (b:Person {name: 'Bob'})<-[r:KNOWS]-(a) WHERE a.age >= 18 RETURN a.name AS person, a.age, a.active, a.optional, r.weight",
            cancellationToken: harness.Token);

        // Assert
        connection.Database.ShouldBe("graph");
        result.AffectedCount.ShouldBe(-1);
        result.Columns.Select(column => column.Name).ShouldBe(["person", "a.age", "a.active", "a.optional", "r.weight"]);
        result.Columns.Select(column => column.Ordinal).ShouldBe([0, 1, 2, 3, 4]);
        // Graph properties can be heterogeneous; the engine advertises unknown (Null) projection metadata.
        result.Columns.Select(column => column.Type).ShouldBe(
            [DatabaseType.Null, DatabaseType.Null, DatabaseType.Null, DatabaseType.Null, DatabaseType.Null]);
        var row = result.ShouldHaveSingleItem();
        row.ToArray().ShouldBe(new object?[] { "Alice", 42L, true, null, 2L });
        row["person"].ShouldBe("Alice");
        row[1].ShouldBeOfType<long>().ShouldBe(42L);
        (await connection.QueryAsync("MATCH (a:Person {name: 'Nobody'}) RETURN a.name", cancellationToken: harness.Token)).ShouldBeEmpty();
    }

    /// <summary>Bound endpoint creation and explicit relationship deletion preserve engine entity counts.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - MATCH CREATE and explicit DELETE preserve endpoint semantics")]
    public async Task ExecuteAsync_BoundCreateAndDelete_ShouldPreserveEndpoints()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("CREATE (:Person {name: 'Alice'}), (:Person {name: 'Bob'}), (:Person {name: 'Isolated'})",
            cancellationToken: harness.Token);

        // Act / Assert
        (await connection.ExecuteAsync(
            "MATCH (a:Person {name: 'Alice'}), (b:Person {name: 'Bob'}) CREATE (a)-[:KNOWS {since: 2026}]->(b)",
            cancellationToken: harness.Token)).ShouldBe(1);
        (await connection.QueryAsync("MATCH (n:Person) RETURN n.name", cancellationToken: harness.Token)).Count.ShouldBe(3);
        (await connection.ExecuteAsync("MATCH (n:Person {name: 'Isolated'}) DELETE n", cancellationToken: harness.Token)).ShouldBe(1);
        (await connection.ExecuteAsync("MATCH (a:Person {name: 'Alice'})-[r:KNOWS]->(b) DELETE a, r",
            cancellationToken: harness.Token)).ShouldBe(2);
        (await connection.QueryAsync("MATCH (n:Person) RETURN n.name", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe("Bob");
        (await connection.QueryAsync("MATCH (a)-[r:KNOWS]->(b) RETURN r.since", cancellationToken: harness.Token)).ShouldBeEmpty();
    }

    /// <summary>Restricted deletion fails atomically on cycles and DETACH DELETE removes incident edges once.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - DELETE rejection and DETACH DELETE retain cycle semantics")]
    public async Task ExecuteAsync_CyclicGraphDelete_ShouldRejectThenDetachAtomically()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync(
            "CREATE (:Vertex {name: 'Isolated'}), (a:Vertex {name: 'A'})-[:LINK]->(b:Vertex {name: 'B'})-[:LINK]->(a), (b)-[:LINK]->(b)",
            cancellationToken: harness.Token);
        Guid sessionId = harness.Server.Context.Sessions.ShouldHaveSingleItem().Id;

        // Act / Assert
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.ExecuteAsync("MATCH (n:Vertex) DELETE n", cancellationToken: harness.Token));
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldContain("COHDBG", Case.Sensitive);
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("MATCH (n:Vertex) RETURN n.name", cancellationToken: harness.Token))
            .Select(row => (string)row[0]!).Order(StringComparer.Ordinal).ShouldBe(["A", "B", "Isolated"]);
        (await connection.QueryAsync("MATCH (a)-[r:LINK]->(b) RETURN a.name", cancellationToken: harness.Token)).Count.ShouldBe(3);
        (await connection.ExecuteAsync("MATCH (b:Vertex {name: 'B'}) DETACH DELETE b", cancellationToken: harness.Token)).ShouldBe(1);
        (await connection.QueryAsync("MATCH (a)-[r:LINK]->(b) RETURN a.name", cancellationToken: harness.Token)).ShouldBeEmpty();
        (await connection.QueryAsync("MATCH (n:Vertex) RETURN n.name", cancellationToken: harness.Token))
            .Select(row => (string)row[0]!).Order(StringComparer.Ordinal).ShouldBe(["A", "Isolated"]);
        harness.Server.Context.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    /// <summary>Schema-owned definitions keep required-property validation and remain unchanged after data writes.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Schema-owned metadata constrains writes and remains owned")]
    public async Task ExecuteAsync_SchemaOwnedProperty_ShouldPreserveValidationAndOwnership()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        Guid labelId = Guid.NewGuid();
        await using (var session = await harness.Database.CreateSessionAsync(harness.Token))
        {
            var schema = GraphSchema.Open(harness.Database, session);
            await schema.SaveLabelAsync(new(labelId, "Managed"), harness.Token);
            await schema.SavePropertyKeyAsync(new(labelId, "name", DatabaseType.String, true), harness.Token);
            await schema.SaveLabelAsync(new(labelId, "Managed", DatabaseObjectOwner.Schema, "Application"), harness.Token);
        }
        await using var connection = await harness.Client.ConnectAsync(harness.Token);

        // Act / Assert: data mutation obeys metadata constraints; ownership protects catalog definitions.
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.ExecuteAsync("CREATE (:Managed {name: 7})", cancellationToken: harness.Token));
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldContain("COHDBG003", Case.Sensitive);
        (await connection.QueryAsync("MATCH (n:Managed) RETURN n.name", cancellationToken: harness.Token)).ShouldBeEmpty();
        await connection.ExecuteAsync("CREATE (:Managed {name: 'Allowed', extra: TRUE})", cancellationToken: harness.Token);
        var keys = await connection.QueryAsync("SHOW PROPERTY KEYS", cancellationToken: harness.Token);
        keys.ShouldHaveSingleItem()["PROPERTY_KEY"].ShouldBe("name");
        var ownership = await connection.QueryAsync("SHOW OBJECT OWNERSHIP", cancellationToken: harness.Token);
        ownership.Count.ShouldBe(2);
        ownership.ShouldAllBe(row => Equals(row[6], "Schema") && Equals(row[7], "Application"));
        await connection.ExecuteAsync("MATCH (n:Managed) DETACH DELETE n", cancellationToken: harness.Token);
        (await connection.QueryAsync("SHOW OBJECT OWNERSHIP", cancellationToken: harness.Token)).Count.ShouldBe(2);
    }

    /// <summary>All catalog subjects retain existing metadata types, values, and database scope.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - SHOW metadata and database scoping remain unchanged")]
    public async Task QueryAsync_ShowCatalog_ShouldPreserveMetadataAndScope()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        Guid labelId = Guid.NewGuid();
        Guid typeId = Guid.NewGuid();
        await using (var session = await harness.Database.CreateSessionAsync(harness.Token))
        {
            var schema = GraphSchema.Open(harness.Database, session);
            await schema.SaveLabelAsync(new(labelId, "Person"), harness.Token);
            await schema.SaveRelationshipTypeAsync(new(typeId, "KNOWS"), harness.Token);
            await schema.SavePropertyKeyAsync(new(labelId, "name", DatabaseType.String, true), harness.Token);
            await schema.CreateIndexAsync("Person", "by_name", "name", harness.Token);
        }
        var other = (IGraphDatabase)await harness.Engine.CreateDatabaseAsync("other", harness.Token);
        await using (var session = await other.CreateSessionAsync(harness.Token))
        {
            await GraphSchema.Open(other, session).SaveLabelAsync(new(Guid.NewGuid(), "Hidden"), harness.Token);
        }
        await using var connection = await harness.Client.ConnectAsync(harness.Token);

        // Act / Assert
        var labels = await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token);
        labels.Columns.Select(column => column.Name).ShouldBe(["DATABASE_NAME", "LABEL_ID", "LABEL_NAME"]);
        labels.Columns[1].Type.ShouldBe(DatabaseType.Guid);
        labels.ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { "graph", labelId, "Person" });
        (await connection.QueryAsync("SHOW RELATIONSHIP TYPES", cancellationToken: harness.Token))
            .ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { "graph", typeId, "KNOWS" });
        (await connection.QueryAsync("SHOW PROPERTY KEYS", cancellationToken: harness.Token))
            .ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { "graph", "LABEL", labelId, "Person", "name", "String", true });
        (await connection.QueryAsync("SHOW INDEXES", cancellationToken: harness.Token))
            .ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { "graph", labelId, "Person", "by_name", "name", false });
        (await connection.QueryAsync("SHOW OBJECT OWNERSHIP", cancellationToken: harness.Token)).Count.ShouldBe(4);
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("SHOW LABELS FROM other", cancellationToken: harness.Token));
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        (await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).ShouldHaveSingleItem()[2].ShouldBe("Person");

        await using var otherClient = GraphClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "other", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var otherConnection = await otherClient.ConnectAsync(harness.Token);
        (await otherConnection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).ShouldHaveSingleItem()[2].ShouldBe("Hidden");
    }

    /// <summary>Read-only SHOW diagnostics survive the new execute dispatcher and preserve its session.</summary>
    /// <param name="statement">A forbidden catalog mutation.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Catalog writes retain the read-only diagnostic")]
    [InlineData("SHOW LABELS DELETE n")]
    [InlineData("SHOW RELATIONSHIP TYPES SET name = 'x'")]
    [InlineData("SHOW PROPERTY KEYS REMOVE name")]
    [InlineData("SHOW INDEXES DROP INDEX by_name")]
    [InlineData("SHOW OBJECT OWNERSHIP CREATE (n)")]
    public async Task QueryAsync_CatalogMutation_ShouldRejectWithoutBreakingSession(string statement)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);

        // Act / Assert
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync(statement, cancellationToken: harness.Token));
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        error.Message.ShouldContain("GQL0007: Graph catalog introspection is read-only.", Case.Sensitive);
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).ShouldBeEmpty();
    }

    /// <summary>Complete statement errors retain the exact pooled server session across rentals.</summary>
    /// <param name="statement">The statement to reject.</param>
    /// <param name="code">The expected stable error code.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Statement failures preserve pooled authenticated sessions")]
    [InlineData("MATCH (a) RETURN *", ProtocolErrorCode.ParseFailure)]
    [InlineData("MATCH (a:Missing) RETURN a.name", ProtocolErrorCode.ExecutionFailure)]
    [InlineData("MATCH (a) RETURN a", ProtocolErrorCode.ExecutionFailure)]
    [InlineData("BEGIN", ProtocolErrorCode.ParseFailure)]
    [InlineData("COMMIT", ProtocolErrorCode.ParseFailure)]
    [InlineData("ROLLBACK", ProtocolErrorCode.ParseFailure)]
    public async Task QueryAsync_StatementFailure_ShouldReuseExactSession(string statement, ProtocolErrorCode code)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        Guid sessionId;
        await using (var connection = await harness.Client.ConnectAsync(harness.Token))
        {
            sessionId = harness.Server.Context.Sessions.ShouldHaveSingleItem().Id;

            // Act
            var error = await Should.ThrowAsync<GraphClientException>(async () =>
                await connection.QueryAsync(statement, cancellationToken: harness.Token));

            // Assert
            error.Code.ShouldBe(code);
            connection.IsOpen.ShouldBeTrue();
        }
        await using var reused = await harness.Client.ConnectAsync(harness.Token);
        (await reused.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).ShouldBeEmpty();
        harness.Server.Context.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    /// <summary>A mutation with a non-scalar result is rejected before its writes can be applied.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Entity projection on Execute fails before mutation")]
    public async Task ExecuteAsync_EntityProjection_ShouldRejectBeforeMutating()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);

        // Act / Assert
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("CREATE (n:Injected) RETURN n", cancellationToken: harness.Token));
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldContain("ExecutePaths", Case.Sensitive);
        (await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).ShouldBeEmpty();
    }

    /// <summary>Late teardown on an already returned wrapper cannot abort a newer rental of its pooled session.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Disposed wrapper teardown cannot affect a newer rental")]
    public async Task AbortAsync_ReturnedWrapper_ShouldLeaveNewRentalUsable()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        var returned = await harness.Client.ConnectAsync(harness.Token);
        Guid sessionId = harness.Server.Context.Sessions.ShouldHaveSingleItem().Id;
        await returned.DisposeAsync();
        await using var current = await harness.Client.ConnectAsync(harness.Token);

        // Act
        await returned.AbortAsync();
        await returned.DisposeAsync();

        // Assert
        returned.IsOpen.ShouldBeFalse();
        current.IsOpen.ShouldBeTrue();
        (await current.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).ShouldBeEmpty();
        harness.Server.Context.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }
}
