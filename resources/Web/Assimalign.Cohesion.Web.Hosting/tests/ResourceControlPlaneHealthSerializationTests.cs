using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class ResourceControlPlaneHealthSerializationTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Control-plane health preserves diagnostic data")]
    public async Task HealthEndpoint_WithContributionData_ShouldSerializeClosedValueSet()
    {
        // Arrange
        int port = ReservePort();
        Uri endpoint = Uri.CreateEndpoint("http", "127.0.0.1", port);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            endpoints: new Dictionary<string, Uri> { ["http"] = endpoint }));
        var data = new Dictionary<string, object>
        {
            ["empty"] = null!,
            ["region"] = "us-east",
            ["degraded"] = true,
            ["attempts"] = 3,
            ["requests"] = 4_000_000_000L,
            ["ratio"] = 1.25D,
            ["load"] = 2.5F,
            ["cost"] = 3.75M,
            ["marker"] = new DiagnosticMarker(),
        };
        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        builder.AddHealthCheck(
            "database",
            _ => ValueTask.FromResult(HealthContribution.Healthy("connected", data)));
        await using WebApplication application = builder.Build();

        // Act
        await ((IHost)application).StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = endpoint };
            using JsonDocument document = JsonDocument.Parse(await client.GetStringAsync(
                "/healthz",
                CancellationToken.None));
            JsonElement result = document.RootElement
                .GetProperty("contributions")
                .GetProperty("database")
                .GetProperty("data");

            // Assert
            result.GetProperty("empty").ValueKind.ShouldBe(JsonValueKind.Null);
            result.GetProperty("region").GetString().ShouldBe("us-east");
            result.GetProperty("degraded").GetBoolean().ShouldBeTrue();
            result.GetProperty("attempts").GetInt32().ShouldBe(3);
            result.GetProperty("requests").GetInt64().ShouldBe(4_000_000_000L);
            result.GetProperty("ratio").GetDouble().ShouldBe(1.25D);
            result.GetProperty("load").GetSingle().ShouldBe(2.5F);
            result.GetProperty("cost").GetDecimal().ShouldBe(3.75M);
            result.GetProperty("marker").GetString().ShouldBe("diagnostic-marker");
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class DiagnosticMarker
    {
        public override string ToString() => "diagnostic-marker";
    }
}
