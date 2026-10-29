using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlSequenceWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Pool reset: sequence state belongs to one logical session")]
    [InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task Connect_PooledSequenceState_ShouldStartUndefined(bool tcp, bool transaction)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp, configureSettings: settings => settings.MaxPoolSize = 1);
        Guid physicalSession;
        await using (var a = await harness.Client.ConnectAsync(timeout.Token))
        {
            physicalSession = harness.Server.Sessions.Single().Id;
            await a.ExecuteAsync("CREATE SEQUENCE s START 47", cancellationToken: timeout.Token);
            if (transaction) { await a.ExecuteAsync("BEGIN", cancellationToken: timeout.Token); }
            (await a.QueryAsync("SELECT nextval('s')", cancellationToken: timeout.Token)).Single()[0].ShouldBe(47L);
            if (!transaction)
            {
                await a.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
                await a.ExecuteAsync("ROLLBACK", cancellationToken: timeout.Token);
                (await a.QueryAsync("SELECT currval('s')", cancellationToken: timeout.Token)).Single()[0].ShouldBe(47L);
            }
        }

        // Act / Assert
        await using var b = await harness.Client.ConnectAsync(timeout.Token);
        harness.Server.Sessions.Single().Id.ShouldBe(physicalSession);
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await b.QueryAsync("SELECT currval('s'), lastval()", cancellationToken: timeout.Token));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04068");
        var last = await Should.ThrowAsync<SqlClientException>(async () => await b.QueryAsync("SELECT lastval()", cancellationToken: timeout.Token));
        last.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04069");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - DISCARD ALL: active transaction refusal preserves state until reset")]
    [InlineData(false)][InlineData(true)]
    public async Task Execute_DiscardAll_ShouldRequireIdleAndClearSequenceState(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        await connection.ExecuteAsync("CREATE SEQUENCE s", cancellationToken: timeout.Token);
        await connection.QueryAsync("SELECT nextval('s')", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync("DISCARD ALL", cancellationToken: timeout.Token));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04070");
        (await connection.QueryAsync("SELECT currval('s')", cancellationToken: timeout.Token)).Single()[0].ShouldBe(1L);
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("DISCARD ALL", cancellationToken: timeout.Token);
        var reset = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync("SELECT currval('s')", cancellationToken: timeout.Token));
        reset.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04068");
    }

    private static object?[] Values(SqlRow row) => Enumerable.Range(0, row.FieldCount).Select(i => row[i]).ToArray();

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Sequences: DDL, functions, state, metadata and EXPLAIN use both transports")]
    [InlineData(false)][InlineData(true)]
    public async Task ExecuteAsync_SequenceSurface_ShouldPreserveSessionAndWireDiagnostics(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var a = await harness.Client.ConnectAsync(timeout.Token);
        await using var b = await harness.Client.ConnectAsync(timeout.Token);
        await a.ExecuteAsync("CREATE SEQUENCE s AS SMALLINT START 10 INCREMENT 2 CACHE 3", cancellationToken: timeout.Token);

        // Act / Assert
        var undefined = await Should.ThrowAsync<SqlClientException>(async () => await a.QueryAsync("SELECT currval('s')", cancellationToken: timeout.Token));
        undefined.Diagnostics.Single().Code.ShouldBe("CS04068");
        undefined.ConnectionUsable.ShouldBeTrue();
        await a.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
        Values((await a.QueryAsync("SELECT NEXT VALUE FOR s, NEXT VALUE FOR s, nextval('s')", cancellationToken: timeout.Token)).Single()).ShouldBe(new object?[] { 10L, 10L, 12L });
        await a.ExecuteAsync("ROLLBACK", cancellationToken: timeout.Token);
        Values((await a.QueryAsync("SELECT currval('s'), lastval()", cancellationToken: timeout.Token)).Single()).ShouldBe(new object?[] { 12L, 12L });
        await Should.ThrowAsync<SqlClientException>(async () => await b.QueryAsync("SELECT lastval()", cancellationToken: timeout.Token));
        Values((await b.QueryAsync("SELECT setval('s', 50, false), nextval('s'::regclass)", cancellationToken: timeout.Token)).Single()).ShouldBe(new object?[] { 50L, 50L });
        Values((await a.QueryAsync("SELECT SEQUENCE_NAME, DATA_TYPE, START_VALUE FROM INFORMATION_SCHEMA.SEQUENCES", cancellationToken: timeout.Token)).Single()).ShouldBe(new object?[] { "s", "SMALLINT", "10" });
        (await a.QueryAsync("EXPLAIN ALTER SEQUENCE s RESTART WITH 8", cancellationToken: timeout.Token)).ShouldNotBeEmpty();
        await a.ExecuteAsync("ALTER SEQUENCE s RESTART WITH 8 NO CYCLE", cancellationToken: timeout.Token);
        (await a.QueryAsync("SELECT nextval('s')", cancellationToken: timeout.Token)).Single()[0].ShouldBe(8L);
        await a.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
        var ddl = await Should.ThrowAsync<SqlClientException>(async () => await a.ExecuteAsync("DROP SEQUENCE s", cancellationToken: timeout.Token));
        ddl.Diagnostics.Single().Code.ShouldBe("CS04003");
        ddl.ConnectionUsable.ShouldBeTrue();
        await a.ExecuteAsync("ROLLBACK", cancellationToken: timeout.Token);
        await a.ExecuteAsync("DROP SEQUENCE s RESTRICT", cancellationToken: timeout.Token);
        (await a.QueryAsync("SELECT * FROM INFORMATION_SCHEMA.SEQUENCES", cancellationToken: timeout.Token)).ShouldBeEmpty();
    }
}
