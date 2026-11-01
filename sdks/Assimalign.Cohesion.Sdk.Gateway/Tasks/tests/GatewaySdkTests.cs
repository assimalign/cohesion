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
    /// An in-process gateway references a top-level Sdk.Web resource and names it with
    /// <c>CohesionResource</c>. The resource's build emits the bridge to its entry point, the
    /// gateway's build binds it, and a bare <c>UseInProcess()</c> runs it: the built gateway starts,
    /// reads the resource's endpoint output, gets an answer from it over HTTP, then stops and
    /// disposes. Resource executables are self-contained, so the gateway must be too: the build
    /// accepts the reference (no NETSDK1151), and the resource's App frameworks load in the
    /// gateway's process.
    /// </summary>
    /// <returns>A task representing the build and the run.</returns>
    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - Run: in-process gateway runs a top-level Sdk.Web resource it binds from project metadata")]
    public async Task Run_InProcessGatewayWithWebResource_ShouldServeTheResourceAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("GatewayInProcessConsumer", "GatewayWebResource");

        // Act
        DotNetBuildResult build = await workspace.BuildAsync("GatewayInProcessConsumer", timeout.Token);
        build.ExitCode.ShouldBe(0, build.Output);
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

        // Assert
        build.Output.ShouldNotContain("NETSDK1151", Case.Sensitive);
        build.Output.ShouldNotContain("COHAPM", Case.Sensitive);
        process.ExitCode.ShouldBe(0, $"{output}{Environment.NewLine}{error}");
        output.ShouldContain("gateway=smoke;resource=smoke-api;instances=1;answer=smoke-api answered", Case.Sensitive);
    }

    /// <summary>
    /// A reference with <c>ReferenceOutputAssembly="false"</c> resolves no assembly, so the gateway
    /// could never run the program it names. The build still hands the reference to the generator,
    /// which reports it at the referenced project instead of the run failing to find the program.
    /// </summary>
    /// <returns>A task representing the build.</returns>
    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - Build: a resource reference whose assembly the gateway does not load is reported")]
    public async Task Build_ResourceReferenceWithoutOutputAssembly_ShouldReportItAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("GatewayUnloadedResourceConsumer", "GatewayWebResource");

        // Act
        DotNetBuildResult build = await workspace.BuildAsync("GatewayUnloadedResourceConsumer", timeout.Token);

        // Assert
        build.ExitCode.ShouldBe(0, build.Output);
        build.Output.ShouldContain("GatewayWebResource.csproj(1,1): warning COHAPM0001", Case.Sensitive);
        build.Output.ShouldContain("ReferenceOutputAssembly", Case.Sensitive);
    }
}
