using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using Assimalign.Cohesion.Database.Execution;
using static KeyValueTestHarness;

/// <summary>
/// Guards the database-only execution boundary shared by every model (A5).
/// </summary>
public sealed class KeyValueDatabaseScopeTests
{
    /// <summary>
    /// A session resolves keys only in its database, including typed operations.
    /// </summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Scope: A session cannot address another database")]
    public async Task Session_ShouldRemainBoundToOneDatabase()
    {
        await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "scope" });
        var first = (IKeyValueDatabase)await engine.CreateDatabaseAsync("first");
        var second = (IKeyValueDatabase)await engine.CreateDatabaseAsync("second");
        await using var session = await first.CreateSessionAsync();
        await using var other = await second.CreateSessionAsync();
        var key = Bytes("marker");
        await first.PutAsync(session, key, Bytes("first"));
        await second.PutAsync(other, key, Bytes("second"));

        await Should.ThrowAsync<DatabaseException>(async () => await second.GetAsync(session, key));
        await Should.ThrowAsync<DatabaseException>(async () => await second.PutAsync(session, key, Bytes("changed")));
        await Should.ThrowAsync<DatabaseException>(async () => await second.TryDeleteAsync(session, key));
        await Should.ThrowAsync<DatabaseException>(async () => await second.ExistsAsync(session, key));
        await Should.ThrowAsync<DatabaseException>(async () =>
        {
            await foreach (var _ in second.ScanAsync(session))
            {
            }
        });
        await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(
            "GET @key FROM second", new Dictionary<string, object?> { ["key"] = key }));

        session.Database.ShouldBeSameAs(first);
        other.Database.ShouldBeSameAs(second);
        Text((await first.GetAsync(session, key))!.Value.Value).ShouldBe("first");
        Text((await second.GetAsync(other, key))!.Value.Value).ShouldBe("second");

        // Typed requests have no database selector: execution remains bound to
        // the receiving session even when another database has the same key.
        var result = (await session.ExecuteAsync(new KeyValueGetRequest(key))).ShouldBeAssignableTo<QueryResultSet>();
        int count = 0;
        await foreach (var row in result.GetRowsAsync())
        {
            Text((byte[])row.GetValue(1)!).ShouldBe("first");
            count++;
        }
        count.ShouldBe(1);
    }

    /// <summary>
    /// Database and server administration never enter the command language.
    /// </summary>
    /// <param name="statement">An attempted server-scoped command.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Scope: Sessions reject server-scoped commands")]
    [InlineData("USE second")]
    [InlineData("CREATE DATABASE third")]
    [InlineData("DROP DATABASE second")]
    [InlineData("ALTER DATABASE second")]
    [InlineData("SHOW DATABASES")]
    [InlineData("SHUTDOWN")]
    public async Task Session_ShouldRejectServerScope(string statement)
    {
        await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "scope" });
        var first = await engine.CreateDatabaseAsync("first");
        var second = await engine.CreateDatabaseAsync("second");
        await using var session = await first.CreateSessionAsync();

        await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(statement));

        session.Database.ShouldBeSameAs(first);
        engine.State.ShouldBe(EngineState.Running);
        engine.TryGetDatabase("second", out var remaining).ShouldBeTrue();
        remaining.ShouldBeSameAs(second);
        engine.TryGetDatabase("third", out _).ShouldBeFalse();
    }
}
