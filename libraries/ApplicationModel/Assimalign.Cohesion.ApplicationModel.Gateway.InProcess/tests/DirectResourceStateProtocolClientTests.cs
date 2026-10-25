using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;
using Runtime = Assimalign.Cohesion.Hosting.Resources;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

public sealed class DirectResourceStateProtocolClientTests
{
    [Fact(DisplayName = "Cohesion Test [Gateway.InProcess] - Runtime state: a restarted invocation refuses delivery to the former process")]
    public async Task ApplyAsync_AfterInvocationChanges_ShouldRequireNewHandshake()
    {
        Runtime.ResourceStateDefinition definition = Runtime.ResourceStateDefinition.Create("test.settings", new Uri("urn:test:settings:v1"),
            Json("{\"const\":\"configuration\"}"), Json("{\"type\":\"object\"}"), Runtime.ResourceStateScope.EveryInstance, [Runtime.ResourceStateUse.Management]);
        Runtime.ResourceControlPlane current = CreatePlane(definition);
        var protocol = new DirectResourceStateProtocolClient((_, _) => current);
        var target = new ResourceManagementTarget("api", "first", new Uri("cohesion-inprocess://runtime/"));
        var address = new ResourceStateAddress("app", "api", "test.settings", "configuration");
        ResourceRuntimeDescription first = await protocol.DescribeAsync(target, "app", "api");
        string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("{\"exists\":true,\"value\":{\"limit\":5}}")));
        var delivery = new ResourceStateDelivery(address, Guid.NewGuid(), first.ProcessStartId, 1, digest, definition.ContractDigest, 1,
            new ResourceDesiredValue(true, Json("{\"limit\":5}")));
        (await protocol.ApplyAsync(target, delivery)).ErrorCode.ShouldBeNull();
        (await protocol.ReadAsync(target, address, definition.ContractDigest)).Revision.ShouldBe(1);
        current = CreatePlane(definition);
        (await protocol.ApplyAsync(target, delivery)).ErrorCode.ShouldNotBeNull();
        ResourceRuntimeDescription restarted = await protocol.DescribeAsync(target, "app", "api");
        restarted.ProcessStartId.ShouldNotBe(first.ProcessStartId);
        (await protocol.ApplyAsync(target, delivery with { ProcessStartId = restarted.ProcessStartId })).ErrorCode.ShouldBeNull();
        (await protocol.ReadAsync(target, address, definition.ContractDigest)).ValueDigest.ShouldBe(digest);
    }

    [Fact(DisplayName = "Cohesion Test [Gateway.InProcess] - Runtime state: unavailable invocations fail without remote fallback")]
    public async Task DescribeAsync_WithMissingInvocation_ShouldFail()
    {
        var protocol = new DirectResourceStateProtocolClient((_, _) => null);
        await Should.ThrowAsync<IOException>(async () => await protocol.DescribeAsync(
            new ResourceManagementTarget("api", "missing", new Uri("cohesion-inprocess://runtime/")), "app", "api"));
    }

    private static Runtime.ResourceControlPlane CreatePlane(Runtime.ResourceStateDefinition definition)
    {
        Runtime.ResourceControlPlane plane = Runtime.ResourceControlPlane.Create();
        plane.ConfigureRuntimeManagement(new Runtime.ResourceRuntimeManagement { Endpoint = "http" });
        plane.RegisterStateHandler(new SettingsHandler(definition));
        return plane;
    }

    private static JsonElement Json(string value)
    {
        using JsonDocument document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class SettingsHandler(Runtime.ResourceStateDefinition definition) : Runtime.IResourceStateHandler
    {
        private Runtime.ResourceStateObservation _state = new(new Runtime.ResourceStateValue(false, Json("null")), 0, string.Empty, default);
        public Runtime.ResourceStateDefinition Definition => definition;
        public ValueTask<Runtime.ResourceStateValidation> ValidateAsync(Runtime.ResourceStateKey key, Runtime.ResourceStateValue desired, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new Runtime.ResourceStateValidation(true));
        public ValueTask<Runtime.ResourceStateObservation> ReadAsync(Runtime.ResourceStateKey key, CancellationToken cancellationToken = default) => ValueTask.FromResult(_state);
        public ValueTask<Runtime.ResourceStateApplyResult> ApplyAsync(Runtime.ResourceStateApplyContext context, Runtime.ResourceStateKey key, Runtime.ResourceStateValue desired, CancellationToken cancellationToken = default)
        {
            _state = new Runtime.ResourceStateObservation(desired, context.Revision, context.ValueDigest, context.TargetProcess);
            return ValueTask.FromResult(new Runtime.ResourceStateApplyResult(Runtime.ResourceStateApplyStatus.Applied, context.Revision));
        }
    }
}
