using System;
using System.IO;
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
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlTypedMigrationWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Typed migration: a populated code-first TYPE publishes through both existing wire transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_CodeFirstTypeMigration_ShouldReturnPublishedLayout(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string path = Path.Combine(Path.GetTempPath(), "cohesion-joint-wire", Guid.NewGuid().ToString("N"));
        try
        {
            var seed = SqlDatabaseEngine.CreateBuilder("joint-wire-seed");
            seed.Options.RootPath = path;
            await using (var engine = await seed.AddDatabase(Source()).BuildAsync(timeout.Token))
            {
                var seeded = await engine.OpenDatabaseAsync("typed", timeout.Token);
                await using var writer = await seeded.CreateSessionAsync(timeout.Token);
                await writer.ExecuteAsync("INSERT INTO items VALUES(1,10),(2,20)", cancellationToken: timeout.Token);
            }
            var builder = SqlDatabaseEngine.CreateBuilder("joint-wire");
            builder.Options.RootPath = path;
            await using var current = await builder.AddDatabase(Target()).BuildAsync(timeout.Token);
            await using IDuplexConnectionListener listener = tcp
                ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
                : new InMemoryConnectionListener();
            await using var server = SqlDatabaseServer.Create(current, new SqlDatabaseServerOptions { Listener = listener });
            await server.StartAsync(timeout.Token);
            await using var client = SqlClient.Create(new SqlClientOptions
            {
                Settings = new DatabaseConnectionSettings { Database = "typed", Principal = "tester", EndPoint = listener.EndPoint },
                ConnectionFactory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory(),
            });

            // Act
            await using var connection = await client.ConnectAsync(timeout.Token);
            var rows = await connection.QueryAsync("SELECT Value FROM items ORDER BY Id", cancellationToken: timeout.Token);

            // Assert
            rows.Select(row => row[0]).ShouldBe(new object?[] { 10L, 20L });
            await connection.ExecuteAsync("INSERT INTO items VALUES(3,30)", cancellationToken: timeout.Token);
            (await connection.QueryAsync("SELECT Value FROM items WHERE Value=30", cancellationToken: timeout.Token)).Single()[0].ShouldBe(30L);
            var database = await current.OpenDatabaseAsync("typed", timeout.Token);
            database.Provisioning.ShouldBeNull();
        }
        finally { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
    }

    private static SqlSchema Source() => SqlSchema.Create("typed", schema =>
        schema.Table<OldRow>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); table.Index(row => row.Value); }));

    private static SqlSchema Target() => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        schema.Table<NewRow>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); table.Index(row => row.Value); });
    });

    private sealed record OldRow(int Id, int Value);
    private sealed record NewRow(int Id, long Value);
}
