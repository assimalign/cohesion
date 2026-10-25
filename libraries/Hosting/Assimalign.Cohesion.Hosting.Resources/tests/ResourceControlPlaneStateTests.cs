using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources.Internal;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public sealed partial class ResourceControlPlaneStateTests
{
    [Theory(DisplayName = "Cohesion Test [Hosting.Resources] - Startup: malformed handler metadata does not alter the discovered catalog")]
    [InlineData("definition")]
    [InlineData("initial-state")]
    [InlineData("initial-entry")]
    public void RegisterStateHandler_WithMissingMetadata_ShouldRejectWithoutCatalogChanges(string missing)
    {
        ResourceStateDefinition definition = CreateDefinition();
        var handler = new VersionedStateHandler(definition);
        if (missing == "definition") { handler.Definition = null!; }
        else if (missing == "initial-state") { handler.InitialState = null!; }
        else { handler.InitialState = [null!]; }
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.ConfigureRuntimeManagement(new ResourceRuntimeManagement { Endpoint = "http" });

        Should.Throw<InvalidOperationException>(() => plane.RegisterStateHandler(handler));

        plane.RuntimeManifest!.States.ShouldBeEmpty();
        plane.RuntimeManifest.InitialState.ShouldBeEmpty();
        plane.RegisterStateHandler(new VersionedStateHandler(definition));
        Should.NotThrow(plane.ValidateStateHandlers);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Startup: handlers publish contracts and configured baselines without build declarations")]
    public void RegisterStateHandler_WithBindingOnly_ShouldDiscoverAndFreezeStartupState()
    {
        // Arrange
        ResourceStateDefinition definition = CreateDefinition();
        var handler = new VersionedStateHandler(definition)
        {
            InitialState = [new ResourceStateDeclaration("test.settings", "configuration", new ResourceStateValue(true, Json("{\"limit\":5}")))]
        };
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.ConfigureRuntimeManagement(new ResourceRuntimeManagement { Endpoint = "http" });

        // Act
        plane.RegisterStateHandler(handler);
        plane.ValidateStateHandlers();
        handler.InitialState = [];
        handler.Definition = definition with { PayloadSchema = Json("{}") };

        // Assert
        plane.RuntimeManifest!.States.Count.ShouldBe(1);
        plane.RuntimeManifest.InitialState[0].Desired.Value.GetProperty("limit").GetInt32().ShouldBe(5);
        plane.RuntimeManifest.States[0].ContractDigest.ShouldBe(definition.ContractDigest);
        Should.Throw<InvalidOperationException>(() => plane.RegisterStateHandler(new VersionedStateHandler(definition)));
    }

    [Theory(DisplayName = "Cohesion Test [Hosting.Resources] - Runtime manifest: invalid management paths fail before deployment")]
    [InlineData("relative")]
    [InlineData("/state?caller=untrusted")]
    [InlineData("/state#fragment")]
    [InlineData("/state\\alternate")]
    [InlineData("/state path")]
    [InlineData("/state\npath")]
    public void ManagementPath_WithInvalidGrammar_ShouldReject(string path)
    {
        ResourceManagementPath.TryCreate(path, out _).ShouldBeFalse();
        ResourceStateWire.IsValidManagementPath(path).ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => new ResourceRuntimeManifest { Management = new ResourceRuntimeManagement { Endpoint = "http", Path = default } }.Validate());
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - State: advertised digest must describe the registered handler")]
    public void RegisterStateHandler_WithChangedSchemaAndOldDigest_ShouldReject()
    {
        ResourceStateDefinition definition = CreateDefinition();
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.ConfigureRuntimeManagement(new ResourceRuntimeManagement { Endpoint = "http" });
        var changed = definition with { PayloadSchema = Json("{\"type\":\"string\"}") };
        Should.Throw<InvalidDataException>(() => plane.RegisterStateHandler(new VersionedStateHandler(changed)));
        plane.RegisterStateHandler(new VersionedStateHandler(definition));
        Should.NotThrow(plane.ValidateStateHandlers);
        plane.RuntimeManagement.ShouldBeSameAs(plane.RuntimeManagement);
        ResourceRuntimeManifest detached = plane.RuntimeManifest!;
        detached.States[0].Uses[0] = ResourceStateUse.Management;
        plane.RuntimeManifest!.States[0].Uses.ShouldBe(definition.Uses);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - State: frozen schema survives handler metadata changes")]
    public async Task ValidateStateAsync_AfterHandlerMetadataChanges_ShouldEnforceFrozenCatalogAsync()
    {
        ResourceStateDefinition definition = CreateDefinition();
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.ConfigureRuntimeManagement(new ResourceRuntimeManagement { Endpoint = "http" });
        var handler = new VersionedStateHandler(definition);
        plane.RegisterStateHandler(handler);
        handler.Definition = definition with { PayloadSchema = Json("{}") };
        (await plane.ValidateStateAsync("test.settings", "configuration", new ResourceStateValue(true, Json("{\"limit\":-1}")))).IsValid.ShouldBeFalse();
        (await plane.ValidateStateAsync("test.settings", "configuration", new ResourceStateValue(true, Json("{\"limit\":5}")))).IsValid.ShouldBeTrue();
        await Should.ThrowAsync<ArgumentException>(async () => await plane.ReadStateAsync("test.settings", "other"));
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - State: process targets and actual revisions gate traffic admission")]
    public async Task ApplyStateAsync_WithBootstrapRetryAndStaleProcess_ShouldVerifyActualStateAsync()
    {
        ResourceStateDefinition definition = CreateDefinition();
        var desired = new ResourceStateValue(true, Json("{\"limit\":5}"));
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.ConfigureRuntimeManagement(new ResourceRuntimeManagement { Endpoint = "http" });
        var handler = new VersionedStateHandler(definition)
        {
            InitialState = [new ResourceStateDeclaration("test.settings", "configuration", desired)]
        };
        plane.RegisterStateHandler(handler);
        plane.RequireStateBootstrap();
        plane.IsManagementReady.ShouldBeFalse();
        (await plane.CheckReadinessAsync()).Status.ShouldBe(HealthStatus.Unhealthy);
        (await plane.CheckLivenessAsync()).Status.ShouldBe(HealthStatus.Healthy);
        (await plane.AdmitStateAsync(new ResourceStateAdmission(plane.ProcessStartId, []))).ShouldBeFalse();
        string digest = ResourceStateWire.ComputeValueDigest(true, desired.Value);
        var context = new ResourceStateApplyContext(Guid.NewGuid(), Guid.NewGuid(), 1, digest, 1);
        (await plane.ApplyStateAsync(context, "test.settings", "configuration", desired)).Status.ShouldBe(ResourceStateApplyStatus.Rejected);
        context = context with { TargetProcess = plane.ProcessStartId };
        (await plane.ApplyStateAsync(context, "test.settings", "configuration", desired)).Status.ShouldBe(ResourceStateApplyStatus.Applied);
        (await plane.ApplyStateAsync(context, "test.settings", "configuration", desired)).Status.ShouldBe(ResourceStateApplyStatus.AlreadyCurrent);
        handler.Writes.ShouldBe(1);
        (await plane.AdmitStateAsync(new ResourceStateAdmission(plane.ProcessStartId, [new ResourceStateAdmissionSlot("test.settings", "configuration", 2, digest)]))).ShouldBeFalse();
        (await plane.AdmitStateAsync(new ResourceStateAdmission(plane.ProcessStartId, [new ResourceStateAdmissionSlot("test.settings", "configuration", 1, digest)]))).ShouldBeTrue();
        plane.IsManagementReady.ShouldBeTrue();
        (await plane.CheckReadinessAsync()).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - State: unsupported schema constraints fail at declaration")]
    public void Validate_WithUnsupportedSchemaKeyword_ShouldReject()
    {
        ResourceStateDefinition definition = ResourceStateDefinition.Create("test.settings", new Uri("urn:test:settings:v1"), Json("{}"), Json("{\"pattern\":\".*\"}"), ResourceStateScope.EveryInstance, [ResourceStateUse.Management]);
        Should.Throw<InvalidDataException>(() => new ResourceRuntimeManifest { Management = new ResourceRuntimeManagement { Endpoint = "http" }, States = [definition] }.Validate());
        ResourceStateKey.TryCreate("..", out _).ShouldBeFalse();
    }

    private static ResourceStateDefinition CreateDefinition() => ResourceStateDefinition.Create("test.settings", new Uri("urn:test:settings:v1"), Json("{\"const\":\"configuration\"}"),
        Json("{\"type\":\"object\",\"required\":[\"limit\"],\"properties\":{\"limit\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":10}},\"additionalProperties\":false}"),
        ResourceStateScope.EveryInstance, [ResourceStateUse.Orchestration, ResourceStateUse.Management]);

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
