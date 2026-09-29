using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Guards the database-only execution boundary shared by every model (A5).
/// </summary>
public sealed class SqlDatabaseScopeTests
{
    /// <summary>
    /// A session resolves identically named objects only in its bound database.
    /// </summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Scope: A session cannot address another database")]
    public async Task Session_ShouldRemainBoundToOneDatabase()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "scope" });
        var first = await engine.CreateDatabaseAsync("first");
        var second = await engine.CreateDatabaseAsync("second");
        await using var session = await first.CreateSessionAsync();
        await using var other = await second.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE marker (value INT NOT NULL)");
        await other.ExecuteAsync("CREATE TABLE marker (value INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO marker (value) VALUES (11)");
        await other.ExecuteAsync("INSERT INTO marker (value) VALUES (22)");

        // Qualification resolves in the local catalog and never selects a
        // database. Exercise both the text and typed seams.
        await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT value FROM second.marker"));
        await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(SqlQueryRequest.FromSql("SELECT value FROM second.marker")));
        await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT value FROM second.dbo.marker"));
        await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("UPDATE second.marker SET value = 99"));
        await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("DROP TABLE second.marker"));

        session.Database.ShouldBeSameAs(first);
        other.Database.ShouldBeSameAs(second);
        (await ReadValuesAsync(session)).ShouldBe(new[] { 11 });
        (await ReadValuesAsync(other)).ShouldBe(new[] { 22 });
    }

    /// <summary>
    /// Database and server administration never enter the session language.
    /// </summary>
    /// <param name="statement">An attempted server-scoped statement.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Scope: Sessions reject server-scoped statements")]
    [InlineData("USE second")]
    [InlineData("CREATE DATABASE third")]
    [InlineData("DROP DATABASE second")]
    [InlineData("ALTER DATABASE second")]
    [InlineData("SHOW DATABASES")]
    [InlineData("SHUTDOWN")]
    public async Task Session_ShouldRejectServerScope(string statement)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "scope" });
        var first = await engine.CreateDatabaseAsync("first");
        var second = await engine.CreateDatabaseAsync("second");
        await using var session = await first.CreateSessionAsync();

        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(statement));

        session.Database.ShouldBeSameAs(first);
        engine.State.ShouldBe(EngineState.Running);
        engine.TryGetDatabase("second", out var remaining).ShouldBeTrue();
        remaining.ShouldBeSameAs(second);
        engine.TryGetDatabase("third", out _).ShouldBeFalse();
    }

    private static async Task<List<int>> ReadValuesAsync(IDatabaseSession session)
    {
        var result = (await session.ExecuteAsync("SELECT value FROM marker")).ShouldBeAssignableTo<QueryResultSet>();
        var values = new List<int>();
        await foreach (var row in result.GetRowsAsync())
        {
            values.Add((int)row.GetValue(0)!);
        }
        return values;
    }
}
