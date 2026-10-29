using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlIdentitySequenceWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Owned identities: ALTER, backfill, overriding and pool reset cross both transports")]
    [InlineData(false)][InlineData(true)]
    public async Task ExecuteAsync_OwnedIdentity_ShouldPreserveWireAndResetSession(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp, configureSettings: settings => settings.MaxPoolSize = 1);
        Guid physical;
        await using (var connection = await harness.Client.ConnectAsync(timeout.Token))
        {
            physical = harness.Server.Sessions.Single().Id;
            await connection.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: timeout.Token);
            await connection.ExecuteAsync("INSERT INTO t VALUES(1),(2)", cancellationToken: timeout.Token);
            // Act
            await connection.ExecuteAsync("ALTER TABLE t ADD id SERIAL", cancellationToken: timeout.Token);
            (await connection.QueryAsync("SELECT currval('t_id_seq'),lastval()", cancellationToken: timeout.Token))[0][0].ShouldBe(2L);
            await connection.ExecuteAsync("ALTER TABLE t ALTER id SET GENERATED ALWAYS", cancellationToken: timeout.Token);
            await connection.ExecuteAsync("INSERT INTO t OVERRIDING SYSTEM VALUE VALUES(3,99)", cancellationToken: timeout.Token);
            await connection.ExecuteAsync("ALTER TABLE t ALTER id RESTART WITH 20", cancellationToken: timeout.Token);
            await connection.ExecuteAsync("INSERT INTO t OVERRIDING USER VALUE VALUES(4,999)", cancellationToken: timeout.Token);
            // Assert
            (await connection.QueryAsync("SELECT id FROM t WHERE k=4", cancellationToken: timeout.Token))[0][0].ShouldBe(20);
            var blocked = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync("DROP SEQUENCE t_id_seq", cancellationToken: timeout.Token));
            blocked.Diagnostics.Single().Code.ShouldBe("CS03080"); blocked.ConnectionUsable.ShouldBeTrue();
            await connection.ExecuteAsync("TRUNCATE t RESTART IDENTITY", cancellationToken: timeout.Token);
            await connection.ExecuteAsync("INSERT INTO t(k) VALUES(5)", cancellationToken: timeout.Token);
            (await connection.QueryAsync("SELECT id FROM t", cancellationToken: timeout.Token))[0][0].ShouldBe(1);
        }
        await using var reused = await harness.Client.ConnectAsync(timeout.Token);
        harness.Server.Sessions.Single().Id.ShouldBe(physical);
        var undefined = await Should.ThrowAsync<SqlClientException>(async () => await reused.QueryAsync("SELECT currval('t_id_seq')", cancellationToken: timeout.Token));
        undefined.Diagnostics.Single().Code.ShouldBe("CS04068");
    }
}
