using System.Collections.Generic;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Fabric.Tests;

public class FabricJsonContextTests
{
    private const string envelope = """
        {
          "id": "/environments/prod/applications/orders/providers/Cohesion.Workloads/workloads/orders-api",
          "uid": "0193a4f2-7c1e-7d40-9b1a-5e2f0c8d1a77",
          "type": "Cohesion.Workloads/workloads",
          "apiVersion": "2026-12-01",
          "metadata": {
            "generation": 9,
            "etag": "\"0x8DD2F\"",
            "owner": "0193a4e0-0000-7000-8000-000000000001",
            "visibility": "public",
            "managedFields": [ { "manager": "apphost", "fields": [ "spec.replicas" ] } ],
            "finalizers": [ "Cohesion.Workloads" ],
            "labels": { "cost-center": "retail" }
          },
          "spec": { "replicas": 3 },
          "status": {
            "observedGeneration": 9,
            "provisioningState": "Succeeded",
            "conditions": [ { "type": "Ready", "status": "True" } ],
            "outputs": { "url": "http://orders-api.acme-orders.svc:8080" }
          }
        }
        """;

    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricJsonContext: Should read the v2 envelope")]
    public void Deserialize_Envelope_ShouldReadEveryPart()
    {
        // Act
        var resource = JsonSerializer.Deserialize(envelope, FabricJsonContext.Default.ResourceEnvelope);

        // Assert
        resource.ShouldNotBeNull();
        resource.Type.ShouldBe(new ResourceTypeName("Cohesion.Workloads/workloads"));
        resource.Metadata.Generation.ShouldBe(9);
        resource.Metadata.Visibility.ShouldBe(ResourceVisibility.Public);
        resource.Metadata.Owner.ShouldNotBeNull();
        resource.Spec.GetProperty("replicas").GetInt32().ShouldBe(3);
        resource.Status!.ProvisioningState.ShouldBe(ProvisioningState.Succeeded);
        resource.Status.Conditions[0].Status.ShouldBe(ConditionStatus.True);
        resource.Status.Outputs["url"].GetString().ShouldBe("http://orders-api.acme-orders.svc:8080");
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricJsonContext: Should refuse an unknown member")]
    public void Deserialize_UnknownMember_ShouldThrow()
    {
        // Arrange
        var misspelled = envelope.Replace("\"finalizers\"", "\"finalisers\"");

        // Act & Assert
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(misspelled, FabricJsonContext.Default.ResourceEnvelope));
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricJsonContext: Should refuse a malformed identifier")]
    public void Deserialize_MalformedType_ShouldThrow()
    {
        // Arrange
        var malformed = envelope.Replace("\"type\": \"Cohesion.Workloads/workloads\"", "\"type\": \"workloads\"");

        // Act & Assert
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(malformed, FabricJsonContext.Default.ResourceEnvelope));
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricJsonContext: Should round-trip a fault with its platform detail")]
    public void Serialize_Fault_ShouldKeepPlatformDetail()
    {
        // Arrange
        var fault = new FabricFault(
            FaultCode.ImageUnavailable,
            FaultDisposition.NeedsChange,
            "0/1 pods running.",
            new PlatformDetail(new PlatformName("kubernetes"), "ImagePullBackOff", "v1/Pod/acme-identity/identity-store-0", null,
                [new PlatformEvent("Failed", 4, "401 Unauthorized")], null));

        // Act
        var json = JsonSerializer.Serialize(fault, FabricJsonContext.Default.FabricFault);
        var read = JsonSerializer.Deserialize(json, FabricJsonContext.Default.FabricFault);

        // Assert
        json.ShouldContain("\"code\":\"ImageUnavailable\"", Case.Sensitive);
        read!.Platform.Reason.ShouldBe("ImagePullBackOff");
        read.Platform.Events[0].Count.ShouldBe(4);
    }
}
