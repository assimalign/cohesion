using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

using ClientCommand = Assimalign.Cohesion.Database.Client.ResourceCommand;
using RuntimeCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed class ResourceCommandHostingTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Commands: authenticated admin and direct delivery share engine mutations and ownership")]
    public async Task SendCommandAsync_WithGatewayScopedHost_ShouldApplyRefuseAndDeleteThroughRuntime()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        int number = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        var endpoint = new Uri($"http://127.0.0.1:{number}");
        const string token = "database-command-bootstrap";
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            applicationName: "appa", resourceName: "database", gatewayName: "local",
            endpoints: new Dictionary<string, Uri> { ["admin"] = endpoint },
            bootstrapCredential: Encoding.UTF8.GetBytes(token)));
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "commands" });
        await using var analytics = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "analytics" });
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions(), typeof(ResourceCommandHostingTests).Assembly);
        builder.AddEngine(engine);
        builder.AddEngine(analytics);
        await using DatabaseApplication application = builder.Build();
        ResourceRuntime.TryGetControlPlane(application, out IResourceControlPlane? plane).ShouldBeTrue();
        plane.ShouldNotBeNull();
        await ((IHost)application).StartAsync(cancellation.Token);
        try
        {
            using var http = new HttpClient { BaseAddress = endpoint };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            while (true)
            {
                try
                {
                    using HttpResponseMessage ready = await http.GetAsync("/readyz", cancellation.Token);
                    if (ready.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (HttpRequestException) when (!cancellation.IsCancellationRequested) { }
                await Task.Delay(20, cancellation.Token);
            }
            using IDatabaseCommandClient client = DatabaseCommandClient.Create(new Uri(endpoint, "/cohesion/v1"), token);
            byte[] payload = Encoding.UTF8.GetBytes("""{"database":"orders","engine":"commands"}""");
            var command = new ClientCommand("create-orders", "database.add-database", "appa", "commands/orders", payload);

            // Act / Assert
            (await client.SendCommandAsync(command, cancellation.Token)).Status.ShouldBe("Applied");
            (await client.SendCommandAsync(command, cancellation.Token)).Status.ShouldBe("Applied");
            engine.TryGetDatabase("orders", out _).ShouldBeTrue();
            var analyticsCommand = new ClientCommand("create-orders-analytics", "database.add-database", "appa", "analytics/orders",
                Encoding.UTF8.GetBytes("""{"database":"orders","engine":"analytics"}"""));
            (await client.SendCommandAsync(analyticsCommand, cancellation.Token)).Status.ShouldBe("Applied");
            analytics.TryGetDatabase("orders", out _).ShouldBeTrue();
            ResourceCommandObservation ambiguous = await client.SendCommandAsync(new ClientCommand(
                "ambiguous", "database.add-database", "appa", "ambiguous",
                Encoding.UTF8.GetBytes("""{"database":"ambiguous"}""")), cancellation.Token);
            ambiguous.Status.ShouldBe("Rejected");
            ambiguous.Detail.ShouldNotBeNull().ShouldContain("exactly one matching engine", Case.Sensitive);
            var ambiguousKey = new RuntimeCommand("ambiguous-key", "database.add-database", "appa", "commands/other/orders",
                Encoding.UTF8.GetBytes("""{"database":"other/orders","engine":"commands"}"""));
            ResourceCommandRejectedException keyRefusal = await Should.ThrowAsync<ResourceCommandRejectedException>(
                () => plane.ExecuteCommandAsync(ambiguousKey, cancellation.Token).AsTask());
            keyRefusal.Detail.ShouldContain("must not contain '/'", Case.Sensitive);
            ResourceCommandObservation keyObservation = await client.SendCommandAsync(new ClientCommand(
                ambiguousKey.Id, ambiguousKey.Kind, ambiguousKey.Owner, ambiguousKey.Key, ambiguousKey.Payload), cancellation.Token);
            keyObservation.Status.ShouldBe("Rejected");
            keyObservation.Detail.ShouldBe(keyRefusal.Detail);
            var runtime = new RuntimeCommand(command.Id, command.Kind, "appb", command.Key, payload);
            ResourceCommandRejectedException direct = await Should.ThrowAsync<ResourceCommandRejectedException>(
                () => plane.ExecuteCommandAsync(runtime, cancellation.Token).AsTask());
            ResourceCommandObservation refused = await client.SendCommandAsync(new ClientCommand(
                "other", command.Kind, "appb", command.Key, payload), cancellation.Token);
            refused.Status.ShouldBe("Rejected");
            refused.Detail.ShouldBe(direct.Detail);
            ResourceCommandObservation principal = await client.SendCommandAsync(new ClientCommand(
                "principal", "database.add-principal", "appa", "orders/reader",
                Encoding.UTF8.GetBytes("""{"database":"orders","name":"reader"}""")), cancellation.Token);
            principal.Status.ShouldBe("Rejected");
            principal.Detail.ShouldNotBeNull().ShouldContain("principal mutation seam", Case.Sensitive);
            foreach ((string body, HttpStatusCode expected) in new[]
            {
                ("""{"id":"blank","kind":"database.add-database","owner":"appa","key":" ","payload":""}""", HttpStatusCode.BadRequest),
                ("""{"id":"unknown","kind":"unknown","owner":"appa","key":"key","payload":""}""", HttpStatusCode.NotImplemented),
            })
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await http.PostAsync("/cohesion/v1/commands", content, cancellation.Token);
                response.StatusCode.ShouldBe(expected);
                if (expected == HttpStatusCode.NotImplemented)
                {
                    using JsonDocument refusal = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellation.Token));
                    refusal.RootElement.GetProperty("status").GetString().ShouldBe("Rejected");
                    refusal.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("unknown", Case.Sensitive);
                }
            }
            using JsonDocument listed = JsonDocument.Parse(await http.GetStringAsync("/cohesion/v1/commands", cancellation.Token));
            listed.RootElement.GetProperty("commands")[0].GetProperty("owner").GetString().ShouldBe("appa");
            (await client.DeleteCommandAsync(command, cancellation.Token)).Status.ShouldBe("Deleted");
            engine.TryGetDatabase("orders", out _).ShouldBeFalse();
            analytics.TryGetDatabase("orders", out _).ShouldBeTrue();
            (await client.DeleteCommandAsync(analyticsCommand, cancellation.Token)).Status.ShouldBe("Deleted");
            analytics.TryGetDatabase("orders", out _).ShouldBeFalse();
            plane.Commands.ShouldBeEmpty();
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }
}
