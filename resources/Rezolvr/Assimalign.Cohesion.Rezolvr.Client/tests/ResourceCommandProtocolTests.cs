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
// The shared ApplicationModel namespace also declares a ResourceMount; this file means the runtime one.
using ResourceMount = Assimalign.Cohesion.Hosting.Resources.ResourceMount;
using Assimalign.Cohesion.Rezolvr;
using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Rezolvr.Hosting;

namespace Assimalign.Cohesion.Rezolvr.Client.Tests;

/// <summary>Exercises the shipped client against a real managed resource listener.</summary>
public sealed class ResourceCommandProtocolTests
{
    static ResourceCommandProtocolTests() => ResourceRuntime.RegisterControlPlane(
        typeof(ResourceCommandProtocolTests).Assembly, RezolvrResourceControlPlane.Create);

    /// <summary>Authenticated apply, replay, refusal and deletion round-trip through the actual host.</summary>
    [Fact(DisplayName = "Cohesion Test [Rezolvr.Client] - Commands: authenticated loopback apply replay refusal and delete survive restart")]
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
        var context = new ResourceContext("appa", "resource", "Development", "local", data,
            new Dictionary<string, Uri> { ["admin"] = endpoint },
            new Dictionary<string, ResourceMount>
            {
                ["data"] = new ResourceMount(data),
                ["credential"] = ResourceMount.FromBytes("test-client-secret"u8),
            }, settings: null, references: null, Encoding.UTF8.GetBytes(token), identity.PublicKey, ambientValues: null);
        using IDisposable scope = ResourceRuntime.CreateScope(context);
        RezolvrApplication application = RezolvrApplication.CreateBuilder([], typeof(ResourceCommandProtocolTests).Assembly).Build();
        using IRezolvrCommandClient client = RezolvrCommandClient.Create(address, token);
        ResourceCommand[] commands =
        [
            new("command-0", "rezolvr.add-a-record", "appa", "api.example", Encoding.UTF8.GetBytes("{\"name\":\"api.example\",\"address\":\"192.0.2.1\",\"ttlSeconds\":300}")),
            new("command-1", "rezolvr.add-cname-record", "appa", "www.example", Encoding.UTF8.GetBytes("{\"name\":\"www.example\",\"target\":\"api.example\",\"ttlSeconds\":300}")),
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

            await ((IHost)application).StopAsync(timeout.Token);
            await ((IAsyncDisposable)application).DisposeAsync();
            application = RezolvrApplication.CreateBuilder([], typeof(ResourceCommandProtocolTests).Assembly).Build();
            await ((IHost)application).StartAsync(timeout.Token);

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
