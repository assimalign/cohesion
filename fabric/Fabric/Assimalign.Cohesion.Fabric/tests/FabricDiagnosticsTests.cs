using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Fabric.Tests;

public class FabricDiagnosticsTests
{
    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricDiagnostics: Should report errors after combining")]
    public void Combine_WithError_ShouldHaveErrors()
    {
        // Arrange
        var warning = new FabricDiagnostics([new FabricDiagnostic(FabricDiagnosticSeverity.Warning, "Deprecated", "Deprecated version.")]);
        var error = new FabricDiagnostics([new FabricDiagnostic(FabricDiagnosticSeverity.Error, "CapabilityUnsupported", "Unsupported.", "spec.replicas")]);

        // Act
        var combined = warning.Combine(error);

        // Assert
        warning.HasErrors.ShouldBeFalse();
        combined.HasErrors.ShouldBeTrue();
        combined.Items.Count.ShouldBe(2);
        FabricDiagnostics.None.Combine(warning).ShouldBeSameAs(warning);
    }
}
