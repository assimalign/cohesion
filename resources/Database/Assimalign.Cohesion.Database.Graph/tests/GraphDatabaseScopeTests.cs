using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphDatabaseScopeTests
{
    [Theory]
    [InlineData("USE other")]
    [InlineData("CREATE DATABASE injected")]
    [InlineData("DROP DATABASE other")]
    [InlineData("SHOW DATABASES")]
    [InlineData("MATCH (a:other.Person) RETURN a")]
    [InlineData("MATCH (a:Person) AT other RETURN a")]
    public async Task Every_operation_checks_database_binding_and_Gql_cannot_reach_server_scope(string command)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var own = await engine.CreateDatabaseAsync("own");
        var other = await engine.CreateDatabaseAsync("other");
        await using var session = await own.CreateSessionAsync();
        await using var otherSession = await other.CreateSessionAsync();
        var a = await other.CreateNodeAsync(otherSession, ["Person"]);
        var b = await other.CreateNodeAsync(otherSession, ["Person"]);
        var edge = await other.CreateRelationshipAsync(otherSession, a.Id, b.Id, "KNOWS");
        session.Database.ShouldBeSameAs(own);
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(command));
        await Should.ThrowAsync<DatabaseException>(async () => await other.CreateNodeAsync(session, ["Person"]));
        await Should.ThrowAsync<DatabaseException>(async () => await other.GetNodeAsync(session, a.Id));
        await Should.ThrowAsync<DatabaseException>(async () => await other.DeleteNodeAsync(session, a.Id));
        await Should.ThrowAsync<DatabaseException>(async () => await other.CreateRelationshipAsync(session, a.Id, b.Id, "KNOWS"));
        await Should.ThrowAsync<DatabaseException>(async () => await other.DeleteRelationshipAsync(session, edge.Id));
        await Should.ThrowAsync<DatabaseException>(async () => { await foreach (var node in other.TraverseAsync(session, new(a.Id))) { } });
        Should.Throw<DatabaseException>(() => GraphSchema.Open(other, session));
        (await other.GetNodeAsync(otherSession, a.Id)).ShouldNotBeNull();
        engine.TryGetDatabase("other", out _).ShouldBeTrue();
        engine.TryGetDatabase("injected", out _).ShouldBeFalse();
        await session.DisposeAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await own.CreateNodeAsync(session, ["Person"]));
    }

    /// <summary>
    /// The typed operations and <see cref="GraphSchema.Open"/> take the typed session and database
    /// (concrete-types plan, phase 4): a null one is an argument error, where the interface-typed
    /// members reported it as a session of another database (<c>COHDBG005</c>), and a session of
    /// another database is still <c>COHDBG005</c>.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Scope: a null session or database is an argument error, another database's session is COHDBG005")]
    public async Task TypedOperations_NullOrForeignSession_ShouldRefuseByArgumentOrCode()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var own = await engine.CreateDatabaseAsync("own");
        var other = await engine.CreateDatabaseAsync("other");
        await using var foreign = await other.CreateSessionAsync();

        // Act
        var nullSession = await Should.ThrowAsync<ArgumentNullException>(async () => await own.CreateNodeAsync(null!, ["Person"]));
        var nullRead = await Should.ThrowAsync<ArgumentNullException>(async () => await own.GetNodeAsync(null!, new GraphNodeId(1)));
        var nullSchemaSession = Should.Throw<ArgumentNullException>(() => GraphSchema.Open(own, null!));
        var nullSchemaDatabase = Should.Throw<ArgumentNullException>(() => GraphSchema.Open(null!, foreign));
        var foreignSession = await Should.ThrowAsync<DatabaseException>(async () => await own.CreateNodeAsync(foreign, ["Person"]));
        var foreignSchema = Should.Throw<DatabaseException>(() => GraphSchema.Open(own, foreign));

        // Assert
        nullSession.ParamName.ShouldBe("session");
        nullRead.ParamName.ShouldBe("session");
        nullSchemaSession.ParamName.ShouldBe("session");
        nullSchemaDatabase.ParamName.ShouldBe("database");
        foreignSession.Message.ShouldStartWith("COHDBG005", Case.Sensitive);
        foreignSchema.Message.ShouldStartWith("COHDBG005", Case.Sensitive);
    }
}
