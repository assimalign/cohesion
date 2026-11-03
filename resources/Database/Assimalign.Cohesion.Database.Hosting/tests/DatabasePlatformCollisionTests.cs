using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed class DatabasePlatformCollisionTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Shared platform wire assignments roll back both composition shapes (#1790)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_SharedPlatformEndpoint_ShouldFailAndReleaseFirstListener(bool twoEngines)
    {
        // Arrange: the model-neutral server contract exposes no listener or assignment origin.
        // This locks in the documented collision limit until composition has an approved seam.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        using var environment = AppEnvironment.Enter(AppEnvironmentScope.Create(new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Gateway] = "test",
            [AppEnvironment.Variables.Endpoint("sql", "HOST")] = "127.0.0.1",
            [AppEnvironment.Variables.Endpoint("sql", "PORT")] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [AppEnvironment.Variables.Endpoint("sql", "SCHEME")] = "tcp",
        }, guard.Token));
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddSql("orders", sql =>
        {
            sql.AddServer(_ => { });
            if (!twoEngines) { sql.AddServer(_ => { }); }
        });
        if (twoEngines) { builder.AddSql("audit", sql => sql.AddServer(_ => { })); }
        await using DatabaseApplication application = builder.Build();
        reservation.Stop();

        // Act
        var failure = await Should.ThrowAsync<SocketException>(() => ((IHost)application).StartAsync(guard.Token));

        // Assert
        failure.SocketErrorCode.ShouldBe(SocketError.AddressAlreadyInUse);
        application.Context.State.ShouldBe(HostState.Failed);
        using var peer = new TcpClient();
        var refusal = await Should.ThrowAsync<SocketException>(async () => await peer.ConnectAsync(IPAddress.Loopback, port, guard.Token));
        refusal.SocketErrorCode.ShouldBe(SocketError.ConnectionRefused);
    }
}
