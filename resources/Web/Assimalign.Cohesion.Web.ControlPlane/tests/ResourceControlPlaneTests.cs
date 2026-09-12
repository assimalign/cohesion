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

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Testing;
using Assimalign.Cohesion.Web.Testing.TestHost;

namespace Assimalign.Cohesion.Web.ControlPlane.Tests;

public sealed class ResourceControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [Web.ControlPlane] - Protocol parity: matches the real Web.Hosting terminal")]
    public async Task Routes_WithSameResourceContext_ShouldMatchWebHosting()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var identity = new TestBootstrapIdentity("tests", "gateway");
        string credential = identity.Issue("resource");
        ResourceContext resource = CreateContext(identity, credential);
        using IDisposable scope = ResourceRuntime.CreateScope(resource);
        await using IWebApplicationProgramTestFactory hosted = WebApplicationTestFactory.FromProgram<Program>(
            new WebApplicationProgramTestFactoryOptions { ResourceContext = resource });
        await using var feature = new WebApplicationTestFactory();
        IResourceControlPlane controlPlane = ResourceControlPlane.Create();
        controlPlane.AddHealthContributor(new Contributor("ambient", HealthContribution.Healthy("alpha")));
        controlPlane.ObserveEndpoint("http", resource.Endpoints["http"]);
        controlPlane.AttachHost(feature.Application);
        ((IWebApplicationPipelineBuilder)feature.Application).UseResourceControlPlane(
            controlPlane, resource, () => feature.Application.Context.State is HostState.Started);
        await ((IHost)feature.Application).StartAsync(timeout.Token);
        using HttpClient hostedClient = hosted.CreateClient();
        using HttpClient featureClient = feature.CreateClient();

        foreach (string path in new[]
        {
            "/healthz", "/readyz", "/livez", "/cohesion/v1/healthz", "/cohesion/v1/readyz",
            "/cohesion/v1/livez", "/cohesion/v1/endpoints", "/cohesion/v1/commands",
        })
        {
            foreach (string method in new[] { "GET", "HEAD" })
            {
                await CompareAsync(method, path, null, credential, HttpStatusCode.OK, null);
            }
            string allow = path.EndsWith("commands", StringComparison.Ordinal) ? "GET, HEAD, POST, DELETE" : "GET, HEAD";
            await CompareAsync("PUT", path, null, credential, HttpStatusCode.MethodNotAllowed, allow);
        }
        await CompareAsync("GET", "/cohesion/v1/stop", null, credential, HttpStatusCode.MethodNotAllowed, "POST");
        await CompareAsync("GET", "/cohesion/v1/endpoints", null, null, HttpStatusCode.Unauthorized, null);
        await CompareAsync("GET", "/cohesion/v1/unknown", null, credential, HttpStatusCode.NotFound, null);
        await CompareAsync("POST", "/cohesion/v1/commands", Envelope("unknown"), credential, HttpStatusCode.NotImplemented, null);
        foreach (string malformed in new[]
        {
            "[]", "null", "{}", "{", "{\"id\":\" \"}",
            "{\"id\":\"id\",\"kind\":\"kind\",\"owner\":\"owner\",\"key\":\"key\",\"payload\":{}}",
            "{\"id\":\"id\",\"kind\":\"kind\",\"owner\":\"owner\",\"key\":\"key\",\"payload\":\"!\"}",
        })
        {
            await CompareAsync("POST", "/cohesion/v1/commands", malformed, credential, HttpStatusCode.BadRequest, null);
        }
        foreach (string field in new[] { "id", "kind", "owner", "key" })
        {
            string malformed = Envelope("kind").Replace($"\"{field}\":\"{field}\"", $"\"{field}\":\" \"", StringComparison.Ordinal);
            await CompareAsync("POST", "/cohesion/v1/commands", malformed, credential, HttpStatusCode.BadRequest, null);
        }
        await CompareAsync("POST", "/cohesion/v1/stop", null, credential, HttpStatusCode.Accepted, null);

        async Task CompareAsync(string method, string path, string? body, string? token, HttpStatusCode expected, string? allow)
        {
            using HttpResponseMessage left = await SendAsync(hostedClient, method, path, body, token, timeout.Token);
            using HttpResponseMessage right = await SendAsync(featureClient, method, path, body, token, timeout.Token);
            left.StatusCode.ShouldBe(expected, $"Web.Hosting: {method} {path}");
            right.StatusCode.ShouldBe(left.StatusCode, $"Web.ControlPlane: {method} {path}");
            (await right.Content.ReadAsStringAsync(timeout.Token)).ShouldBe(await left.Content.ReadAsStringAsync(timeout.Token));
            right.Content.Headers.ContentType?.ToString().ShouldBe(left.Content.Headers.ContentType?.ToString());
            right.Headers.WwwAuthenticate.ToString().ShouldBe(left.Headers.WwwAuthenticate.ToString());
            string.Join(", ", right.Content.Headers.Allow).ShouldBe(allow ?? string.Empty);
            string.Join(", ", left.Content.Headers.Allow).ShouldBe(allow ?? string.Empty);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.ControlPlane] - Readiness: gates owning host startup and preserves health data")]
    public async Task Readiness_BeforeOwnerStarts_ShouldBeUnavailable()
    {
        await using var factory = new WebApplicationTestFactory();
        IResourceControlPlane plane = ResourceControlPlane.Create();
        var data = new Dictionary<string, object>
        {
            ["null"] = null!, ["string"] = "value", ["bool"] = true, ["int"] = 1,
            ["long"] = 4_000_000_000L, ["double"] = 1.25D, ["float"] = 2.5F, ["decimal"] = 3.75M,
        };
        plane.AddHealthContributor(new Contributor("z-last", HealthContribution.Healthy("ok", data)));
        plane.AddHealthContributor(new Contributor("a-first", HealthContribution.Degraded("waiting")));
        bool ready = false;
        ((IWebApplicationPipelineBuilder)factory.Application).UseResourceControlPlane(plane, new ResourceContext(), () => ready);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage starting = await client.GetAsync("/readyz", CancellationToken.None);
        starting.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await starting.Content.ReadAsStringAsync()).ShouldContain("cohesion.host", Case.Sensitive);
        ready = true;
        using HttpResponseMessage started = await client.GetAsync("/readyz", CancellationToken.None);
        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        started.Headers.CacheControl!.NoStore.ShouldBeTrue();
        using JsonDocument document = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().ShouldBe("Degraded");
        JsonElement values = document.RootElement.GetProperty("contributions").GetProperty("z-last").GetProperty("data");
        values.GetProperty("null").ValueKind.ShouldBe(JsonValueKind.Null);
        values.GetProperty("string").GetString().ShouldBe("value");
        values.GetProperty("bool").GetBoolean().ShouldBeTrue();
        values.GetProperty("int").GetInt32().ShouldBe(1);
        values.GetProperty("long").GetInt64().ShouldBe(4_000_000_000L);
        values.GetProperty("double").GetDouble().ShouldBe(1.25D);
        values.GetProperty("float").GetSingle().ShouldBe(2.5F);
        values.GetProperty("decimal").GetDecimal().ShouldBe(3.75M);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ControlPlane] - Bootstrap: verifies signature, identity, audience, lifetime, and rotation")]
    public async Task Authorization_WithManagedContext_ShouldVerifyJwtClaims()
    {
        using var identity = new TestBootstrapIdentity("tests", "gateway");
        using var otherKey = new TestBootstrapIdentity("tests", "gateway");
        using var otherIssuer = new TestBootstrapIdentity("other", "gateway");
        string original = identity.Issue("resource");
        ResourceContext resource = CreateContext(identity, original);
        await using var factory = new WebApplicationTestFactory();
        ((IWebApplicationPipelineBuilder)factory.Application).UseResourceControlPlane(ResourceControlPlane.Create(), resource, () => true);
        using HttpClient client = factory.CreateClient();
        foreach ((string? token, HttpStatusCode expected) in new (string?, HttpStatusCode)[]
        {
            (null, HttpStatusCode.Unauthorized), ("invalid", HttpStatusCode.Unauthorized),
            (otherKey.Issue("resource"), HttpStatusCode.Unauthorized),
            (otherIssuer.Issue("resource"), HttpStatusCode.Unauthorized),
            (identity.Issue("other-resource"), HttpStatusCode.Forbidden),
            (identity.Issue("resource", TimeSpan.FromHours(25)), HttpStatusCode.Unauthorized),
            (identity.Issue("resource", TimeSpan.FromHours(1), DateTimeOffset.UtcNow.AddHours(-2)), HttpStatusCode.Unauthorized),
            (identity.Issue("resource", includeId: false), HttpStatusCode.Unauthorized),
            (identity.Issue("resource"), HttpStatusCode.OK),
        })
        {
            using HttpResponseMessage response = await SendAsync(client, "GET", "/cohesion/v1/endpoints", null, token, CancellationToken.None);
            response.StatusCode.ShouldBe(expected);
            if (expected is HttpStatusCode.Unauthorized)
            {
                response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
            }
        }
        using HttpResponseMessage bare = await client.GetAsync("/readyz", CancellationToken.None);
        bare.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ControlPlane] - Commands: lists applied envelopes and returns conflict refusals")]
    public async Task Commands_WithHandler_ShouldExposeStateAndRejectConflictingOwnership()
    {
        await using var factory = new WebApplicationTestFactory();
        IResourceControlPlane plane = ResourceControlPlane.Create(["kind"]);
        plane.RegisterCommandHandler(new EchoHandler());
        ((IWebApplicationPipelineBuilder)factory.Application).UseResourceControlPlane(plane, new ResourceContext(), () => true);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage applied = await SendAsync(client, "POST", "/cohesion/v1/commands", Envelope("kind"), null, CancellationToken.None);
        applied.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument state = JsonDocument.Parse(await client.GetStringAsync("/cohesion/v1/commands", CancellationToken.None));
        JsonElement command = state.RootElement.GetProperty("commands")[0];
        command.GetProperty("id").GetString().ShouldBe("id");
        command.GetProperty("kind").GetString().ShouldBe("kind");
        command.GetProperty("owner").GetString().ShouldBe("owner");
        command.GetProperty("key").GetString().ShouldBe("key");
        command.GetProperty("status").GetString().ShouldBe("Applied");
        using HttpResponseMessage refused = await SendAsync(client, "POST", "/cohesion/v1/commands",
            Envelope("kind").Replace("\"owner\":\"owner\"", "\"owner\":\"other\"", StringComparison.Ordinal), null, CancellationToken.None);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using JsonDocument rejection = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        rejection.RootElement.GetProperty("status").GetString().ShouldBe("Rejected");
        rejection.RootElement.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
        using HttpResponseMessage deleted = await SendAsync(client, "DELETE", "/cohesion/v1/commands", Envelope("kind"), null, CancellationToken.None);
        deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
        plane.Commands.ShouldBeEmpty();
    }

    private static string Envelope(string kind) => $"{{\"id\":\"id\",\"kind\":\"{kind}\",\"owner\":\"owner\",\"key\":\"key\"}}";

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string? body, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await client.SendAsync(request, cancellationToken);
    }

    private static ResourceContext CreateContext(TestBootstrapIdentity identity, string token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new ResourceContext("tests", "resource", "Testing", "gateway", contentRootPath: null,
            endpoints: new Dictionary<string, Uri> { ["http"] = Uri.CreateEndpoint("http", "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port) },
            mounts: new Dictionary<string, ResourceMount> { ["fixture"] = ResourceMount.FromBytes("mounted"u8) },
            settings: new Dictionary<string, string> { ["Test:Marker"] = "alpha" },
            references: new Dictionary<string, Uri> { ["inventory-database:db"] = Uri.CreateEndpoint("cohesion-db", "127.0.0.1", 15740) },
            bootstrapCredential: Encoding.UTF8.GetBytes(token), applicationTrustKey: identity.PublicKey, ambientValues: null);
    }

    private sealed class Contributor(string name, HealthContribution contribution) : IHealthContributor
    {
        public string Name => name;
        public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(contribution);
    }

    private sealed class EchoHandler : IResourceCommandHandler
    {
        public string Kind => "kind";
        public ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default) => ValueTask.FromResult(command.Payload);
        public ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
    }
}
