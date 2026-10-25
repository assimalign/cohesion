using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tests;

public sealed class ResourceRuntimeManifestSdkTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk] - Runtime manifest: packaged adapters discover contracts and startup configuration")]
    public async Task Build_WithPackagedCorsAdapter_ShouldPreserveAndRegisterRuntimeManifestAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("ManagedWeb");

        // Act
        DotNetBuildResult build = await workspace.BuildAsync("ManagedWeb", timeout.Token);
        build.ExitCode.ShouldBe(0, build.Output);
        string project = workspace.ProjectDirectory("ManagedWeb");
        string resourcePath = Directory.GetFiles(Path.Combine(project, "obj"), "resource.json", SearchOption.AllDirectories).Single();
        using JsonDocument resource = JsonDocument.Parse(File.ReadAllBytes(resourcePath));
        JsonElement runtime = resource.RootElement.GetProperty("runtime");

        // Assert
        resource.RootElement.GetProperty("schema").GetString().ShouldBe("cohesion/resource/v2");
        resource.RootElement.TryGetProperty("commands", out _).ShouldBeFalse();
        runtime.EnumerateObject().Select(static property => property.Name).ShouldBe(["schema", "management"]);
        runtime.GetProperty("management").GetProperty("protocol").GetString().ShouldBe("cohesion/resource-state/v1");
        Directory.GetFiles(Path.Combine(project, "obj"), "resource.runtime.json", SearchOption.AllDirectories).ShouldBeEmpty();
        string sourcePath = Directory.GetFiles(Path.Combine(project, "obj"), "Resource.g.cs", SearchOption.AllDirectories).Single();
        File.ReadAllText(sourcePath).ShouldNotContain("StateContracts", Case.Sensitive);
        string registrationPath = Directory.GetFiles(Path.Combine(project, "obj"), "ResourceControlPlane.g.cs", SearchOption.AllDirectories).Single();
        File.ReadAllText(registrationPath).ShouldContain("ConfigureRuntimeManagement(", Case.Sensitive);
        File.ReadAllText(registrationPath).ShouldNotContain("ConfigureRuntimeManifest(", Case.Sensitive);
        string assembly = Directory.GetFiles(Path.Combine(project, "bin"), "ManagedWeb.dll", SearchOption.AllDirectories).Single();
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(assembly)!, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(assembly);
        start.Environment["Cors__Origin"] = "https://override.example";
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        process.ExitCode.ShouldBe(0, await error);
        (await output).ShouldContain("Runtime catalog captures effective startup configuration.", Case.Sensitive);

        DotNetBuildResult pack = await workspace.PackAsync("ManagedWeb", cancellationToken: timeout.Token);
        pack.ExitCode.ShouldBe(0, pack.Output);
        string package = Directory.GetFiles(workspace.LocalPackageFeedDirectory, "ManagedWeb.Manifest.*.nupkg", SearchOption.AllDirectories).Single();
        using ZipArchive archive = ZipFile.OpenRead(package);
        using Stream embedded = archive.GetEntry("cohesion/resource.json")!.Open();
        using JsonDocument packaged = await JsonDocument.ParseAsync(embedded, cancellationToken: timeout.Token);
        JsonElement.DeepEquals(packaged.RootElement.GetProperty("runtime"), runtime).ShouldBeTrue();
        packaged.RootElement.TryGetProperty("commands", out _).ShouldBeFalse();
    }
}
