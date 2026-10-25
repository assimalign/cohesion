using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

public sealed partial class GatewayControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [Gateway.ControlPlane] - State: explicit grants, conditions and durable idempotent receipts")]
    public async Task State_WithAuthorizedCallerRetryAndMissingGrant_ShouldEnforceManagementBoundaryAsync()
    {
        string root = CreateTestDirectory();
        using var timeout = new CancellationTokenSource(_testTimeout);
        var protocol = new StateTestProtocol();
        var gateway = new TestGateway(new ApplicationGatewayOptions
        {
            ExportDirectory = root,
            ControlPlane = GatewayControlPlane.CreateFactory(options => options.MetadataDirectory = root),
            StateProtocolClient = protocol,
        });
        IApplicationBuilder builder = Application.CreateBuilder("appa", ["--environment", AppEnvironment.Keys.Local]).UseGateway(gateway);
        builder.Providers.Callers.Add(new RecordingCallerAuthenticator("management", new ConcurrentQueue<string>(), request =>
        {
            ApplicationCaller? caller = request.Credential switch
            {
                "manage" => new ApplicationCaller(null, "example-issuer/operator", ApplicationCallerKind.Operator, [], ["api/state/web.cors/read", "api/state/web.cors/write"]),
                "commands" => new ApplicationCaller("appa", "operator", ApplicationCallerKind.Peer, ["*"], []),
                "developer" => new ApplicationCaller("appa", "operator", ApplicationCallerKind.Developer, [], ["*"]),
                _ => null
            };
            return caller is null ? default : new ApplicationCallerResult(ApplicationCallerStatus.Authenticated, caller, null);
        }));
        const string contract = "{\"keySchema\":{\"const\":\"configuration\"},\"name\":\"web.cors\",\"payloadSchema\":{\"type\":\"object\"},\"payloadSchemaId\":\"urn:test:cors:v1\",\"scope\":\"everyInstance\",\"supportsAbsence\":false,\"uses\":[\"management\"]}";
        string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contract)));
        using JsonDocument runtime = JsonDocument.Parse("{\"schema\":\"cohesion/resource-runtime/v1\",\"management\":{\"endpoint\":\"http\",\"path\":\"/cohesion/v1\",\"protocol\":\"cohesion/resource-state/v1\"},\"states\":[" + contract[..^1] + ",\"contractDigest\":\"" + digest + "\"}],\"initialState\":[]}");
        protocol.Runtime = runtime.RootElement.Clone();
        using JsonDocument binding = JsonDocument.Parse("{\"schema\":\"cohesion/resource-runtime/v1\",\"management\":" + runtime.RootElement.GetProperty("management").GetRawText() + "}");
        builder.AddResource(CreateManifest("appa", "api", "test", 43110) with { Runtime = binding.RootElement.Clone() });
        IApplicationModel model = builder.Build().Model;
        IApplicationGateway control = gateway;
        try
        {
            await control.StartAsync(model, timeout.Token);
            (Uri address, _) = ReadMetadata(root, "appa");
            using var client = new HttpClient { BaseAddress = address };
            using HttpResponseMessage legacyCommands = await SendAsync(client, HttpMethod.Get, new Uri(address, "/cohesion/v1/resources/api/commands"), "manage", null, timeout.Token);
            legacyCommands.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            const string route = "/cohesion/v2/resources/api/state/web.cors/configuration";
            Guid requestId = Guid.NewGuid();
            using HttpResponseMessage commandGrant = await PutStateAsync(client, route, "commands", requestId, "*", timeout.Token);
            commandGrant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            using HttpResponseMessage developer = await PutStateAsync(client, route, "developer", requestId, "*", timeout.Token);
            developer.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            using HttpResponseMessage noCondition = await PutStateAsync(client, route, "manage", requestId, null, timeout.Token);
            ((int)noCondition.StatusCode).ShouldBe(428);
            using HttpResponseMessage first = await PutStateAsync(client, route, "manage", requestId, "*", timeout.Token);
            first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            first.Headers.ETag!.Tag.ShouldBe("\"1\"");
            Uri operation = first.Headers.Location!;
            using HttpResponseMessage retry = await PutStateAsync(client, route, "manage", requestId, "*", timeout.Token);
            retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            retry.Headers.Location.ShouldBe(operation);
            using HttpResponseMessage conflict = await PutStateAsync(client, route, "manage", Guid.NewGuid(), "*", timeout.Token);
            conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            using HttpResponseMessage observed = await SendAsync(client, HttpMethod.Get, new Uri(address, operation), "manage", null, timeout.Token);
            observed.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await observed.Content.ReadAsStringAsync(timeout.Token)).ShouldContain("https://portal.example");
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    private static async Task<HttpResponseMessage> PutStateAsync(HttpClient client, string route, string credential, Guid id, string? condition, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Add("Idempotency-Key", id.ToString("D"));
        if (condition is not null) { request.Headers.TryAddWithoutValidation("If-None-Match", condition); }
        request.Content = Json("{\"exists\":true,\"value\":{\"origin\":\"https://portal.example\"}}");
        return await client.SendAsync(request, cancellationToken);
    }

    private sealed class StateTestProtocol : IResourceStateProtocolClient
    {
        private readonly object _gate = new();
        private readonly Guid _process = Guid.NewGuid();
        private ResourceRuntimeStateObservation _observed;

        public StateTestProtocol()
        {
            using JsonDocument empty = JsonDocument.Parse("{}");
            _observed = new ResourceRuntimeStateObservation(new ResourceDesiredValue(true, empty.RootElement.Clone()), 0, string.Empty, _process);
        }

        public JsonElement Runtime { get; set; }

        public ValueTask<ResourceRuntimeDescription> DescribeAsync(ResourceManagementTarget target, ApplicationName application, ResourceName resource,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ResourceRuntimeDescription(_process, Runtime.Clone()));
        }

        public ValueTask<bool> AdmitAsync(ResourceManagementTarget target, ApplicationName application, ResourceName resource, Guid processStartId,
            IReadOnlyList<ResourceBootstrapState> states, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                foreach (ResourceBootstrapState state in states)
                {
                    if (state.Revision != _observed.Revision || state.ValueDigest != _observed.ValueDigest) { return ValueTask.FromResult(false); }
                }
                return ValueTask.FromResult(processStartId == _process);
            }
        }

        public ValueTask<ResourceRuntimeStateObservation> ReadAsync(ResourceManagementTarget target, ResourceStateAddress address, string contractDigest,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) { return ValueTask.FromResult(_observed); }
        }

        public ValueTask<ResourceStateDeliveryResult> ApplyAsync(ResourceManagementTarget target, ResourceStateDelivery delivery,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (delivery.ProcessStartId != _process || delivery.Revision < _observed.Revision)
                {
                    return ValueTask.FromResult(new ResourceStateDeliveryResult(_observed.Revision, "stale-process-or-revision"));
                }
                _observed = new ResourceRuntimeStateObservation(delivery.Desired, delivery.Revision, delivery.ValueDigest, _process);
                return ValueTask.FromResult(new ResourceStateDeliveryResult(delivery.Revision));
            }
        }
    }
}
