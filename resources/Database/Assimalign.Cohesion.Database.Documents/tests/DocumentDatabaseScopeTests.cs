using System;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentDatabaseScopeTests
{
    [Theory]
    [InlineData("USE other")]
    [InlineData("CREATE DATABASE injected")]
    [InlineData("DROP DATABASE other")]
    [InlineData("SELECT * FROM other.items")]
    [InlineData("SHOW DATABASES")]
    [InlineData("SELECT * FROM other/items")]
    public async Task Session_commands_and_collection_handles_cannot_address_another_database(string command)
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var own = await engine.CreateDatabaseAsync("own");
        var other = await engine.CreateDatabaseAsync("other");
        var ownCollection = await own.CreateCollectionAsync("items");
        var otherCollection = await other.CreateCollectionAsync("items");
        await using var session = await own.CreateSessionAsync();
        await using var otherSession = await other.CreateSessionAsync();
        await ownCollection.PutAsync(session, "one", "\"own\""u8.ToArray());
        await otherCollection.PutAsync(otherSession, "one", "\"other\""u8.ToArray());
        session.Database.Name.ShouldBe(own.Name);
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(command));
        await Should.ThrowAsync<DatabaseException>(async () => await otherCollection.GetAsync(session, "one"));
        await Should.ThrowAsync<DatabaseException>(async () => await otherCollection.PutAsync(session, "one", "null"u8.ToArray()));
        await Should.ThrowAsync<DatabaseException>(async () => await otherCollection.DeleteAsync(session, "one"));
        await Should.ThrowAsync<DatabaseException>(async () => await session.GetCollectionAsync("other.items"));
        Encoding.UTF8.GetString((await otherCollection.GetAsync(otherSession, "one")).ShouldNotBeNull().Content.Span).ShouldBe("\"other\"");
        engine.TryGetDatabase("other", out _).ShouldBeTrue();
        engine.TryGetDatabase("injected", out _).ShouldBeFalse();
        var bound = await session.GetCollectionAsync("items");
        await session.DisposeAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await bound.GetAsync(session, "one"));
    }

    /// <summary>
    /// A collection's document operations take the typed <see cref="DocumentDatabaseSession"/>
    /// (concrete-types plan, phase 4): a null session is an argument error, and a session of
    /// another document database, or another session than the one a session-bound collection
    /// belongs to, is refused with the model's binding message, as before. A session of another
    /// model can no longer be passed at all.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Scope: a null session is an argument error, another database's or session's is refused")]
    public async Task GetAsync_NullForeignOrUnboundSession_ShouldRefuseByArgumentOrBinding()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var own = await engine.CreateDatabaseAsync("own");
        var other = await engine.CreateDatabaseAsync("other");
        var unbound = await own.CreateCollectionAsync("items");
        await using var session = await own.CreateSessionAsync();
        await using var sibling = await own.CreateSessionAsync();
        await using var foreign = await other.CreateSessionAsync();
        var bound = await session.GetCollectionAsync("items");

        // Act
        var nullSession = await Should.ThrowAsync<ArgumentNullException>(async () => await unbound.GetAsync(null!, "one"));
        var nullWrite = await Should.ThrowAsync<ArgumentNullException>(async () => await unbound.PutAsync(null!, "one", "1"u8.ToArray()));
        var foreignSession = await Should.ThrowAsync<DatabaseException>(async () => await unbound.GetAsync(foreign, "one"));
        var siblingSession = await Should.ThrowAsync<DatabaseException>(async () => await bound.GetAsync(sibling, "one"));
        var siblingRead = await unbound.GetAsync(sibling, "one");

        // Assert
        nullSession.ParamName.ShouldBe("session");
        nullWrite.ParamName.ShouldBe("session");
        foreignSession.Message.ShouldBe("The document collection and session must belong to the same bound database and session.");
        siblingSession.Message.ShouldBe(foreignSession.Message);
        siblingRead.ShouldBeNull();
    }
}
