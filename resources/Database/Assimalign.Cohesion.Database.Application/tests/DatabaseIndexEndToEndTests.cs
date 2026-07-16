using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Application.Tests;

/// <summary>
/// Index adoption over the real stack (#914): CREATE TABLE → CREATE INDEX →
/// bulk load → indexed SELECT correctness under concurrent DML on separate
/// connections → restart → the index is still consistent with the recovered
/// data (registrations and root pages survive a wire-driven root split; the
/// re-export discipline proven end to end). Everything rides TCP loopback and
/// the typed SQL client, composed the way the executable composes it. The
/// planner's index usage itself is proven at the engine grain (the
/// records-examined suite in Database.Sql); this end-to-end proves the served
/// results and the persistence discipline.
/// </summary>
public sealed class DatabaseIndexEndToEndTests : IDisposable
{
    private readonly string _rootPath;

    public DatabaseIndexEndToEndTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-db-ix-e2e", Guid.NewGuid().ToString("N"));
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

    private static CancellationToken TestTimeout(int seconds = 60)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

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

    private static ISqlClient CreateClient(int port)
        => SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = DatabaseApplicationBootstrap.DefaultDatabaseName,
                Principal = "ix-e2e",
                EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            },
            ConnectionFactory = new TcpConnectionFactory(),
        });

    [Fact(DisplayName = "Cohesion Test [Database.Application] - E2E: secondary indexes serve correct results under concurrent DML and survive a restart")]
    public async Task EndToEnd_SecondaryIndexes_ShouldServeCorrectlyAndRecover()
    {
        // ---- First composition: DDL, a wire-driven bulk load big enough to
        // ---- split the index root, and indexed reads under concurrent DML. ----
        await using (var composition = DatabaseApplicationBootstrap.Compose(new DatabaseHostConfiguration { DataPath = _rootPath, Port = 0 }))
        {
            await ((IHost)composition.Application).StartAsync(TestTimeout());
            int port = await WaitForBoundPortAsync(composition.Listener);

            await using var client = CreateClient(port);
            await using var connection = await client.ConnectAsync(TestTimeout());

            await connection.ExecuteAsync(
                "CREATE TABLE items (id INT NOT NULL, category INT NOT NULL, name VARCHAR(100))",
                cancellationToken: TestTimeout());
            await connection.ExecuteAsync("CREATE UNIQUE INDEX ix_items_id ON items (id)", cancellationToken: TestTimeout());
            await connection.ExecuteAsync("CREATE INDEX ix_items_category ON items (category)", cancellationToken: TestTimeout());

            // 600 rows in wire batches: enough int keys to split the unique
            // index's root (a leaf holds ~250), so the restart leg below also
            // proves the drifted-root re-export.
            for (int start = 0; start < 600; start += 100)
            {
                var values = string.Join(", ", Enumerable.Range(start, 100).Select(i => $"({i}, {i % 10}, 'item-{i}')"));
                long affected = await connection.ExecuteAsync($"INSERT INTO items (id, category, name) VALUES {values}", cancellationToken: TestTimeout());
                affected.ShouldBe(100);
            }

            // Indexed point and category lookups over the wire.
            SqlResultSet byId = await connection.QueryAsync(
                "SELECT name FROM items WHERE id = @id",
                new Dictionary<string, object?> { ["id"] = 407 },
                TestTimeout());
            byId.ShouldHaveSingleItem();
            byId[0].GetString("name").ShouldBe("item-407");

            SqlResultSet byCategory = await connection.QueryAsync(
                "SELECT id FROM items WHERE category = 3 ORDER BY id",
                cancellationToken: TestTimeout());
            byCategory.Count.ShouldBe(60);
            byCategory[0].GetInt32("id").ShouldBe(3);

            // ---- Concurrent DML from a SECOND connection (per-connection
            // ---- dispatch is sequential; concurrency lives across connections),
            // ---- interleaved with indexed reads on the first. ----
            await using (var writerClient = CreateClient(port))
            await using (var writerConnection = await writerClient.ConnectAsync(TestTimeout()))
            {
                await writerConnection.ExecuteAsync("UPDATE items SET category = 99 WHERE id = 407", cancellationToken: TestTimeout());

                SqlResultSet moved = await connection.QueryAsync(
                    "SELECT id FROM items WHERE category = 99",
                    cancellationToken: TestTimeout());
                moved.ShouldHaveSingleItem();
                moved[0].GetInt32("id").ShouldBe(407);

                await writerConnection.ExecuteAsync("DELETE FROM items WHERE id = 3", cancellationToken: TestTimeout());

                SqlResultSet afterDelete = await connection.QueryAsync(
                    "SELECT id FROM items WHERE category = 3 ORDER BY id",
                    cancellationToken: TestTimeout());
                afterDelete.Count.ShouldBe(59);
                afterDelete[0].GetInt32("id").ShouldBe(13);

                // A duplicate key on the UNIQUE index fails the statement — an
                // ExecutionFailure on the wire — and the connection stays usable.
                var violation = await Should.ThrowAsync<SqlClientException>(async () =>
                    await writerConnection.ExecuteAsync("INSERT INTO items (id, category, name) VALUES (5, 0, 'dup')", cancellationToken: TestTimeout()));
                violation.ConnectionUsable.ShouldBeTrue();

                long replaced = await writerConnection.ExecuteAsync(
                    "INSERT INTO items (id, category, name) VALUES (3, 3, 'item-3-reborn')",
                    cancellationToken: TestTimeout());
                replaced.ShouldBe(1);
            }

            // An uncommitted engine-side writer is invisible to indexed reads
            // over the wire (snapshot isolation through the seek path).
            composition.Engine.TryGetDatabase(DatabaseApplicationBootstrap.DefaultDatabaseName, out IDatabase database).ShouldBeTrue();

            await using (var session = await database.CreateSessionAsync(TestTimeout()))
            {
                var uncommitted = await session.BeginTransactionAsync(TestTimeout());
                await session.ExecuteAsync("INSERT INTO items (id, category, name) VALUES (1000, 99, 'ghost')", cancellationToken: TestTimeout());

                SqlResultSet during = await connection.QueryAsync(
                    "SELECT id FROM items WHERE category = 99 ORDER BY id",
                    cancellationToken: TestTimeout());
                during.ShouldHaveSingleItem(); // only the committed move of 407

                await uncommitted.RollbackAsync(TestTimeout());
            }

            await ((IHost)composition.Application).StopAsync(TestTimeout());
        }

        // ---- Second composition: the recovered index serves the same truth. ----
        await using (var reopened = DatabaseApplicationBootstrap.Compose(new DatabaseHostConfiguration { DataPath = _rootPath, Port = 0 }))
        {
            await ((IHost)reopened.Application).StartAsync(TestTimeout());
            int port = await WaitForBoundPortAsync(reopened.Listener);

            await using var client = CreateClient(port);
            await using var connection = await client.ConnectAsync(TestTimeout());

            // Point lookups through the re-attached (root-split) unique index.
            SqlResultSet byId = await connection.QueryAsync(
                "SELECT name, category FROM items WHERE id = 407",
                cancellationToken: TestTimeout());
            byId.ShouldHaveSingleItem();
            byId[0].GetInt32("category").ShouldBe(99);

            SqlResultSet reborn = await connection.QueryAsync(
                "SELECT name FROM items WHERE id = 3",
                cancellationToken: TestTimeout());
            reborn.ShouldHaveSingleItem();
            reborn[0].GetString("name").ShouldBe("item-3-reborn");

            // The rolled-back ghost never came back; the category index still
            // reflects every committed mutation.
            SqlResultSet category99 = await connection.QueryAsync(
                "SELECT id FROM items WHERE category = 99",
                cancellationToken: TestTimeout());
            category99.ShouldHaveSingleItem();

            SqlResultSet category3 = await connection.QueryAsync(
                "SELECT id FROM items WHERE category = 3 ORDER BY id",
                cancellationToken: TestTimeout());
            category3.Count.ShouldBe(60); // 59 survivors + the reborn row

            // The recovered index keeps enforcing uniqueness...
            var violation = await Should.ThrowAsync<SqlClientException>(async () =>
                await connection.ExecuteAsync("INSERT INTO items (id, category, name) VALUES (407, 0, 'dup')", cancellationToken: TestTimeout()));
            violation.ConnectionUsable.ShouldBeTrue();

            // ...and index DDL keeps working over recovered data: build a fresh
            // index over the existing rows and read through it.
            await connection.ExecuteAsync("CREATE INDEX ix_items_name ON items (name)", cancellationToken: TestTimeout());

            SqlResultSet byName = await connection.QueryAsync(
                "SELECT id FROM items WHERE name = 'item-3-reborn'",
                cancellationToken: TestTimeout());
            byName.ShouldHaveSingleItem();
            byName[0].GetInt32("id").ShouldBe(3);

            await ((IHost)reopened.Application).StopAsync(TestTimeout());
        }
    }
}
