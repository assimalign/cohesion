using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlCreateTableAsWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - CTAS: disposing a running server session cancels its build and releases admission on both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DisposedServerSession_ShouldAbandonBuildAndAllowRetry(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var functions = new CancellationFunctions();
        var builder = SqlDatabaseEngine.CreateBuilder("ctas-wire-cancel");
        functions.Register(builder);
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using (var session = await database.CreateSessionAsync(timeout.Token))
        {
            await session.ExecuteAsync("CREATE TABLE src (id INT)", cancellationToken: timeout.Token);
            await session.ExecuteAsync("INSERT INTO src VALUES(1)", cancellationToken: timeout.Token);
        }
        await using IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync(timeout.Token);
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "db", Principal = "tester", EndPoint = listener.EndPoint },
            ConnectionFactory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(timeout.Token);
        var serverSession = server.Sessions.ShouldHaveSingleItem();
        var closing = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        functions.BeforeThrow = _ => closing.TrySetResult(serverSession.DisposeAsync().AsTask());

        // Act: disposal cancels the running statement before the application throws.
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync("CREATE TABLE copy AS SELECT cancel_scalar1(id) AS n FROM src", cancellationToken: timeout.Token));
        await (await closing.Task.WaitAsync(timeout.Token)).WaitAsync(timeout.Token);

        // Assert
        failure.ConnectionUsable.ShouldBeFalse();
        functions.Calls.ShouldBe(1);
        functions.LastToken.IsCancellationRequested.ShouldBeTrue();
        await using var retry = await client.ConnectAsync(timeout.Token);
        (await retry.QueryAsync("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='copy'", cancellationToken: timeout.Token)).ShouldBeEmpty();
        await retry.ExecuteAsync("CREATE TABLE copy AS SELECT id FROM src", cancellationToken: timeout.Token);
        (await retry.QueryAsync("SELECT id FROM copy", cancellationToken: timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - CTAS: inferred rows, renaming, no-data and failures preserve both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_TableAsQuery_ShouldPublishCompleteRowsAndRetainConnection(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        await connection.ExecuteAsync("CREATE TABLE src (id INT NOT NULL, label VARCHAR(12), amount DECIMAL(9,2))", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("INSERT INTO src VALUES(1,'one',1.25),(2,NULL,2.5)", cancellationToken: timeout.Token);

        // Act
        (await connection.ExecuteAsync("CREATE TABLE copy (key,word,value) AS WITH q AS (SELECT * FROM src) SELECT * FROM q WITH DATA", cancellationToken: timeout.Token)).ShouldBe(0);
        await connection.ExecuteAsync("CREATE TABLE empty AS SELECT CAST('bad' AS INT) AS n WITH NO DATA", cancellationToken: timeout.Token);

        // Assert
        var copy = await connection.QueryAsync("SELECT * FROM copy ORDER BY key", cancellationToken: timeout.Token);
        copy.Count.ShouldBe(2);
        copy[0][0].ShouldBe(1);
        copy[0][1].ShouldBe("one");
        copy[0][2].ShouldBe(1.25m);
        (await connection.QueryAsync("SELECT * FROM empty", cancellationToken: timeout.Token)).ShouldBeEmpty();
        await connection.ExecuteAsync("CREATE TABLE IF NOT EXISTS copy AS SELECT missing FROM absent", cancellationToken: timeout.Token);
        var conversion = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync("CREATE TABLE bad AS SELECT CAST('bad' AS INT) AS n FROM src", cancellationToken: timeout.Token));
        conversion.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='bad'", cancellationToken: timeout.Token)).ShouldBeEmpty();
        var names = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync("CREATE TABLE bad AS SELECT id,id FROM src", cancellationToken: timeout.Token));
        names.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03068");
        names.ConnectionUsable.ShouldBeTrue();
        await connection.ExecuteAsync("CREATE TABLE bad AS SELECT id FROM src", cancellationToken: timeout.Token);
        (await connection.QueryAsync("SELECT * FROM bad", cancellationToken: timeout.Token)).Count.ShouldBe(2);
        await connection.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("INSERT INTO src VALUES(3,'three',3)", cancellationToken: timeout.Token);
        var transaction = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync("CREATE TABLE refused AS SELECT * FROM src", cancellationToken: timeout.Token));
        transaction.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04003");
        transaction.ConnectionUsable.ShouldBeTrue();
        await connection.ExecuteAsync("COMMIT", cancellationToken: timeout.Token);
        (await connection.QueryAsync("SELECT COUNT(*) FROM src", cancellationToken: timeout.Token)).Single()[0].ShouldBe(3L);
    }
}
