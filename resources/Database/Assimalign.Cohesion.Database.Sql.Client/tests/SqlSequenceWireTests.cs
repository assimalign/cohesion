using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlSequenceWireTests
{
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
