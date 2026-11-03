using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Tcp.AotSample;

internal static class HealthEndpointSmoke
{
    internal static async Task RunAsync(CancellationToken cancellationToken)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        using var environment = AppEnvironment.Enter(AppEnvironmentScope.Create(new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Gateway] = "aot-smoke",
            [AppEnvironment.Variables.Endpoint("http", "HOST")] = "127.0.0.1",
            [AppEnvironment.Variables.Endpoint("http", "PORT")] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [AppEnvironment.Variables.Endpoint("http", "SCHEME")] = "http",
        }, cancellationToken));
        await using DatabaseApplication application = DatabaseApplication.CreateBuilder().Build(cancellationToken);
        reservation.Stop();
        await ((IHost)application).StartAsync(cancellationToken);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        foreach (string path in new[] { "/cohesion/v1/readyz", "/cohesion/v1/livez" })
        {
            using HttpResponseMessage response = await client.GetAsync(path, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        await ((IHost)application).StopAsync(cancellationToken);
        using var peer = new TcpClient();
        try
        {
            await peer.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            throw new InvalidOperationException("The health listener still accepts after stop.");
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
        }
    }
}
