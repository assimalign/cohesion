using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;
using Shouldly;
using Xunit;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

public sealed partial class GatewayControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Management: each v2 route reaches its provider operation only under a matching grant")]
    public async Task Management_Routes_ShouldRequireGrantsAndReachTheProvider()
    {
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var provider = new RecordingManagementProvider();
        (string reader, TrustedIssuer readerIssuer) = CreateControlPlaneToken("readers", DateTimeOffset.UtcNow, TimeSpan.FromHours(1), allowCommands: false);
        (string writer, TrustedIssuer writerIssuer) = CreateControlPlaneToken("writers", DateTimeOffset.UtcNow, TimeSpan.FromHours(1), allowCommands: false);
        (string none, TrustedIssuer noneIssuer) = CreateControlPlaneToken("nobody", DateTimeOffset.UtcNow, TimeSpan.FromHours(1), allowCommands: false);
        provider.Issuers.Add(Grant(readerIssuer, ResourceManagementOperations.ReadConfiguration, ResourceManagementOperations.ReadPlatform + ":kubernetes/hpa"));
        provider.Issuers.Add(Grant(writerIssuer, ResourceManagementOperations.WriteConfiguration + ":Web:Cors", ResourceManagementOperations.InvokeActions + ":echo",
            ResourceManagementOperations.WriteConfiguration + ":status"));
        provider.Issuers.Add(noneIssuer);
        await using ManagementServer server = await ManagementServer.StartAsync(provider, cancellation.Token);
        using var client = new HttpClient();

        // Without a grant every management route is refused before the provider sees it.
        using (HttpResponseMessage refused = await SendAsync(client, HttpMethod.Get, server.Uri("configuration"), none, null, cancellation.Token))
        {
            refused.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
            (await refused.Content.ReadAsStringAsync(cancellation.Token)).ShouldContain("\"forbidden\"", Case.Sensitive);
        }

        using (HttpResponseMessage read = await SendAsync(client, HttpMethod.Get, server.Uri("configuration"), reader, null, cancellation.Token))
        {
            read.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            read.Headers.ETag!.Tag.ShouldBe("\"v1\"");
            (await read.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("""{"operation":"configuration"}""");
        }

        (await Status(client, HttpMethod.Get, server.Uri("configuration/status"), reader, null, cancellation.Token)).ShouldBe(NetHttpStatusCode.OK);
        (await Status(client, HttpMethod.Get, server.Uri("instances"), reader, null, cancellation.Token)).ShouldBe(NetHttpStatusCode.OK);
        (await Status(client, HttpMethod.Get, server.Uri("platform/kubernetes/hpa"), reader, null, cancellation.Token)).ShouldBe(NetHttpStatusCode.OK);
        (await Status(client, HttpMethod.Patch, server.Uri("configuration/Web:Cors"), reader, Json("{}"), cancellation.Token)).ShouldBe(NetHttpStatusCode.Forbidden);

        // A section-scoped grant covers its section only; a percent-encoded colon names the same section.
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, server.Uri("configuration/Web%3ACors")) { Content = Json("""{"origins":[]}""") })
        {
            patch.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", writer);
            patch.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\""));
            using HttpResponseMessage response = await client.SendAsync(patch, cancellation.Token);
            response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        }

        (await Status(client, HttpMethod.Patch, server.Uri("configuration/Web:Other"), writer, Json("{}"), cancellation.Token)).ShouldBe(NetHttpStatusCode.Forbidden);
        (await Status(client, HttpMethod.Put, server.Uri("configuration/Web:Cors"), writer, Json("{}"), cancellation.Token)).ShouldBe(NetHttpStatusCode.OK);
        (await Status(client, HttpMethod.Delete, server.Uri("configuration/Web:Cors"), writer, null, cancellation.Token)).ShouldBe(NetHttpStatusCode.OK);
        // GET configuration/status is the status route; any other method names a section called 'status'.
        (await Status(client, HttpMethod.Patch, server.Uri("configuration/status"), writer, Json("{}"), cancellation.Token)).ShouldBe(NetHttpStatusCode.OK);
        (await Status(client, HttpMethod.Post, server.Uri("actions/echo"), writer, Json("""{"text":"hi"}"""), cancellation.Token)).ShouldBe(NetHttpStatusCode.OK);
        (await Status(client, HttpMethod.Post, server.Uri("actions/flush"), writer, Json("{}"), cancellation.Token)).ShouldBe(NetHttpStatusCode.Forbidden);

        provider.Calls.ToArray().ShouldBe(
        [
            "configuration api",
            "status api",
            "instances api",
            "platform-read api kubernetes/hpa",
            "override api Web:Cors Merge v1 {\"origins\":[]}",
            "override api Web:Cors Replace - {}",
            "override api Web:Cors Remove - ",
            "override api status Merge - {}",
            "action api echo {\"text\":\"hi\"}",
        ]);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Management: provider outcomes map to their HTTP statuses")]
    [InlineData(ResourceManagementStatus.InvalidConfiguration, NetHttpStatusCode.BadRequest)]
    [InlineData(ResourceManagementStatus.UnknownSection, NetHttpStatusCode.NotFound)]
    [InlineData(ResourceManagementStatus.DeploymentOnly, NetHttpStatusCode.Conflict)]
    [InlineData(ResourceManagementStatus.NotOverridable, NetHttpStatusCode.Conflict)]
    [InlineData(ResourceManagementStatus.ClaimConflict, NetHttpStatusCode.Conflict)]
    [InlineData(ResourceManagementStatus.VersionMoved, NetHttpStatusCode.PreconditionFailed)]
    [InlineData(ResourceManagementStatus.RecordsUnsupported, NetHttpStatusCode.NotImplemented)]
    [InlineData(ResourceManagementStatus.Unavailable, NetHttpStatusCode.ServiceUnavailable)]
    public async Task Management_ProviderRefusal_ShouldMapToItsHttpStatus(ResourceManagementStatus status, NetHttpStatusCode expected)
    {
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var provider = new RecordingManagementProvider
        {
            Result = ResourceManagementResult.Refuse(status, "refused for the test"),
        };
        (string token, TrustedIssuer issuer) = CreateControlPlaneToken("admins", DateTimeOffset.UtcNow, TimeSpan.FromHours(1), allowCommands: false);
        provider.Issuers.Add(Grant(issuer, ResourceManagementOperations.All));
        await using ManagementServer server = await ManagementServer.StartAsync(provider, cancellation.Token);
        using var client = new HttpClient();

        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Patch, server.Uri("configuration/Web:Cors"), token, Json("{}"), cancellation.Token);

        response.StatusCode.ShouldBe(expected);
        (await response.Content.ReadAsStringAsync(cancellation.Token))
            .ShouldContain("\"" + ResourceManagementResult.GetErrorCode(status) + "\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Management: a caller writes claims only for its own application")]
    public async Task Management_Claims_ShouldBelongToTheCallerApplication()
    {
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var provider = new RecordingManagementProvider();
        (string peer, TrustedIssuer issuer) = CreateControlPlaneToken("appb", DateTimeOffset.UtcNow, TimeSpan.FromHours(1), allowCommands: true);
        provider.Issuers.Add(Grant(issuer, ResourceManagementOperations.WriteClaims));
        await using ManagementServer server = await ManagementServer.StartAsync(provider, cancellation.Token);
        using var client = new HttpClient();

        HttpStatusCodeAndBody own = await SendForStatusAsync(client, server.Uri("claims/appb"), peer, """{"Orders:Schema":{"invoices":{}}}""", cancellation.Token);
        HttpStatusCodeAndBody foreign = await SendForStatusAsync(client, server.Uri("claims/appc"), peer, "{}", cancellation.Token);

        own.Status.ShouldBe(NetHttpStatusCode.OK);
        foreign.Status.ShouldBe(NetHttpStatusCode.Forbidden);
        provider.Calls.ToArray().ShouldBe(["claims api appb {\"Orders:Schema\":{\"invoices\":{}}}"]);
    }

    private static TrustedIssuer Grant(TrustedIssuer issuer, params string[] operations) =>
        new(issuer.Issuer, issuer.PublicKey, issuer.AllowedCommandKinds, operations);

    private static async Task<NetHttpStatusCode> Status(
        HttpClient client, HttpMethod method, Uri address, string token, HttpContent? content, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(client, method, address, token, content, cancellationToken);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCodeAndBody> SendForStatusAsync(
        HttpClient client, Uri address, string token, string body, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Put, address, token, Json(body), cancellationToken);
        return new HttpStatusCodeAndBody(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private readonly record struct HttpStatusCodeAndBody(NetHttpStatusCode Status, string Body);

    private sealed class ManagementServer : IAsyncDisposable
    {
        private readonly GatewayControlPlaneServer _server;

        private ManagementServer(GatewayControlPlaneServer server)
        {
            _server = server;
        }

        public static async Task<ManagementServer> StartAsync(RecordingManagementProvider provider, CancellationToken cancellationToken)
        {
            IApplicationModel model = BuildModel(new TestGateway(new ApplicationGatewayOptions()), "appa", includeUnsupportedResource: false);
            var server = new GatewayControlPlaneServer("appa", metadataDirectory: null, TimeProvider.System, Array.Empty<IResourceCommandDispatcher>());
            await server.StartAsync(new Uri("http://127.0.0.1:0"), model, new InMemoryResourceStateManager(), provider, cancellationToken);
            return new ManagementServer(server);
        }

        public Uri Uri(string route) => new(_server.Address, "/cohesion/v2/resources/api/" + route);

        public async ValueTask DisposeAsync() => await _server.StopAsync(CancellationToken.None);
    }

    // Records each management call in a compact form and answers with one configured result.
    private sealed class RecordingManagementProvider : ITrustedIssuerProvider, IResourceManagementProvider
    {
        public List<TrustedIssuer> Issuers { get; } = new();

        public ConcurrentQueue<string> Calls { get; } = new();

        public ResourceManagementResult Result { get; init; } =
            ResourceManagementResult.Create(ResourceManagementStatus.Ok, Encoding.UTF8.GetBytes("""{"operation":"configuration"}"""), "v1");

        public IReadOnlyList<TrustedIssuer> GetTrustedIssuers(ApplicationName application) => Issuers;

        public ValueTask<ResourceManagementResult> GetConfigurationAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default) =>
            Record($"configuration {resource}");

        public ValueTask<ResourceManagementResult> GetConfigurationStatusAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default) =>
            Record($"status {resource}");

        public ValueTask<ResourceManagementResult> ChangeOverrideAsync(ApplicationName application, ResourceName resource, ResourceSectionPath section,
            ResourceOverrideChangeKind kind, ReadOnlyMemory<byte> patch, string? expectedVersion, ApplicationCaller caller, CancellationToken cancellationToken = default) =>
            Record($"override {resource} {section} {kind} {expectedVersion ?? "-"} {Encoding.UTF8.GetString(patch.Span)}");

        public ValueTask<ResourceManagementResult> ReplaceClaimsAsync(ApplicationName application, ResourceName resource, ApplicationName owner,
            ReadOnlyMemory<byte> entries, string? expectedVersion, ApplicationCaller caller, CancellationToken cancellationToken = default) =>
            Record($"claims {resource} {owner} {Encoding.UTF8.GetString(entries.Span)}");

        public ValueTask<ResourceManagementResult> InvokeActionAsync(ApplicationName application, ResourceName resource, string action,
            ReadOnlyMemory<byte> input, ApplicationCaller caller, CancellationToken cancellationToken = default) =>
            Record($"action {resource} {action} {Encoding.UTF8.GetString(input.Span)}");

        public ValueTask<ResourceManagementResult> GetInstanceInventoryAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default) =>
            Record($"instances {resource}");

        public ValueTask<ResourceManagementResult> GetPlatformSurfaceAsync(ApplicationName application, ResourceName resource, ResourceExtensionKey surface,
            CancellationToken cancellationToken = default) =>
            Record($"platform-read {resource} {surface}");

        public ValueTask<ResourceManagementResult> ChangePlatformSurfaceAsync(ApplicationName application, ResourceName resource, ResourceExtensionKey surface,
            ReadOnlyMemory<byte> patch, string? expectedVersion, ApplicationCaller caller, CancellationToken cancellationToken = default) =>
            Record($"platform-write {resource} {surface}");

        private ValueTask<ResourceManagementResult> Record(string call)
        {
            Calls.Enqueue(call);
            return ValueTask.FromResult(Result);
        }
    }
}
