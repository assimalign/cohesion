using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Tests;

/// <summary>Verifies the base SDK rejects an enabled model without the auxiliary SDK import.</summary>
public sealed class ApplicationModelSdkGuardTests
{
    /// <summary>Builds a plain base-SDK consumer that enables orchestration without importing its SDK.</summary>
    /// <returns>A task representing the build.</returns>
    [Fact(DisplayName = "Cohesion Test [Sdk] - enabled application model without ApplicationModel SDK reports COHSDK011")]
    public async Task Build_EnabledApplicationModelWithoutSdkImport_ShouldReportCOHSDK011()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("EnabledWithoutApplicationModelSdk");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("EnabledWithoutApplicationModelSdk", timeout.Token);

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("COHSDK011", Case.Sensitive);
        result.Output.ShouldContain("Assimalign.Cohesion.Sdk.ApplicationModel", Case.Sensitive);
    }
}
