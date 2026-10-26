using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphTransactionTextWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Another wire session cannot see writes until the open transaction commits (w11 review)")]
    [InlineData(false, "START TRANSACTION")]
    [InlineData(true, "START TRANSACTION")]
    [InlineData(false, "BEGIN")]
    [InlineData(true, "BEGIN")]
    public async Task ExecuteAsync_OpenWireTransaction_ShouldHideWritesFromAnotherSession(bool tcp, string begin)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var peer = GraphClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings
            { Database = "graph", Principal = "peer", EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = harness.ConnectionFactory,
        });
        await using var writer = await harness.Client.ConnectAsync(harness.Token);
        await using var reader = await peer.ConnectAsync(harness.Token);

        // Act / Assert
        await writer.ExecuteAsync(begin, cancellationToken: harness.Token);
        await writer.ExecuteAsync("INSERT (:Pending {k:1})", cancellationToken: harness.Token);
        (await writer.QueryAsync("MATCH (n:Pending) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await reader.QueryAsync("MATCH (n:Pending) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        await writer.ExecuteAsync("COMMIT", cancellationToken: harness.Token);
        (await reader.QueryAsync("MATCH (n:Pending) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Duplicate START aborts the existing transaction (#1540)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DuplicateStart_ShouldAbortExistingTransaction(bool tcp)
    {
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("START TRANSACTION", cancellationToken: harness.Token);
        await connection.ExecuteAsync("INSERT (:Pending)", cancellationToken: harness.Token);
        var duplicate = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token));
        duplicate.Diagnostics.ShouldContain(item => item.Code == "CG04022");
        var refused = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.ExecuteAsync("INSERT (:Escaped)", cancellationToken: harness.Token));
        refused.Diagnostics.ShouldContain(item => item.Code == "CG04007");
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token);
        (await connection.QueryAsync("MATCH (n) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - ExecutePaths refuses transaction controls before changing state (#1540)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPathsAsync_TransactionControl_ShouldRefuseBeforeStart(bool tcp)
    {
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
        {
            await foreach (var _ in connection.QueryPathsAsync("START TRANSACTION", cancellationToken: harness.Token)) { }
        });
        error.Diagnostics.ShouldContain(item => item.Code == "CG05001");
        harness.Server.Sessions.ShouldHaveSingleItem().DatabaseSession.ShouldNotBeNull().CurrentTransaction.ShouldBeNull();
        await connection.ExecuteAsync("START TRANSACTION", cancellationToken: harness.Token);
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - A failed wire transaction refuses work until rollback and returns cleanly (#1540)")]
    [InlineData(false, "MATCH (", true)]
    [InlineData(true, "MATCH (", true)]
    [InlineData(false, "RETURN missing", true)]
    [InlineData(true, "RETURN missing", false)]
    public async Task ExecuteAsync_FailedTransaction_ShouldRefuseUntilRollback(bool tcp, string failure, bool explicitRollback)
    {
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var first = await harness.Client.ConnectAsync(harness.Token);
        await first.ExecuteAsync("START TRANSACTION", cancellationToken: harness.Token);
        await first.ExecuteAsync("INSERT (:Pending {k:1})", cancellationToken: harness.Token);
        await Should.ThrowAsync<GraphClientException>(async () =>
            await first.QueryAsync(failure, cancellationToken: harness.Token));
        var refused = await Should.ThrowAsync<GraphClientException>(async () =>
            await first.ExecuteAsync("INSERT (:Escaped)", cancellationToken: harness.Token));
        refused.Diagnostics.ShouldContain(item => item.Code == "CG04007");
        var session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        if (explicitRollback) { await first.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token); }
        await first.DisposeAsync();
        await using var next = await harness.Client.ConnectAsync(harness.Token);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
        (await next.QueryAsync("MATCH (n) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - A failed COMMIT cannot silently autocommit pending graph writes (#1540)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_FailedCommit_ShouldRollbackPendingWrites(bool tcp)
    {
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("START TRANSACTION", cancellationToken: harness.Token);
        await connection.ExecuteAsync("INSERT (:Pending)", cancellationToken: harness.Token);
        await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("RETURN missing", cancellationToken: harness.Token));
        await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.ExecuteAsync("COMMIT", cancellationToken: harness.Token));
        (await connection.QueryAsync("MATCH (n) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Transaction text retains one snapshot across another client's write (#1540)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ExplicitTransaction_ShouldKeepWireSnapshot(bool tcp)
    {
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var peer = GraphClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings
            { Database = "graph", Principal = "peer", EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = harness.ConnectionFactory,
        });
        await using var first = await harness.Client.ConnectAsync(harness.Token);
        await using var writer = await peer.ConnectAsync(harness.Token);
        await first.ExecuteAsync("INSERT (:Person {id:1})", cancellationToken: harness.Token);
        await first.ExecuteAsync("START TRANSACTION", cancellationToken: harness.Token);
        (await first.QueryAsync("MATCH (n:Person) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        await writer.ExecuteAsync("INSERT (:Person {id:2})", cancellationToken: harness.Token);
        (await first.QueryAsync("MATCH (n:Person) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        await first.ExecuteAsync("COMMIT", cancellationToken: harness.Token);
        (await first.QueryAsync("MATCH (n:Person) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Returning an open transaction rolls it back on the exact pooled server session (#1540)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_OpenTransaction_ShouldRollbackBeforeReuse(bool tcp)
    {
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var first = await harness.Client.ConnectAsync(harness.Token);
        await first.ExecuteAsync("START TRANSACTION", cancellationToken: harness.Token);
        await first.ExecuteAsync("INSERT (:Pending {k:1})", cancellationToken: harness.Token);
        var session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        await first.DisposeAsync();
        await using var next = await harness.Client.ConnectAsync(harness.Token);
        (await next.QueryAsync("MATCH (n:Pending) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
        await next.ExecuteAsync("START TRANSACTION", cancellationToken: harness.Token);
        await next.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token);
    }
}
