using System.Linq;

using Assimalign.Cohesion.Fabric;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Workloads.Tests;

public class WorkloadCapabilitiesTests
{
    private static readonly PlatformName _platform = new("local");

    private static readonly WorkloadCapabilities _capabilities = new(
        ProgramSource: Support.Native,
        ImageSource: Support.Unsupported,
        MaxReplicas: 1,
        PersistentMounts: Support.Emulated("A local directory."),
        Stateful: Support.Native,
        OneShotTasks: Support.Native,
        Probes: Support.Native,
        Isolation: Isolation.None);

    [Fact(DisplayName = "Cohesion Test [Workloads] - Check: Should accept a spec the platform supports natively")]
    public void Check_NativeSpec_ShouldHaveNoFindings()
    {
        // Arrange
        var spec = new WorkloadSpec { Source = WorkloadSource.FromProgram("Orders.Api") };

        // Act
        var result = _capabilities.Check(spec, _platform, new FabricEnvironmentPolicy());

        // Assert
        result.Items.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Workloads] - Check: Should refuse an unsupported field and too many replicas")]
    public void Check_UnsupportedSpec_ShouldReportErrors()
    {
        // Arrange
        var spec = new WorkloadSpec { Source = WorkloadSource.FromImage("registry.acme.example/orders-api@sha256:9b1e"), Replicas = 3 };

        // Act
        var result = _capabilities.Check(spec, _platform, new FabricEnvironmentPolicy());

        // Assert
        result.HasErrors.ShouldBeTrue();
        result.Items.Select(item => item.Code).ShouldBe(["CapabilityUnsupported", "ReplicasExceedMaximum"]);
        result.Items[0].Path.ShouldBe("spec.source.image");
    }

    [Fact(DisplayName = "Cohesion Test [Workloads] - Check: Should refuse emulation the policy denies and warn on emulation it allows")]
    public void Check_EmulatedField_ShouldFollowPolicy()
    {
        // Arrange
        var volume = new FabricRef(new ResourceTypeName("Cohesion.Storage/volumes"), new ResourceName("orders-data"));
        var spec = new WorkloadSpec { Source = WorkloadSource.FromProgram("Orders.Api"), Mounts = [new WorkloadMount(volume, "/data")] };

        // Act
        var denied = _capabilities.Check(spec, _platform, new FabricEnvironmentPolicy());
        var allowed = _capabilities.Check(spec, _platform, new FabricEnvironmentPolicy { AllowEmulation = true });

        // Assert
        denied.Items.Single().Code.ShouldBe("CapabilityEmulationDenied");
        denied.HasErrors.ShouldBeTrue();
        allowed.Items.Single().Severity.ShouldBe(FabricDiagnosticSeverity.Warning);
        allowed.HasErrors.ShouldBeFalse();
    }
}
