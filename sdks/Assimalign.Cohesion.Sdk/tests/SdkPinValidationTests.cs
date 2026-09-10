using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Tests;

public sealed class SdkPinValidationTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk] - disagreeing Cohesion SDK pins report COHSDK002 and corrected pins pass")]
    public async Task Restore_WithDisagreeingThenCorrectedPins_ReportsCOHSDK002ThenBuildSucceeds()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("SdkPinValidationConsumer");
        Dictionary<string, string> pins = ConsumerWorkspace.CreateMatchingSdkPins();
        pins.Count.ShouldBe(20);
        pins["Assimalign.Cohesion.Sdk.Gateway"] = "10.0.1-preview.3.gateway";
        pins["Assimalign.Cohesion.Sdk.Web"] = "10.0.1-preview.3.web";
        workspace.WriteGlobalJson("10.0.300", pins);

        // Act
        DotNetBuildResult invalidResult = await workspace.RestoreAsync(
            "SdkPinValidationConsumer",
            timeout.Token);

        // Assert
        invalidResult.ExitCode.ShouldNotBe(0, invalidResult.Output);
        invalidResult.Output.ShouldContain("COHSDK002");
        invalidResult.Output.ShouldContain("Assimalign.Cohesion.Sdk.Gateway='10.0.1-preview.3.gateway'");
        invalidResult.Output.ShouldContain("Assimalign.Cohesion.Sdk.Web='10.0.1-preview.3.web'");
        invalidResult.Output.ShouldContain($"expected '{ConsumerWorkspace.SdkPackageVersion}'");

        // Arrange
        pins["Assimalign.Cohesion.Sdk.Gateway"] = ConsumerWorkspace.SdkPackageVersion;
        pins["Assimalign.Cohesion.Sdk.Web"] = ConsumerWorkspace.SdkPackageVersion;
        workspace.WriteGlobalJson("10.0.300", pins);

        // Act
        DotNetBuildResult correctedResult = await workspace.BuildAsync(
            "SdkPinValidationConsumer",
            timeout.Token);

        // Assert
        correctedResult.ExitCode.ShouldBe(0, correctedResult.Output);
        correctedResult.Output.ShouldNotContain("COHSDK002");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - CohesionSkipSdkPinCheck bypasses COHSDK002")]
    public async Task Build_WithSkipProperty_BypassesSdkPinValidation()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("SdkPinValidationConsumer");
        Dictionary<string, string> pins = ConsumerWorkspace.CreateMatchingSdkPins();
        pins["Assimalign.Cohesion.Sdk.Gateway"] = "10.0.1-preview.3.mismatch";
        workspace.WriteGlobalJson("10.0.200", pins);

        // Act
        DotNetBuildResult result = await workspace.BuildAsync(
            "SdkPinValidationConsumer",
            ["CohesionSkipSdkPinCheck=true"],
            timeout.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldNotContain("COHSDK002");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - matching local Cohesion SDK identities pass COHSDK002 validation")]
    public async Task Build_WithMatchingLocalPinIdentities_Succeeds()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("SdkPinValidationConsumer");
        Dictionary<string, string> pins = ConsumerWorkspace.CreateMatchingSdkPins();
        foreach (string packageId in new List<string>(pins.Keys))
        {
            pins[packageId] = "10.0.1-preview.3.local";
        }
        workspace.UseInlineBaseSdkVersion("SdkPinValidationConsumer");
        workspace.WriteGlobalJson("10.0.300", pins);

        // Act
        DotNetBuildResult result = await workspace.BuildAsync(
            "SdkPinValidationConsumer",
            timeout.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldNotContain("COHSDK002");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - a .NET SDK pin below 10.0.300 reports COHSDK002")]
    public async Task Build_WithDotNetSdkBelowMinimum_ReportsCOHSDK002()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("SdkPinValidationConsumer");
        workspace.WriteGlobalJson("10.0.200", ConsumerWorkspace.CreateMatchingSdkPins());

        // Act
        DotNetBuildResult result = await workspace.BuildAsync(
            "SdkPinValidationConsumer",
            timeout.Token);

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("COHSDK002");
        result.Output.ShouldContain("pins .NET SDK '10.0.200'");
        result.Output.ShouldContain("requires '10.0.300' or newer");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - a consumer without global.json is not pin-validated")]
    public async Task Build_WithoutGlobalJson_SucceedsWithoutPinValidation()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("SdkPinValidationConsumer");
        workspace.RemoveGlobalJsonAndUseInlineBaseSdkVersion("SdkPinValidationConsumer");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync(
            "SdkPinValidationConsumer",
            timeout.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldNotContain("COHSDK002");
    }
}
