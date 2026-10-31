using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class NamedSavepointWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Savepoints: nested scopes, commit, peer visibility and exact pooled-session reset on both transports (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_NestedSavepoints_ShouldCommitPrefixAndResetPooledState(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        var token = timeout.Token;
        await using var writer = await harness.Client.ConnectAsync(token);
        await writer.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: token);
        await using var reader = await harness.Client.ConnectAsync(token);

        // Act / Assert
        foreach (string statement in new[] { "BEGIN", "INSERT INTO items VALUES (1)", "SAVEPOINT root", "INSERT INTO items VALUES (2)", "SAVEPOINT duplicate", "INSERT INTO items VALUES (3)", "SAVEPOINT duplicate", "INSERT INTO items VALUES (4)", "ROLLBACK TO duplicate" })
        { await writer.ExecuteAsync(statement, cancellationToken: token); }
        (await writer.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
        await writer.ExecuteAsync("INSERT INTO items VALUES (5)", cancellationToken: token);
        await writer.ExecuteAsync("ROLLBACK TO SAVEPOINT duplicate", cancellationToken: token);
        (await writer.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
        await writer.ExecuteAsync("RELEASE duplicate", cancellationToken: token);
        await writer.ExecuteAsync("ROLLBACK TO duplicate", cancellationToken: token);
        (await writer.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
        await writer.ExecuteAsync("SAVEPOINT child", cancellationToken: token);
        await writer.ExecuteAsync("INSERT INTO items VALUES (6)", cancellationToken: token);
        await writer.ExecuteAsync("RELEASE SAVEPOINT root", cancellationToken: token);
        (await reader.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        await writer.ExecuteAsync("COMMIT", cancellationToken: token);
        (await reader.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT pooled", cancellationToken: token);
        await writer.ExecuteAsync("INSERT INTO items VALUES (99)", cancellationToken: token);
        var session = harness.Server.Sessions.Single(item => item.DatabaseSession!.CurrentTransaction is not null);
        await writer.DisposeAsync();
        session.DatabaseSession!.CurrentTransaction.ShouldBeNull();
        await using var next = await harness.Client.ConnectAsync(token);
        await next.ExecuteAsync("BEGIN", cancellationToken: token);
        harness.Server.Sessions.Single(item => item.DatabaseSession!.CurrentTransaction is not null).Id.ShouldBe(session.Id);
        var unknown = await Should.ThrowAsync<SqlClientException>(async () => await next.ExecuteAsync("ROLLBACK TO pooled", cancellationToken: token));
        unknown.Diagnostics.ShouldContain(item => item.Code == "CK04013");
        await next.ExecuteAsync("ROLLBACK", cancellationToken: token);
        (await next.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Savepoints: outside-block refusals carry CK04012 on both transports (#1726)")]
    [InlineData(false, "SAVEPOINT outside")]
    [InlineData(true, "SAVEPOINT outside")]
    [InlineData(false, "RELEASE SAVEPOINT outside")]
    [InlineData(true, "RELEASE SAVEPOINT outside")]
    [InlineData(false, "ROLLBACK TO outside")]
    [InlineData(true, "ROLLBACK TO outside")]
    public async Task ExecuteAsync_OutsideBlock_ShouldCarrySharedCode(bool tcp, string source)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        var token = timeout.Token;
        await using var writer = await harness.Client.ConnectAsync(token);

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await writer.ExecuteAsync(source, cancellationToken: token));

        // Assert
        error.Diagnostics.ShouldContain(item => item.Code == "CK04012");
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("ROLLBACK", cancellationToken: token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Savepoints: statement failures keep the model recovery rule on both transports (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_FailedStatement_ShouldPreserveModelRecovery(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        var token = timeout.Token;
        await using var writer = await harness.Client.ConnectAsync(token);
        await writer.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: token);
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT keep", cancellationToken: token);
        await writer.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: token);

        // Act
        await Should.ThrowAsync<SqlClientException>(async () => await writer.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: token));

        // Assert
        await writer.ExecuteAsync("ROLLBACK TO keep", cancellationToken: token);
        await writer.ExecuteAsync("INSERT INTO items VALUES (2)", cancellationToken: token);
        await writer.ExecuteAsync("COMMIT", cancellationToken: token);
        (await writer.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }
}
