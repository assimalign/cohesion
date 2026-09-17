using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityHub;
using Assimalign.Cohesion.IdentityHub.ApplicationModel;
using Assimalign.Cohesion.IdentityHub.Hosting;

namespace Assimalign.Cohesion.IdentityHub.Client.Tests;

/// <summary>Exercises the shipped client against a real managed resource listener.</summary>
public sealed class ResourceCommandProtocolTests
{
    static ResourceCommandProtocolTests() => ResourceRuntime.RegisterControlPlane(
        typeof(ResourceCommandProtocolTests).Assembly, IdentityHubResourceControlPlane.Create);

    /// <summary>Authenticated apply, replay, refusal and deletion round-trip through the actual host.</summary>
    [Fact(DisplayName = "Cohesion Test [IdentityHub.Client] - Commands: authenticated loopback apply replay refusal and delete survive restart")]
    public async Task Commands_RealHost_ShouldObserveMutationsAsync()
    {
        string data = CreateDataDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var identity = new TestBootstrapIdentity();
        string token = identity.Issue("resource");
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}");
        port.Stop();
        var address = new Uri(endpoint, "/cohesion/v1");
        var context = new ResourceContext("appa", "resource", AppEnvironment.Keys.Local, "local", data,
            new Dictionary<string, Uri> { ["https"] = endpoint },
            new Dictionary<string, ResourceMount>
            {
                ["data"] = new ResourceMount(data),
                ["credential"] = ResourceMount.FromBytes("orders-secret"u8),
            }, settings: null, references: null, Encoding.UTF8.GetBytes(token), identity.PublicKey, ambientValues: null);
        using IDisposable scope = ResourceRuntime.CreateScope(context);
        IdentityHubApplication application = IdentityHubApplication.CreateBuilder([], typeof(ResourceCommandProtocolTests).Assembly).Build();
        using IIdentityHubCommandClient client = IdentityHubCommandClient.Create(address, token);
        ResourceCommand[] commands =
        [
            new("command-0", "identityhub.add-audience", "appa", "orders", Encoding.UTF8.GetBytes("{\"name\":\"orders\"}")),
            new("command-1", "identityhub.add-client", "appa", "orders", Encoding.UTF8.GetBytes("{\"clientId\":\"orders\",\"audiences\":[\"orders\"],\"credentialSource\":\"credential\"}")),
        ];
        try
        {
            await ((IHost)application).StartAsync(timeout.Token);
            foreach (ResourceCommand command in commands)
            {
                ResourceCommandObservation applied = await client.SendCommandAsync(command, timeout.Token);
                applied.Status.ShouldBe("Applied");
                applied.Detail.ShouldBeNull();
            }
            for (int index = 0; index < commands.Length; index++)
            {
                ResourceCommand original = commands[index];
                commands[index] = new ResourceCommand(original.Id + "-reapply", original.Kind, original.Owner, original.Key, original.Payload);
                (await client.SendCommandAsync(commands[index], timeout.Token)).Status.ShouldBe("Applied");
            }
            await VerifyTokenAsync(endpoint, timeout.Token);
            await ((IHost)application).StopAsync(timeout.Token);
            await ((IAsyncDisposable)application).DisposeAsync();
            application = IdentityHubApplication.CreateBuilder([], typeof(ResourceCommandProtocolTests).Assembly).Build();
            await ((IHost)application).StartAsync(timeout.Token);
            await VerifyTokenAsync(endpoint, timeout.Token);
            ResourceRuntime.TryGetControlPlane((Assimalign.Cohesion.Hosting.IHost)application,
                out IResourceControlPlane? plane).ShouldBeTrue();
            plane.ShouldNotBeNull();
            foreach (ResourceCommand existing in commands)
            {
                var foreign = new Assimalign.Cohesion.Hosting.Resources.ResourceCommand(
                    "foreign-direct", existing.Kind, "other", existing.Key, existing.Payload);
                ResourceCommandRejectedException conflict = await Should.ThrowAsync<ResourceCommandRejectedException>(
                    () => plane.ExecuteCommandAsync(foreign, timeout.Token).AsTask());
                conflict.Detail.ShouldContain("appa", Case.Sensitive);
                conflict.Detail.ShouldContain(existing.Key, Case.Sensitive);
            }
            ResourceCommand owned = commands[^1];
            ResourceCommandObservation refused = await client.SendCommandAsync(
                new ResourceCommand("foreign", owned.Kind, "other", owned.Key, owned.Payload), timeout.Token);
            refused.Status.ShouldBe("Rejected");
            refused.Detail.ShouldNotBeNull().ShouldContain("other", Case.Sensitive);
            refused.Detail.ShouldNotBeNull().ShouldContain("appa", Case.Sensitive);

            foreach (ResourceCommand command in commands.Reverse())
            {
                (await client.DeleteCommandAsync(command, timeout.Token)).Status.ShouldBe("Deleted");
            }
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using JsonDocument remaining = JsonDocument.Parse(await http.GetStringAsync(new Uri(address.ToString().TrimEnd('/') + "/commands"), timeout.Token));
            remaining.RootElement.GetProperty("commands").GetArrayLength().ShouldBe(0);
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
            await ((IAsyncDisposable)application).DisposeAsync();
            Directory.Delete(data, recursive: true);
        }
    }

    private static async Task VerifyTokenAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = "orders",
            ["client_secret"] = "orders-secret", ["audience"] = "orders",
        });
        using HttpResponseMessage response = await http.PostAsync(new Uri(endpoint, "/oauth2/token"), form, cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument token = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        token.RootElement.GetProperty("access_token").GetString().ShouldNotBeNullOrWhiteSpace();
        using HttpResponseMessage discovery = await http.GetAsync(new Uri(endpoint, "/.well-known/openid-configuration"), cancellationToken);
        using HttpResponseMessage jwks = await http.GetAsync(new Uri(endpoint, "/oauth2/jwks"), cancellationToken);
        discovery.StatusCode.ShouldBe(HttpStatusCode.OK);
        jwks.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static string CreateDataDirectory()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Assimalign.Cohesion.slnx"))) { root = root.Parent; }
        string path = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "_out", "31c-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
