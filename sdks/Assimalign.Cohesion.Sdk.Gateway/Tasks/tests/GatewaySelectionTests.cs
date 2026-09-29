using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Gateway.Tests;

public sealed class GatewaySelectionTests
{
    [Theory(DisplayName = "Cohesion Test [Sdk.Gateway] - Local selects the first declared provider and explicit selections win")]
    [InlineData("JitTest;Local", "jit-test")]
    [InlineData("Local;JitTest", "local")]
    public async Task ResolveGateway_UnsetEnvironment_ShouldUseDeclarationOrderAsync(string gateways, string expected)
    {
        // Arrange: provider import order and CLI spelling differ from declaration order.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "GatewaySmoke", "GatewaySmokeWeb", "GatewaySmokeDatabase", "GatewaySmokeSupport");
        string projectPath = workspace.ProjectFile("GatewaySmoke");
        XDocument project = XDocument.Load(projectPath);
        project.Descendants("CohesionGateways").Single().Value = gateways;
        project.Save(projectPath);
        var environment = new Dictionary<string, string?>
        {
            ["COHESION_ENVIRONMENT"] = null,
            ["DOTNET_ENVIRONMENT"] = null,
            ["COHESION_GATEWAY"] = null,
        };
        DotNetBuildResult build = await workspace.BuildAsync("GatewaySmoke", cancellation.Token);
        build.ExitCode.ShouldBe(0, build.Output);

        // Act / assert: execute the generated selector with a real apphost builder.
        DotNetBuildResult unset = await workspace.RunBuiltProjectAsync(
            "GatewaySmoke", ["--mode", "describe", "--jit-provider-token=received"], cancellation.Token, environment);
        AssertGateway(unset, expected);

        environment["COHESION_GATEWAY"] = "LoCaL";
        DotNetBuildResult variable = await workspace.RunBuiltProjectAsync(
            "GatewaySmoke", ["--mode", "describe"], cancellation.Token, environment);
        AssertGateway(variable, "local");
        DotNetBuildResult argument = await workspace.RunBuiltProjectAsync(
            "GatewaySmoke", ["--mode", "describe", "--gateway=jit-test", "--jit-provider-token=received"], cancellation.Token, environment);
        AssertGateway(argument, "jit-test");

        environment["COHESION_GATEWAY"] = null;
        environment["COHESION_ENVIRONMENT"] = "Production";
        DotNetBuildResult production = await workspace.RunBuiltProjectAsync(
            "GatewaySmoke", ["--mode", "describe"], cancellation.Token, environment);
        production.ExitCode.ShouldNotBe(0);
        production.Output.ShouldContain("No Cohesion gateway was selected", Case.Sensitive);
        production.Output.ShouldContain("--gateway <name>", Case.Sensitive);
        production.Output.ShouldContain("COHESION_GATEWAY", Case.Sensitive);
        production.Output.ShouldContain("Accepted names (case-insensitive):", Case.Sensitive);
        production.Output.ShouldContain("jit-test", Case.Sensitive);
        production.Output.ShouldContain($"Local defaults to '{expected}'", Case.Sensitive);
    }

    private static void AssertGateway(DotNetBuildResult result, string expected)
    {
        result.ExitCode.ShouldBe(0, result.Output);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        document.RootElement.GetProperty("gateway").GetString().ShouldBe(expected);
        document.RootElement.GetProperty("environment").GetString().ShouldBe("Local");
    }
}
