using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.ApplicationModel;
using Assimalign.Cohesion.Database.Application.Internal;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Client;

using CohesionApplication = Assimalign.Cohesion.ApplicationModel.Application;

namespace Assimalign.Cohesion.Database.Application.Tests;

/// <summary>
/// The deferred #853 real-process gateway end-to-end: the <see cref="LocalGateway"/>
/// resolves the <c>Assimalign.Cohesion.Database.Application</c> artifact (the
/// apphost this test project copies into its own output via the project reference),
/// launches it as a supervised child process with the manifest-injected environment,
/// and the typed SQL client speaks to it over real TCP. The gateway's MVP stop is a
/// forceful kill — which doubles as crash-recovery proof: rows committed before the
/// kill survive a relaunch on the same data directory.
/// </summary>
public sealed class DatabaseGatewayEndToEndTests : IDisposable
{
    private readonly string _dataPath;

    public DatabaseGatewayEndToEndTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "cohesion-db-gateway-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            try
            {
                Directory.Delete(_dataPath, recursive: true);
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
    /// Reserves a free loopback port: the resource declares an explicit port so both
    /// the child process and this test agree on the endpoint deterministically.
    /// </summary>
    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static ISqlClient CreateClient(int port)
        => SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = DatabaseApplicationBootstrap.DefaultDatabaseName,
                Principal = "gateway-e2e",
                EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            },
            ConnectionFactory = new TcpConnectionFactory(),
        });

    /// <summary>
    /// Connects with retries: the readiness marker prints before the endpoint's
    /// accept loop binds, so the first connect attempts can race the child's startup.
    /// </summary>
    private static async Task<ISqlConnection> ConnectWithRetryAsync(ISqlClient client, int seconds = 30)
    {
        long deadline = Environment.TickCount64 + (seconds * 1000L);

        while (true)
        {
            try
            {
                return await client.ConnectAsync(TestTimeout(5));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && Environment.TickCount64 < deadline)
            {
                // Startup race; retry until the child accepts. (Broad catch is
                // deliberate in this retry loop: any transport-level failure mode
                // before the deadline is "not up yet".)
                await Task.Delay(250);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Application] - Gateway E2E: LocalGateway launches the real host process, SQL round-trips, and a relaunch recovers the data")]
    public async Task LocalGateway_RealHostProcess_ShouldServeSqlAndRecoverAcrossRelaunch()
    {
        // Arrange: the artifact must be on disk next to this test assembly (the
        // project reference copies the apphost). Skip-proof: fail loudly if not.
        string artifactPath = Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.Database.Application" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        File.Exists(artifactPath).ShouldBeTrue($"the apphost was not copied to the test output: {artifactPath}");

        int port = GetFreePort();

        IApplicationModel BuildModel(IApplicationGateway gateway)
        {
            IApplicationBuilder builder = CohesionApplication.CreateBuilder().UseGateway(gateway);
            builder.AddDatabase("orders-db", options =>
            {
                options.Port = port;
                options.DataMountPath = _dataPath;
            });
            return builder.Build().Model;
        }

        // ---- First launch: the gateway starts the real process; SQL round-trips. ----
        var gateway = new LocalGateway(new LocalGatewayOptions { ReadyMarker = "cohesion-db: starting" });

        await ((IApplicationGateway)gateway).StartAsync(BuildModel(gateway), TestTimeout(60));

        try
        {
            await using var client = CreateClient(port);
            await using var connection = await ConnectWithRetryAsync(client);

            await connection.ExecuteAsync("CREATE TABLE orders (id INT NOT NULL, item VARCHAR(100))", cancellationToken: TestTimeout());
            await connection.ExecuteAsync("INSERT INTO orders (id, item) VALUES (1, 'widget'), (2, 'gadget')", cancellationToken: TestTimeout());

            SqlResultSet rows = await connection.QueryAsync("SELECT id, item FROM orders ORDER BY id", cancellationToken: TestTimeout());
            rows.Count.ShouldBe(2);
            rows[0].GetString("item").ShouldBe("widget");
        }
        finally
        {
            // MVP gateway stop is a forceful kill of the child tree.
            await ((IApplicationGateway)gateway).StopAsync(TestTimeout(60));
        }

        // ---- Relaunch on the same data directory: WAL recovery through the real process. ----
        var relaunched = new LocalGateway(new LocalGatewayOptions { ReadyMarker = "cohesion-db: starting" });

        await ((IApplicationGateway)relaunched).StartAsync(BuildModel(relaunched), TestTimeout(60));

        try
        {
            await using var client = CreateClient(port);
            await using var connection = await ConnectWithRetryAsync(client);

            SqlResultSet rows = await connection.QueryAsync("SELECT id, item FROM orders ORDER BY id", cancellationToken: TestTimeout());
            rows.Count.ShouldBe(2);
            rows[1].GetString("item").ShouldBe("gadget");
        }
        finally
        {
            await ((IApplicationGateway)relaunched).StopAsync(TestTimeout(60));
        }
    }
}
