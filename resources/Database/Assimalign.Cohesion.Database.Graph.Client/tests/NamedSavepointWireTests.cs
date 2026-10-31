using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class NamedSavepointWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Savepoints: nested scopes, commit, peer visibility and exact pooled-session reset on both transports (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_NestedSavepoints_ShouldCommitPrefixAndResetPooledState(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await GraphClientTestHarness.StartAsync(tcp: tcp);
        var token = harness.Token;
        await using var writer = await harness.Client.ConnectAsync(token);
        await using var peer = GraphClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "graph", Principal = "peer", EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = harness.ConnectionFactory,
        });
        await using var reader = await peer.ConnectAsync(token);

        // Act / Assert
        foreach (string statement in new[] { "BEGIN", "INSERT (:Item {n: 1})", "SAVEPOINT root", "INSERT (:Item {n: 2})", "SAVEPOINT duplicate", "INSERT (:Item {n: 3})", "SAVEPOINT duplicate", "INSERT (:Item {n: 4})", "ROLLBACK TO duplicate" })
        { await writer.ExecuteAsync(statement, cancellationToken: token); }
        (await writer.QueryAsync("MATCH (n:Item) RETURN count(*)", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
        await writer.ExecuteAsync("INSERT (:Item {n: 5})", cancellationToken: token);
        await writer.ExecuteAsync("ROLLBACK TO SAVEPOINT duplicate", cancellationToken: token);
        (await writer.QueryAsync("MATCH (n:Item) RETURN count(*)", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
        await writer.ExecuteAsync("RELEASE duplicate", cancellationToken: token);
        await writer.ExecuteAsync("ROLLBACK TO duplicate", cancellationToken: token);
        (await writer.QueryAsync("MATCH (n:Item) RETURN count(*)", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
        await writer.ExecuteAsync("SAVEPOINT child", cancellationToken: token);
        await writer.ExecuteAsync("INSERT (:Item {n: 6})", cancellationToken: token);
        await writer.ExecuteAsync("RELEASE SAVEPOINT root", cancellationToken: token);
        (await reader.QueryAsync("MATCH (n:Item) RETURN count(*)", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        await writer.ExecuteAsync("COMMIT", cancellationToken: token);
        (await reader.QueryAsync("MATCH (n:Item) RETURN count(*)", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT pooled", cancellationToken: token);
        await writer.ExecuteAsync("INSERT (:Item {n: 99})", cancellationToken: token);
        var session = harness.Server.Sessions.Single(item => item.DatabaseSession!.CurrentTransaction is not null);
        await writer.DisposeAsync();
        session.DatabaseSession!.CurrentTransaction.ShouldBeNull();
        await using var next = await harness.Client.ConnectAsync(token);
        await next.ExecuteAsync("BEGIN", cancellationToken: token);
        harness.Server.Sessions.Single(item => item.DatabaseSession!.CurrentTransaction is not null).Id.ShouldBe(session.Id);
        var unknown = await Should.ThrowAsync<GraphClientException>(async () => await next.ExecuteAsync("ROLLBACK TO pooled", cancellationToken: token));
        unknown.Diagnostics.ShouldContain(item => item.Code == "CK04013");
        await next.ExecuteAsync("ROLLBACK", cancellationToken: token);
        (await next.QueryAsync("MATCH (n:Item) RETURN count(*)", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Savepoints: outside-block refusals carry CK04012 on both transports (#1726)")]
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
        await using var harness = await GraphClientTestHarness.StartAsync(tcp: tcp);
        var token = harness.Token;
        await using var writer = await harness.Client.ConnectAsync(token);

        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () => await writer.ExecuteAsync(source, cancellationToken: token));

        // Assert
        error.Diagnostics.ShouldContain(item => item.Code == "CK04012");
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("ROLLBACK", cancellationToken: token);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Savepoints: statement failures keep the model recovery rule on both transports (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_FailedStatement_ShouldPreserveModelRecovery(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await GraphClientTestHarness.StartAsync(tcp: tcp);
        var token = harness.Token;
        await using var writer = await harness.Client.ConnectAsync(token);
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT keep", cancellationToken: token);
        await writer.ExecuteAsync("INSERT (:Item {n: 1})", cancellationToken: token);

        // Act
        await Should.ThrowAsync<GraphClientException>(async () => await writer.ExecuteAsync("RETURN missing", cancellationToken: token));

        // Assert
        var refused = await Should.ThrowAsync<GraphClientException>(async () => await writer.ExecuteAsync("ROLLBACK TO keep", cancellationToken: token));
        refused.Diagnostics.ShouldContain(item => item.Code == "CG04007");
        await writer.ExecuteAsync("ROLLBACK", cancellationToken: token);
        (await writer.QueryAsync("MATCH (n:Item) RETURN count(*)", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }
}
