using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Gateway.Tests;

/// <summary>Builds package-only consumers of the slim Gateway SDK.</summary>
public sealed class GatewaySdkTests
{
    /// <summary>
    /// The SDK makes the project an executable and adds the gateway package, so a Program.cs that
    /// declares a model compiles with no reference of its own, and no framework or generated
    /// source comes with it.
    /// </summary>
    /// <returns>A task representing the build.</returns>
    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - Build: gateway consumer compiles against the implicit gateway package")]
    public async Task Build_GatewayConsumer_ShouldReferenceGatewayPackageAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("GatewaySdkConsumer");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("GatewaySdkConsumer", timeout.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        string output = Path.Combine(workspace.ProjectDirectory("GatewaySdkConsumer"), "bin");
        string[] files = Directory.GetFiles(output, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).OfType<string>().ToArray();
        files.ShouldContain("Assimalign.Cohesion.ApplicationModel.Gateway.dll");
        files.ShouldContain("GatewaySdkConsumer.dll");
        files.ShouldContain(OperatingSystem.IsWindows() ? "GatewaySdkConsumer.exe" : "GatewaySdkConsumer");
        files.ShouldNotContain("Assimalign.Cohesion.App.dll");
        string obj = Path.Combine(workspace.ProjectDirectory("GatewaySdkConsumer"), "obj");
        Directory.GetFiles(obj, "*.g.cs", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ShouldNotContain("Gateway.g.cs");
        Directory.GetFiles(obj, "resource.json", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    /// <summary>
    /// An in-process gateway references the Sdk.Web resource project it runs. Resource executables
    /// are self-contained, so the gateway must be too: the build accepts the reference (no
    /// NETSDK1151), and the built gateway starts and loads the resource's App.Web types (no
    /// "No frameworks were found").
    /// </summary>
    /// <returns>A task representing the build and the run.</returns>
    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - Build: gateway referencing a Sdk.Web resource builds and runs")]
    public async Task Build_GatewayReferencingWebResource_ShouldBuildAndRunAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("GatewayInProcessConsumer", "GatewayWebResource");

        // Act
        DotNetBuildResult build = await workspace.BuildAsync("GatewayInProcessConsumer", timeout.Token);

        // Assert
        build.ExitCode.ShouldBe(0, build.Output);
        build.Output.ShouldNotContain("NETSDK1151", Case.Sensitive);
        string bin = Path.Combine(workspace.ProjectDirectory("GatewayInProcessConsumer"), "bin");
        string apphostName = OperatingSystem.IsWindows() ? "GatewayInProcessConsumer.exe" : "GatewayInProcessConsumer";
        string apphost = Directory.GetFiles(bin, apphostName, SearchOption.AllDirectories).ShouldHaveSingleItem();

        var startInfo = new ProcessStartInfo
        {
            FileName = apphost,
            WorkingDirectory = Path.GetDirectoryName(apphost)!,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the built gateway.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        string output = await standardOutput;
        string error = await standardError;

        process.ExitCode.ShouldBe(0, $"{output}{Environment.NewLine}{error}");
        output.ShouldContain("gateway=smoke;resource=Assimalign.Cohesion.Web.Hosting", Case.Sensitive);
    }
}
