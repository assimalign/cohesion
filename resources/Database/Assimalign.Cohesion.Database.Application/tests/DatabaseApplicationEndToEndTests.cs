using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Application.Internal;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Application.Tests;

/// <summary>
/// The definitive SQL-over-the-host end-to-end (#904): the full stack composed the
/// way the executable composes it — file-backed SQL engine, real TCP loopback
/// listener, wire-protocol server, hosting application with the engine-worker
/// slots — driven with the typed SQL client, including restart recovery, the
/// grouped durability mode, and checkpoints under load.
/// </summary>
public sealed class DatabaseApplicationEndToEndTests : IDisposable
{
    private readonly string _rootPath;

    public DatabaseApplicationEndToEndTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-db-e2e", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup
            }
        }
    }

    private static CancellationToken TestTimeout(int seconds = 30)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    /// <summary>
    /// The TCP listener binds lazily on the accept loop's first accept; poll until
    /// the OS-assigned port is observable.
    /// </summary>
    private static async Task<int> WaitForBoundPortAsync(TcpConnectionListener listener)
    {
        long deadline = Environment.TickCount64 + 15_000;

        while (Environment.TickCount64 < deadline)
        {
            if (listener.EndPoint is IPEndPoint { Port: > 0 } endpoint)
            {
                return endpoint.Port;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The TCP listener did not bind within the budget.");
    }

    private static ISqlClient CreateClient(int port, string database)
        => SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = database,
                Principal = "e2e",
                EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            },
            ConnectionFactory = new TcpConnectionFactory(),
        });

    [Fact(DisplayName = "Cohesion Test [Database.Application] - E2E: SQL over TCP round-trips, explicit transactions resolve, and data survives a restart")]
    public async Task EndToEnd_SqlOverTcpWithRestart_ShouldServeAndRecover()
    {
        // ---- First composition: serve DDL/DML over real TCP loopback. ----
        var configuration = new DatabaseHostConfiguration { DataPath = _rootPath, Port = 0 };

        await using (var composition = DatabaseApplicationBootstrap.Compose(configuration))
        {
            await ((IHost)composition.Application).StartAsync(TestTimeout());
            int port = await WaitForBoundPortAsync(composition.Listener);

            await using (var client = CreateClient(port, DatabaseApplicationBootstrap.DefaultDatabaseName))
            await using (var connection = await client.ConnectAsync(TestTimeout()))
            {
                // DDL + INSERT (parameterized and literal) + parameterized SELECT.
                await connection.ExecuteAsync("CREATE TABLE users (id INT NOT NULL, name VARCHAR(100))", cancellationToken: TestTimeout());

                long affected = await connection.ExecuteAsync(
                    "INSERT INTO users (id, name) VALUES (@id, @name)",
                    new Dictionary<string, object?> { ["id"] = 1, ["name"] = "ada" },
                    TestTimeout());
                affected.ShouldBe(1);

                await connection.ExecuteAsync("INSERT INTO users (id, name) VALUES (2, 'grace')", cancellationToken: TestTimeout());

                SqlResultSet selected = await connection.QueryAsync(
                    "SELECT id, name FROM users WHERE id = @id",
                    new Dictionary<string, object?> { ["id"] = 2 },
                    TestTimeout());

                selected.ShouldHaveSingleItem();
                selected[0].GetString("name").ShouldBe("grace");
            }

            // Explicit transaction commit + rollback through the composed engine's
            // session (explicit transaction control over the wire lands with the
            // protocol's Transaction payload schema — a documented deferral).
            composition.Engine.TryGetDatabase(DatabaseApplicationBootstrap.DefaultDatabaseName, out IDatabase database).ShouldBeTrue();

            await using (var session = await database.CreateSessionAsync(TestTimeout()))
            {
                var committed = await session.BeginTransactionAsync(TestTimeout());
                await session.ExecuteAsync("INSERT INTO users (id, name) VALUES (3, 'committed')", cancellationToken: TestTimeout());
                await committed.CommitAsync(TestTimeout());

                var discarded = await session.BeginTransactionAsync(TestTimeout());
                await session.ExecuteAsync("INSERT INTO users (id, name) VALUES (4, 'discarded')", cancellationToken: TestTimeout());
                await discarded.RollbackAsync(TestTimeout());
            }

            // The committed row is visible over the wire; the rolled-back row is not.
            await using (var client = CreateClient(port, DatabaseApplicationBootstrap.DefaultDatabaseName))
            await using (var connection = await client.ConnectAsync(TestTimeout()))
            {
                SqlResultSet rows = await connection.QueryAsync("SELECT id FROM users ORDER BY id", cancellationToken: TestTimeout());
                rows.Count.ShouldBe(3);
                rows[2].GetInt32("id").ShouldBe(3);
            }

            // Graceful stop: endpoint drains, workers quiesce, engines flush durably.
            await ((IHost)composition.Application).StopAsync(TestTimeout());
            composition.Application.Context.State.ShouldBe(HostState.Stopped);
        }

        // ---- Second composition over the same data directory: recovery. ----
        await using (var reopened = DatabaseApplicationBootstrap.Compose(new DatabaseHostConfiguration { DataPath = _rootPath, Port = 0 }))
        {
            await ((IHost)reopened.Application).StartAsync(TestTimeout());
            int port = await WaitForBoundPortAsync(reopened.Listener);

            await using var client = CreateClient(port, DatabaseApplicationBootstrap.DefaultDatabaseName);
            await using var connection = await client.ConnectAsync(TestTimeout());

            SqlResultSet rows = await connection.QueryAsync("SELECT id, name FROM users ORDER BY id", cancellationToken: TestTimeout());
            rows.Count.ShouldBe(3);
            rows[0].GetString("name").ShouldBe("ada");
            rows[2].GetString("name").ShouldBe("committed");

            await ((IHost)reopened.Application).StopAsync(TestTimeout());
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Application] - E2E: The grouped durability mode round-trips and survives a restart")]
    public async Task EndToEnd_GroupedDurability_ShouldServeAndRecover()
    {
        // ---- Grouped commits ride the host-claimed WAL flush worker. ----
        var configuration = new DatabaseHostConfiguration { DataPath = _rootPath, Port = 0, Durability = "grouped" };

        await using (var composition = DatabaseApplicationBootstrap.Compose(configuration))
        {
            await ((IHost)composition.Application).StartAsync(TestTimeout());
            int port = await WaitForBoundPortAsync(composition.Listener);

            await using var client = CreateClient(port, DatabaseApplicationBootstrap.DefaultDatabaseName);
            await using var connection = await client.ConnectAsync(TestTimeout());

            await connection.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)", cancellationToken: TestTimeout());

            for (int i = 0; i < 5; i++)
            {
                await connection.ExecuteAsync($"INSERT INTO t (id) VALUES ({i})", cancellationToken: TestTimeout());
            }

            SqlResultSet rows = await connection.QueryAsync("SELECT id FROM t", cancellationToken: TestTimeout());
            rows.Count.ShouldBe(5);

            await ((IHost)composition.Application).StopAsync(TestTimeout());
        }

        // ---- Grouped-mode commits were acknowledged only once durable: recover. ----
        await using (var reopened = DatabaseApplicationBootstrap.Compose(new DatabaseHostConfiguration { DataPath = _rootPath, Port = 0 }))
        {
            await ((IHost)reopened.Application).StartAsync(TestTimeout());
            int port = await WaitForBoundPortAsync(reopened.Listener);

            await using var client = CreateClient(port, DatabaseApplicationBootstrap.DefaultDatabaseName);
            await using var connection = await client.ConnectAsync(TestTimeout());

            SqlResultSet rows = await connection.QueryAsync("SELECT id FROM t", cancellationToken: TestTimeout());
            rows.Count.ShouldBe(5);

            await ((IHost)reopened.Application).StopAsync(TestTimeout());
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Application] - E2E: Checkpoints under load never corrupt subsequent reads")]
    public async Task EndToEnd_CheckpointMidLoad_ShouldKeepReadsConsistent()
    {
        // Arrange: the executable's composition shape, with an aggressive checkpoint
        // cadence so several checkpoint passes land during the load.
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "sql",
            RootPath = _rootPath,
            CheckpointInterval = TimeSpan.FromMilliseconds(25),
        });

        var listener = new TcpConnectionListener(new TcpConnectionListenerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        });

        SqlDatabaseServer server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });

        var applicationOptions = new DatabaseApplicationOptions();
        applicationOptions.Servers.Add(server);
        var application = new DatabaseApplication(applicationOptions);

        await ((IHost)application).StartAsync(TestTimeout(60));
        int port = await WaitForBoundPortAsync(listener);

        var database = await engine.CreateDatabaseAsync("load");

        // Act: a write load with interleaved reads while the checkpointer runs.
        await using (var client = CreateClient(port, "load"))
        await using (var connection = await client.ConnectAsync(TestTimeout(60)))
        {
            await connection.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)", cancellationToken: TestTimeout(60));

            for (int i = 1; i <= 150; i++)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO t (id) VALUES (@id)",
                    new Dictionary<string, object?> { ["id"] = i },
                    TestTimeout(60));

                if (i % 25 == 0)
                {
                    SqlResultSet slice = await connection.QueryAsync("SELECT id FROM t", cancellationToken: TestTimeout(60));
                    slice.Count.ShouldBe(i);
                }
            }

            // Wait for a quiescent checkpoint to truncate the data journal — proof a
            // checkpoint actually ran — then read again.
            string journalPath = Path.Combine(_rootPath, "load", "load.log");
            long deadline = Environment.TickCount64 + 15_000;

            while (Environment.TickCount64 < deadline && GetFileLength(journalPath) >= 512)
            {
                await Task.Delay(50);
            }

            GetFileLength(journalPath).ShouldBeLessThan(512);

            SqlResultSet final = await connection.QueryAsync("SELECT id FROM t", cancellationToken: TestTimeout(60));
            final.Count.ShouldBe(150);
        }

        await ((IHost)application).StopAsync(TestTimeout(60));
        await server.DisposeAsync();
        await listener.DisposeAsync();
        await engine.DisposeAsync();

        // Assert: everything the checkpointed files hold survives a fresh composition.
        await using var reopened = DatabaseApplicationBootstrap.Compose(new DatabaseHostConfiguration { DataPath = _rootPath, Port = 0 });
        await ((IHost)reopened.Application).StartAsync(TestTimeout(60));
        int reopenedPort = await WaitForBoundPortAsync(reopened.Listener);

        await using (var client = CreateClient(reopenedPort, "load"))
        await using (var connection = await client.ConnectAsync(TestTimeout(60)))
        {
            SqlResultSet rows = await connection.QueryAsync("SELECT id FROM t", cancellationToken: TestTimeout(60));
            rows.Count.ShouldBe(150);
        }

        await ((IHost)reopened.Application).StopAsync(TestTimeout(60));
    }

    private static long GetFileLength(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.Length;
    }
}
