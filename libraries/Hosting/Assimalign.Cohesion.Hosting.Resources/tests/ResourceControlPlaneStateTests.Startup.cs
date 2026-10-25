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
    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Startup: exact configured revision-zero state can admit without a persisted declaration")]
    public async Task AdmitStateAsync_ConfiguredBaselineZero_ShouldVerifyFrozenActualValue()
    {
        var baseline = new ResourceStateValue(true, Json("{\"limit\":5}"));
        var handler = new VersionedStateHandler(CreateDefinition());
        ResourceControlPlane plane = StartupPlane((handler, baseline));
        handler.InitialState = [new ResourceStateDeclaration(handler.Definition.Name, "configuration", new ResourceStateValue(true, Json("{\"limit\":7}")))];

        bool admitted = await plane.AdmitStateAsync(new ResourceStateAdmission(plane.ProcessStartId,
            [new ResourceStateAdmissionSlot(handler.Definition.Name, "configuration", 0, ResourceStateWire.ComputeValueDigest(true, baseline.Value))]));

        admitted.ShouldBeTrue();
        plane.IsManagementReady.ShouldBeTrue();
        handler.Writes.ShouldBe(0);
        (await plane.ReadStateAsync(handler.Definition.Name, "configuration")).Revision.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Hosting.Resources] - Startup: revision zero cannot invent a baseline or hide managed state")]
    [InlineData("changed-actual")]
    [InlineData("wrong-digest")]
    [InlineData("managed-revision")]
    [InlineData("missing-baseline")]
    public async Task AdmitStateAsync_UnprovenBaselineZero_ShouldRemainClosed(string defect)
    {
        var baseline = new ResourceStateValue(true, Json("{\"limit\":5}"));
        var handler = new VersionedStateHandler(CreateDefinition());
        ResourceControlPlane plane;
        if (defect == "missing-baseline")
        {
            plane = ResourceControlPlane.Create();
            plane.ConfigureRuntimeManagement(new ResourceRuntimeManagement { Endpoint = "http" });
            handler.Observation = new ResourceStateObservation(baseline, 0, string.Empty, default);
            plane.RegisterStateHandler(handler);
            plane.ValidateStateHandlers();
            plane.RequireStateBootstrap();
        }
        else { plane = StartupPlane((handler, baseline)); }
        string digest = ResourceStateWire.ComputeValueDigest(true, baseline.Value);
        if (defect == "changed-actual")
        {
            handler.Observation = handler.Observation with { Value = new ResourceStateValue(true, Json("{\"limit\":7}")) };
            digest = ResourceStateWire.ComputeValueDigest(true, handler.Observation.Value.Value);
        }
        if (defect == "wrong-digest") { digest = "sha256:incorrect"; }
        if (defect == "managed-revision") { handler.Observation = handler.Observation with { Revision = 1, ValueDigest = digest }; }

        (await plane.AdmitStateAsync(new ResourceStateAdmission(plane.ProcessStartId,
            [new ResourceStateAdmissionSlot(handler.Definition.Name, "configuration", 0, digest)]))).ShouldBeFalse();

        plane.IsManagementReady.ShouldBeFalse();
        handler.Writes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Projection: valid empty desired state admits exact configured startup baselines")]
    public async Task Projection_EmptyValidGeneration_ShouldAdmitConfiguredBaselineOnly()
    {
        var handler = new VersionedStateHandler(CreateDefinition());
        ResourceControlPlane plane = StartupPlane((handler, new ResourceStateValue(true, Json("{\"limit\":5}"))));

        (await ResourceStateProjectionRunner.ApplyProjectionAsync(plane, Projection())).ShouldBeTrue();

        plane.IsManagementReady.ShouldBeTrue();
        handler.Writes.ShouldBe(0);
        handler.Observation.Revision.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Projection: added configured states do not block successfully applied durable overrides")]
    public async Task Projection_DurableOverrideAndNewBaseline_ShouldAdmitBothWithoutPersistingBaseline()
    {
        ResourceStateDefinition existing = CreateDefinition();
        ResourceStateDefinition added = OtherDefinition();
        var first = new VersionedStateHandler(existing);
        var second = new VersionedStateHandler(added);
        ResourceControlPlane plane = StartupPlane(
            (first, new ResourceStateValue(true, Json("{\"limit\":5}"))),
            (second, new ResourceStateValue(true, Json("{\"limit\":4}"))));
        var desired = new ResourceStateValue(true, Json("{\"limit\":7}"));

        (await ResourceStateProjectionRunner.ApplyProjectionAsync(plane, Projection((existing, 3L, desired)))).ShouldBeTrue();

        plane.IsManagementReady.ShouldBeTrue();
        first.Observation.Value.Value.GetProperty("limit").GetInt32().ShouldBe(7);
        first.Observation.Revision.ShouldBe(3);
        first.Writes.ShouldBe(1);
        second.Observation.Revision.ShouldBe(0);
        second.Writes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Projection: incompatible durable entries cannot be skipped to admit startup defaults")]
    public async Task Projection_UnknownDurableState_ShouldValidateEverythingBeforeApplying()
    {
        var handler = new VersionedStateHandler(CreateDefinition());
        ResourceControlPlane plane = StartupPlane((handler, new ResourceStateValue(true, Json("{\"limit\":5}"))));
        var desired = new ResourceStateValue(true, Json("{\"limit\":7}"));

        (await ResourceStateProjectionRunner.ApplyProjectionAsync(plane,
            Projection((handler.Definition, 3L, desired), (OtherDefinition(), 4L, desired)))).ShouldBeFalse();

        plane.IsManagementReady.ShouldBeFalse();
        handler.Writes.ShouldBe(0);
        handler.Observation.Revision.ShouldBe(0);
        handler.Observation.Value.Value.GetProperty("limit").GetInt32().ShouldBe(5);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Projection: failed durable delivery never falls back to a configured revision-zero baseline")]
    public async Task Projection_StaleDurableDelivery_ShouldRemainClosed()
    {
        var handler = new VersionedStateHandler(CreateDefinition());
        ResourceControlPlane plane = StartupPlane((handler, new ResourceStateValue(true, Json("{\"limit\":5}"))));
        handler.Observation = handler.Observation with { Revision = 8, ValueDigest = "newer" };
        var desired = new ResourceStateValue(true, Json("{\"limit\":7}"));

        (await ResourceStateProjectionRunner.ApplyProjectionAsync(plane,
            Projection((handler.Definition, 3L, desired)))).ShouldBeFalse();

        plane.IsManagementReady.ShouldBeFalse();
        handler.Observation.Revision.ShouldBe(8);
        handler.Writes.ShouldBe(0);
    }

    private static ResourceControlPlane StartupPlane(params (VersionedStateHandler Handler, ResourceStateValue Baseline)[] configured)
    {
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.ConfigureRuntimeManagement(new ResourceRuntimeManagement { Endpoint = "http" });
        foreach ((VersionedStateHandler handler, ResourceStateValue baseline) in configured)
        {
            handler.InitialState = [new ResourceStateDeclaration(handler.Definition.Name, "configuration", baseline)];
            handler.Observation = new ResourceStateObservation(baseline, 0, string.Empty, default);
            plane.RegisterStateHandler(handler);
        }
        plane.ValidateStateHandlers();
        plane.RequireStateBootstrap();
        return plane;
    }

    private static ResourceStateDefinition OtherDefinition()
    {
        ResourceStateDefinition existing = CreateDefinition();
        return ResourceStateDefinition.Create("test.added", new Uri("urn:test:added:v1"), existing.KeySchema,
            existing.PayloadSchema, existing.Scope, existing.Uses);
    }

    private static byte[] Projection(params (ResourceStateDefinition Definition, long Revision, ResourceStateValue Desired)[] entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "cohesion/resource-state-projection/v1");
            writer.WriteString("incarnation", Guid.NewGuid());
            writer.WriteStartArray("entries");
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", entry.Definition.Name.Value);
                writer.WriteString("key", "configuration");
                writer.WriteString("scope", "everyInstance");
                writer.WriteString("contractDigest", entry.Definition.ContractDigest);
                writer.WriteString("valueDigest", ResourceStateWire.ComputeValueDigest(entry.Desired.Exists, entry.Desired.Value));
                writer.WriteNumber("revision", entry.Revision);
                writer.WriteNumber("fence", entry.Revision);
                writer.WriteStartObject("desired");
                writer.WriteBoolean("exists", entry.Desired.Exists);
                writer.WritePropertyName("value");
                entry.Desired.Value.WriteTo(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
