using System;
using System.Linq;
using System.Text.Json;

using Assimalign.Cohesion.Workloads;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Fabric.InProcess.Tests;

public class InProcessPlatformTests
{
    [Fact(DisplayName = "Cohesion Test [Fabric.InProcess] - Create: Should register a classifier and the workload extension")]
    public void Create_InProcessPlatform_ShouldRegister()
    {
        // Act
        var registration = FabricPlatformRegistration.Create(new InProcessPlatform());

        // Assert
        registration.Name.ShouldBe(new PlatformName("inprocess"));
        registration.FaultClassifiers.Count.ShouldBe(1);
        var extension = registration.Extensions.Single();
        extension.HostType.ShouldBe(WorkloadResourceTypes.Workloads);
        extension.HubType.ShouldBe(typeof(InProcessWorkloadSettings));
    }

    [Theory(DisplayName = "Cohesion Test [Fabric.InProcess] - Classify: Should map the platform's reasons onto the catalog")]
    [InlineData(InProcessFaultReasons.EntryPointThrew, FaultCode.WorkloadCrashing)]
    [InlineData(InProcessFaultReasons.EntryPointExited, FaultCode.WorkloadCrashing)]
    [InlineData(InProcessFaultReasons.ProgramMissing, FaultCode.ConfigInvalid)]
    [InlineData("SomethingNew", FaultCode.PlatformError)]
    public void Classify_Reason_ShouldMapToCode(string reason, FaultCode expected)
    {
        // Arrange
        var classifier = FabricPlatformRegistration.Create(new InProcessPlatform()).FaultClassifiers.Single();
        var detail = new PlatformDetail(InProcessPlatform.PlatformName, reason, null, "boom", [], "Unhandled exception.");

        // Act
        var fault = classifier.Classify(detail, ResourceUid.CreateVersion7());

        // Assert
        fault.Code.ShouldBe(expected);
        fault.Disposition.ShouldBe(FaultDisposition.NeedsChange);
        fault.Platform.ShouldBeSameAs(detail);
    }

    [Fact(DisplayName = "Cohesion Test [Fabric.InProcess] - Read: Should read the workload block and refuse an unknown version")]
    public void Read_WorkloadBlock_ShouldReadKnownVersionOnly()
    {
        // Arrange
        var extension = (FabricExtension<InProcessWorkloadSettings>)FabricPlatformRegistration.Create(new InProcessPlatform()).Extensions.Single();
        using var block = JsonDocument.Parse("""{ "contentRoot": "wwwroot" }""");

        // Act
        var settings = extension.Read(new ApiVersion("2026-12-01"), block.RootElement);

        // Assert
        settings.ContentRoot.ShouldBe("wwwroot");
        Should.Throw<JsonException>(() => extension.Read(new ApiVersion("2027-01-01"), block.RootElement));
    }

    [Fact(DisplayName = "Cohesion Test [Fabric.InProcess] - WorkloadCapabilities: Should refuse images and more than one replica")]
    public void WorkloadCapabilities_ImageWithReplicas_ShouldBeRefused()
    {
        // Arrange
        var spec = new WorkloadSpec { Source = WorkloadSource.FromImage("registry.acme.example/orders-api"), Replicas = 3 };

        // Act
        var result = InProcessPlatform.WorkloadCapabilities.Check(spec, InProcessPlatform.PlatformName, new FabricEnvironmentPolicy { AllowEmulation = true });

        // Assert
        result.Items.Select(item => item.Code).ShouldBe(["CapabilityUnsupported", "ReplicasExceedMaximum"]);
    }
}
