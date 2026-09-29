using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using RezolvrResourceControlPlane = Assimalign.Cohesion.ApplicationModel.RezolvrResourceControlPlane;

namespace Assimalign.Cohesion.Rezolvr.Hosting.Tests;

/// <summary>
/// Drives declarative DNS record commands through a real, gateway-managed Rezolvr host over the
/// standard control-plane <c>commands</c> route, the wire the gateway's generic
/// <c>ResourceControlPlaneCommandClient</c> speaks now that the Rezolvr client package is retired.
/// </summary>
public sealed class RezolvrCommandProtocolTests
{
    private const string applicationName = "appa";
    private const string resourceName = "resolver";

    // An orchestration-enabled executable registers the Rezolvr ApplicationModel's default control
    // plane for its entry assembly. The ApplicationModel assembly stands in for that entry assembly
    // here, so the registration leaves the test assembly's own control-plane registration unchanged.
    private static readonly Assembly _resourceAssembly = typeof(RezolvrResourceControlPlane).Assembly;

    static RezolvrCommandProtocolTests() => ResourceRuntime.RegisterControlPlane(
        _resourceAssembly, RezolvrResourceControlPlane.Create);

    [Fact(DisplayName = "Cohesion Test [Rezolvr.Hosting] - Commands: authenticated apply, replay, owner refusal, and delete survive a restart")]
    public async Task Commands_ManagedHost_ShouldApplyReplayRefuseAndDeleteAcrossRestart()
    {
        // Arrange
        DirectoryInfo data = Directory.CreateTempSubdirectory("cohesion-rezolvr-tests-");
        using var identity = new TestBootstrapIdentity(applicationName, "local");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string token = identity.Issue(resourceName);
        Uri endpoint = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        var commandsRoute = new Uri(endpoint, "/cohesion/v1/commands");
        var context = new ResourceContext(
            applicationName,
            resourceName,
            "Development",
            identity.Subject,
            data.FullName,
            new Dictionary<string, Uri> { ["admin"] = endpoint },
            new Dictionary<string, ResourceMount> { ["data"] = new ResourceMount(data.FullName) },
            settings: null,
            references: null,
            Encoding.UTF8.GetBytes(token),
            identity.PublicKey,
            ambientValues: null);
        using IDisposable scope = ResourceRuntime.CreateScope(context);
        ResourceCommand[] commands =
        [
            new("command-0", "rezolvr.add-a-record", applicationName, "api.example",
                "{\"name\":\"api.example\",\"address\":\"192.0.2.1\",\"ttlSeconds\":300}"u8.ToArray()),
            new("command-1", "rezolvr.add-cname-record", applicationName, "www.example",
                "{\"name\":\"www.example\",\"target\":\"api.example\",\"ttlSeconds\":300}"u8.ToArray()),
        ];
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        RezolvrApplication application = RezolvrApplication.CreateBuilder([], _resourceAssembly).Build();

        try
        {
            // Act: apply both declarations, then replay each under a new idempotency id.
            await ((IHost)application).StartAsync(timeout.Token);
            foreach (ResourceCommand command in commands)
            {
                using HttpResponseMessage applied = await SendCommandAsync(
                    client, HttpMethod.Post, commandsRoute, command, timeout.Token);
                applied.StatusCode.ShouldBe(HttpStatusCode.OK);
            }
            for (int index = 0; index < commands.Length; index++)
            {
                ResourceCommand original = commands[index];
                commands[index] = original with { Id = original.Id + "-replay" };
                using HttpResponseMessage replayed = await SendCommandAsync(
                    client, HttpMethod.Post, commandsRoute, commands[index], timeout.Token);
                replayed.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            // Act: restart the host over the same data directory.
            await ((IHost)application).StopAsync(timeout.Token);
            await ((IAsyncDisposable)application).DisposeAsync();
            application = RezolvrApplication.CreateBuilder([], _resourceAssembly).Build();
            await ((IHost)application).StartAsync(timeout.Token);

            // Assert: the restored ledger still lists both declarations under their latest ids.
            using (JsonDocument restored = JsonDocument.Parse(await client.GetStringAsync(commandsRoute, timeout.Token)))
            {
                restored.RootElement.GetProperty("commands").EnumerateArray()
                    .Select(static command => command.GetProperty("id").GetString().ShouldNotBeNull())
                    .OrderBy(static id => id, StringComparer.Ordinal)
                    .ShouldBe(commands.Select(static command => command.Id));
            }

            // Assert: the restored ledger refuses another owner at the control plane itself.
            ResourceRuntime.TryGetControlPlane((IHost)application, out IResourceControlPlane? plane).ShouldBeTrue();
            plane.ShouldNotBeNull();
            foreach (ResourceCommand existing in commands)
            {
                ResourceCommand foreign = existing with { Id = "foreign-direct", Owner = "other" };
                ResourceCommandRejectedException conflict = await Should.ThrowAsync<ResourceCommandRejectedException>(
                    () => plane.ExecuteCommandAsync(foreign, timeout.Token).AsTask());
                conflict.Detail.ShouldContain(applicationName, Case.Sensitive);
                conflict.Detail.ShouldContain(existing.Key, Case.Sensitive);
            }

            // Assert: the route reports the same refusal with its detail.
            ResourceCommand owned = commands[^1];
            using (HttpResponseMessage refused = await SendCommandAsync(
                client, HttpMethod.Post, commandsRoute, owned with { Id = "foreign", Owner = "other" }, timeout.Token))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                using JsonDocument refusal = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(timeout.Token));
                refusal.RootElement.GetProperty("status").GetString().ShouldBe("Rejected");
                string detail = refusal.RootElement.GetProperty("detail").GetString().ShouldNotBeNull();
                detail.ShouldContain("other", Case.Sensitive);
                detail.ShouldContain(applicationName, Case.Sensitive);
            }

            // Act: withdraw both declarations, newest first.
            foreach (ResourceCommand command in commands.Reverse())
            {
                using HttpResponseMessage deleted = await SendCommandAsync(
                    client, HttpMethod.Delete, commandsRoute, command, timeout.Token);
                deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            // Assert: nothing stays declared.
            using JsonDocument remaining = JsonDocument.Parse(await client.GetStringAsync(commandsRoute, timeout.Token));
            remaining.RootElement.GetProperty("commands").GetArrayLength().ShouldBe(0);
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
            await ((IAsyncDisposable)application).DisposeAsync();
            data.Delete(recursive: true);
        }
    }

    private static async Task<HttpResponseMessage> SendCommandAsync(
        HttpClient client,
        HttpMethod method,
        Uri commandsRoute,
        ResourceCommand command,
        CancellationToken cancellationToken)
    {
        var body = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("id", command.Id);
            writer.WriteString("kind", command.Kind);
            writer.WriteString("owner", command.Owner);
            writer.WriteString("key", command.Key);
            writer.WriteBase64String("payload", command.Payload.Span);
            writer.WriteEndObject();
        }

        using var request = new HttpRequestMessage(method, commandsRoute)
        {
            Content = new ByteArrayContent(body.WrittenSpan.ToArray()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.SendAsync(request, cancellationToken);
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
