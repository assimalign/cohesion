using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

[Collection(nameof(DocumentClientEventSourceCollection))]
public sealed class NamedSavepointWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Savepoints: nested scopes, commit, peer visibility and exact pooled-session reset on both transports (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_NestedSavepoints_ShouldCommitPrefixAndResetPooledState(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp: tcp);
        var token = harness.Token;
        await using var writer = await harness.Client.ConnectAsync(token);
        await writer.ExecuteAsync("CREATE COLLECTION items", cancellationToken: token);
        await using var peer = DocumentClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "documents", Principal = "peer", EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = harness.Factory,
        });
        await using var reader = await peer.ConnectAsync(token);

        // Act / Assert
        foreach (string statement in new[] { "BEGIN", "INSERT INTO items (KEY, VALUE) VALUES ('k1', {'n': 1})", "SAVEPOINT root", "INSERT INTO items (KEY, VALUE) VALUES ('k2', {'n': 2})", "SAVEPOINT duplicate", "INSERT INTO items (KEY, VALUE) VALUES ('k3', {'n': 3})", "SAVEPOINT duplicate", "INSERT INTO items (KEY, VALUE) VALUES ('k4', {'n': 4})", "ROLLBACK TO duplicate" })
        { await writer.ExecuteAsync(statement, cancellationToken: token); }
        (await writer.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
        await writer.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('k5', {'n': 5})", cancellationToken: token);
        await writer.ExecuteAsync("ROLLBACK TO SAVEPOINT duplicate", cancellationToken: token);
        (await writer.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
        await writer.ExecuteAsync("RELEASE duplicate", cancellationToken: token);
        await writer.ExecuteAsync("ROLLBACK TO duplicate", cancellationToken: token);
        (await writer.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
        await writer.ExecuteAsync("SAVEPOINT child", cancellationToken: token);
        await writer.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('k6', {'n': 6})", cancellationToken: token);
        await writer.ExecuteAsync("RELEASE SAVEPOINT root", cancellationToken: token);
        (await reader.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        await writer.ExecuteAsync("COMMIT", cancellationToken: token);
        (await reader.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT pooled", cancellationToken: token);
        await writer.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('k99', {'n': 99})", cancellationToken: token);
        var session = harness.Server.Sessions.Single(item => item.DatabaseSession!.CurrentTransaction is not null);
        await writer.DisposeAsync();
        session.DatabaseSession!.CurrentTransaction.ShouldBeNull();
        await using var next = await harness.Client.ConnectAsync(token);
        await next.ExecuteAsync("BEGIN", cancellationToken: token);
        harness.Server.Sessions.Single(item => item.DatabaseSession!.CurrentTransaction is not null).Id.ShouldBe(session.Id);
        var unknown = await Should.ThrowAsync<DocumentClientException>(async () => await next.ExecuteAsync("ROLLBACK TO pooled", cancellationToken: token));
        unknown.Diagnostics.ShouldContain(item => item.Code == "CK04013");
        await next.ExecuteAsync("ROLLBACK", cancellationToken: token);
        (await next.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Savepoints: outside-block refusals carry CK04012 on both transports (#1726)")]
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
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp: tcp);
        var token = harness.Token;
        await using var writer = await harness.Client.ConnectAsync(token);

        // Act
        var error = await Should.ThrowAsync<DocumentClientException>(async () => await writer.ExecuteAsync(source, cancellationToken: token));

        // Assert
        error.Diagnostics.ShouldContain(item => item.Code == "CK04012");
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("ROLLBACK", cancellationToken: token);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Savepoints: statement failures keep the model recovery rule on both transports (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_FailedStatement_ShouldPreserveModelRecovery(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp: tcp);
        var token = harness.Token;
        await using var writer = await harness.Client.ConnectAsync(token);
        await writer.ExecuteAsync("CREATE COLLECTION items", cancellationToken: token);
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT keep", cancellationToken: token);
        await writer.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('k1', {'n': 1})", cancellationToken: token);

        // Act
        await Should.ThrowAsync<DocumentClientException>(async () => await writer.ExecuteAsync("SELECT * FROM absent", cancellationToken: token));

        // Assert
        var refused = await Should.ThrowAsync<DocumentClientException>(async () => await writer.ExecuteAsync("ROLLBACK TO keep", cancellationToken: token));
        refused.Diagnostics.ShouldContain(item => item.Code == "CD04101");
        await writer.ExecuteAsync("ROLLBACK", cancellationToken: token);
        (await writer.QueryAsync("SELECT count(*) FROM items", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }
}
