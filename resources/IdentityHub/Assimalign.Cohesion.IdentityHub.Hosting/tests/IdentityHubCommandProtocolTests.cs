using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using IdentityHubResourceControlPlane = Assimalign.Cohesion.ApplicationModel.IdentityHubResourceControlPlane;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Tests;

/// <summary>
/// Drives declarative identity commands through a real, gateway-managed IdentityHub host over the
/// standard control-plane <c>commands</c> route, the wire the gateway's generic
/// <c>ResourceControlPlaneCommandClient</c> speaks now that the IdentityHub client package is retired.
/// </summary>
public sealed class IdentityHubCommandProtocolTests
{
    private const string resourceName = "identity";

    // An orchestration-enabled executable registers the IdentityHub ApplicationModel's default
    // control plane for its entry assembly. The ApplicationModel assembly stands in for that entry
    // assembly here, so the registration leaves every other test in this assembly unchanged.
    private static readonly Assembly _resourceAssembly = typeof(IdentityHubResourceControlPlane).Assembly;

    static IdentityHubCommandProtocolTests() => ResourceRuntime.RegisterControlPlane(
        _resourceAssembly, IdentityHubResourceControlPlane.Create);

    [Fact(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Commands: authenticated apply, replay, owner refusal, and delete survive a restart")]
    public async Task Commands_ManagedHost_ShouldApplyReplayRefuseAndDeleteAcrossRestart()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        using var identity = new TestBootstrapIdentity();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string token = identity.Issue(resourceName);
        Uri endpoint = IdentityHubTestHost.GetEndpoint();
        var commandsRoute = new Uri(endpoint, "/cohesion/v1/commands");
        var context = new ResourceContext(
            identity.Issuer,
            resourceName,
            AppEnvironment.Keys.Local,
            identity.Subject,
            data.Path,
            new Dictionary<string, Uri> { ["https"] = endpoint },
            new Dictionary<string, ResourceMount>
            {
                ["data"] = new ResourceMount(data.Path),
                ["credential"] = ResourceMount.FromBytes("orders-secret"u8),
            },
            settings: null,
            references: null,
            Encoding.UTF8.GetBytes(token),
            identity.PublicKey,
            ambientValues: null);
        using IDisposable scope = ResourceRuntime.CreateScope(context);
        ResourceCommand[] commands =
        [
            new("command-0", "identityhub.add-audience", identity.Issuer, "orders",
                "{\"name\":\"orders\"}"u8.ToArray()),
            new("command-1", "identityhub.add-client", identity.Issuer, "orders",
                "{\"clientId\":\"orders\",\"audiences\":[\"orders\"],\"credentialSource\":\"credential\"}"u8.ToArray()),
        ];
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        IdentityHubApplication application = IdentityHubApplication.CreateBuilder([], _resourceAssembly).Build();

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
            using (HttpResponseMessage issued = await RequestClientTokenAsync(endpoint, timeout.Token))
            {
                issued.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            // Act: restart the host over the same data directory.
            await ((IHost)application).StopAsync(timeout.Token);
            await ((IAsyncDisposable)application).DisposeAsync();
            application = IdentityHubApplication.CreateBuilder([], _resourceAssembly).Build();
            await ((IHost)application).StartAsync(timeout.Token);

            // Assert: the command-registered client still authenticates after the restart.
            using (HttpResponseMessage reissued = await RequestClientTokenAsync(endpoint, timeout.Token))
            {
                reissued.StatusCode.ShouldBe(HttpStatusCode.OK);
                using JsonDocument body = JsonDocument.Parse(await reissued.Content.ReadAsStringAsync(timeout.Token));
                body.RootElement.GetProperty("access_token").GetString().ShouldNotBeNullOrWhiteSpace();
            }

            // Assert: the restored ledger refuses another owner at the control plane itself.
            ResourceRuntime.TryGetControlPlane((IHost)application, out IResourceControlPlane? plane).ShouldBeTrue();
            plane.ShouldNotBeNull();
            foreach (ResourceCommand existing in commands)
            {
                ResourceCommand foreign = existing with { Id = "foreign-direct", Owner = "other" };
                ResourceCommandRejectedException conflict = await Should.ThrowAsync<ResourceCommandRejectedException>(
                    () => plane.ExecuteCommandAsync(foreign, timeout.Token).AsTask());
                conflict.Detail.ShouldContain(identity.Issuer, Case.Sensitive);
                conflict.Detail.ShouldContain(existing.Key, Case.Sensitive);
            }

            // Assert: the route refuses a declaration whose owner is not the authenticated application.
            ResourceCommand owned = commands[^1];
            using (HttpResponseMessage refused = await SendCommandAsync(
                client, HttpMethod.Post, commandsRoute, owned with { Id = "foreign", Owner = "other" }, timeout.Token))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                using JsonDocument refusal = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(timeout.Token));
                refusal.RootElement.GetProperty("status").GetString().ShouldBe("Rejected");
                string detail = refusal.RootElement.GetProperty("detail").GetString().ShouldNotBeNull();
                detail.ShouldContain("other", Case.Sensitive);
                detail.ShouldContain(identity.Issuer, Case.Sensitive);
            }

            // Act: withdraw both declarations, newest first.
            foreach (ResourceCommand command in commands.Reverse())
            {
                using HttpResponseMessage deleted = await SendCommandAsync(
                    client, HttpMethod.Delete, commandsRoute, command, timeout.Token);
                deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            // Assert: nothing stays declared and the withdrawn client can no longer authenticate.
            using JsonDocument remaining = JsonDocument.Parse(await client.GetStringAsync(commandsRoute, timeout.Token));
            remaining.RootElement.GetProperty("commands").GetArrayLength().ShouldBe(0);
            using HttpResponseMessage revoked = await RequestClientTokenAsync(endpoint, timeout.Token);
            revoked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
            await ((IAsyncDisposable)application).DisposeAsync();
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

    private static async Task<HttpResponseMessage> RequestClientTokenAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "orders",
            ["client_secret"] = "orders-secret",
            ["audience"] = "orders",
        });
        return await client.PostAsync(new Uri(endpoint, "/oauth2/token"), form, cancellationToken);
    }
}
