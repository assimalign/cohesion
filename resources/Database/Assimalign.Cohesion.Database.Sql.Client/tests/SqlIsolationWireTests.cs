using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlIsolationWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Isolation: text BEGIN and SET control concurrent visibility on both transports")]
    [InlineData(false, "READ COMMITTED", true)]
    [InlineData(true, "READ COMMITTED", true)]
    [InlineData(false, "READ UNCOMMITTED", true)]
    [InlineData(true, "READ UNCOMMITTED", true)]
    [InlineData(false, "REPEATABLE READ", false)]
    [InlineData(true, "REPEATABLE READ", false)]
    public async Task QueryAsync_IsolationLevel_ShouldControlVisibility(bool tcp, string level, bool seesCommit)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var a = await harness.Client.ConnectAsync(ceiling.Token);
        await using var b = await harness.Client.ConnectAsync(ceiling.Token);
        int next = 3;

        // Act / Assert
        foreach (string begin in new[] { "BEGIN", "BEGIN TRANSACTION", "BEGIN WORK", "START TRANSACTION", "SET TRANSACTION" })
        {
            if (begin == "SET TRANSACTION") { await a.ExecuteAsync("BEGIN", cancellationToken: ceiling.Token); }
            await a.ExecuteAsync(begin + " ISOLATION LEVEL " + level, cancellationToken: ceiling.Token);
            int before = (await a.QueryAsync("SELECT id FROM users", cancellationToken: ceiling.Token)).Count;
            await b.ExecuteAsync($"INSERT INTO users(id) VALUES({next++})", cancellationToken: ceiling.Token);
            (await a.QueryAsync("SELECT id FROM users", cancellationToken: ceiling.Token)).Count.ShouldBe(before + (seesCommit ? 1 : 0));
            var late = await Should.ThrowAsync<SqlClientException>(async () =>
                await a.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL " + level, cancellationToken: ceiling.Token));
            late.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04072");
            late.ConnectionUsable.ShouldBeTrue();
            await a.ExecuteAsync("COMMIT", cancellationToken: ceiling.Token);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Isolation: refusal codes preserve Ready state and repeated early configuration")]
    [InlineData(false)][InlineData(true)]
    public async Task ExecuteAsync_RefusedIsolation_ShouldKeepConnectionAndTransactionUsable(bool tcp)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(ceiling.Token);

        // Act / Assert
        var outside = await connection.QueryAsync("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", cancellationToken: ceiling.Token);
        outside.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04071");
        outside.Diagnostics[0].Severity.ShouldBe(ProtocolDiagnosticSeverity.Warning);
        var begin = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync("BEGIN ISOLATION LEVEL SERIALIZABLE", cancellationToken: ceiling.Token));
        begin.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04073");
        await connection.ExecuteAsync("BEGIN ISOLATION LEVEL READ COMMITTED", cancellationToken: ceiling.Token);
        foreach (string mode in new[] { "ISOLATION LEVEL SERIALIZABLE", "READ ONLY", "READ WRITE", "DEFERRABLE", "NOT DEFERRABLE" })
        {
            var error = await Should.ThrowAsync<SqlClientException>(async () =>
                await connection.ExecuteAsync("SET TRANSACTION " + mode, cancellationToken: ceiling.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(mode.StartsWith("ISOLATION", StringComparison.Ordinal) ? "CS04073" : "CK00001");
            error.ConnectionUsable.ShouldBeTrue();
        }
        await connection.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, ISOLATION LEVEL READ COMMITTED", cancellationToken: ceiling.Token);
        await connection.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ", cancellationToken: ceiling.Token);
        await connection.ExecuteAsync("INSERT INTO users(id) VALUES(3)", cancellationToken: ceiling.Token);
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: ceiling.Token);
        (await connection.QueryAsync("SELECT id FROM users", cancellationToken: ceiling.Token)).Count.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Isolation: pooled return and DISCARD restore the Snapshot default")]
    [InlineData(false, false)][InlineData(true, false)]
    [InlineData(false, true)][InlineData(true, true)]
    public async Task ConnectAsync_PoolAndDiscard_ShouldRestoreDefaultLevel(bool tcp, bool discard)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp,
            configureSettings: settings => settings.MaxPoolSize = 1);
        Guid physical;
        await using (var first = await harness.Client.ConnectAsync(ceiling.Token))
        {
            physical = harness.Server.Sessions.Single().Id;
            await first.ExecuteAsync("BEGIN ISOLATION LEVEL READ COMMITTED", cancellationToken: ceiling.Token);
            await first.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED", cancellationToken: ceiling.Token);
            if (discard)
            {
                await first.ExecuteAsync("ROLLBACK", cancellationToken: ceiling.Token);
                await first.ExecuteAsync("DISCARD ALL", cancellationToken: ceiling.Token);
            }
        }

        // Act: the same physical session's optionless BEGIN must keep its begin snapshot.
        await using var reused = await harness.Client.ConnectAsync(ceiling.Token);
        harness.Server.Sessions.Single().Id.ShouldBe(physical);
        await reused.ExecuteAsync("BEGIN", cancellationToken: ceiling.Token);
        (await reused.QueryAsync("SELECT id FROM users", cancellationToken: ceiling.Token)).Count.ShouldBe(2);
        var database = await harness.Engine.OpenDatabaseAsync(SqlClientTestHarness.DatabaseName, ceiling.Token);
        await using var writer = await database.CreateSessionAsync(ceiling.Token);
        await writer.ExecuteAsync("INSERT INTO users(id) VALUES(3)", cancellationToken: ceiling.Token);

        // Assert
        (await reused.QueryAsync("SELECT id FROM users", cancellationToken: ceiling.Token)).Count.ShouldBe(2);
        await reused.ExecuteAsync("COMMIT", cancellationToken: ceiling.Token);
        (await reused.QueryAsync("SELECT id FROM users", cancellationToken: ceiling.Token)).Count.ShouldBe(3);
    }
}
