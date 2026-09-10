using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Tests;

public sealed class StronglyTypedSettingsSdkIntegrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk] - StronglyTypedSettings opt-out generates and compiles nothing")]
    public async Task Build_WithoutCohesionAppSettingsClass_GeneratesAndCompilesNothing()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("StronglyTypedSettingsOptOut");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync(
            "StronglyTypedSettingsOptOut",
            timeout.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        string projectDirectory = workspace.ProjectDirectory("StronglyTypedSettingsOptOut");
        Directory.EnumerateFiles(projectDirectory, "*.generated.cs", SearchOption.AllDirectories)
            .ShouldBeEmpty();

        string compileItemsPath = Path.Combine(projectDirectory, "obj", "compile-items.txt");
        File.Exists(compileItemsPath).ShouldBeTrue(result.Output);
        File.ReadAllText(compileItemsPath).ShouldNotContain(".generated.cs");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - StronglyTypedSettings opt-in generates a public AOT-safe Bind")]
    public async Task Build_WithCohesionAppSettingsClass_GeneratesPublicTypeAndAotSafeBind()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("StronglyTypedSettingsOptIn");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync(
            "StronglyTypedSettingsOptIn",
            timeout.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        string projectDirectory = workspace.ProjectDirectory("StronglyTypedSettingsOptIn");
        string generatedPath = Directory
            .EnumerateFiles(projectDirectory, "CatalogSettings.generated.cs", SearchOption.AllDirectories)
            .ShouldHaveSingleItem();
        Path.GetRelativePath(projectDirectory, generatedPath)
            .ShouldStartWith(Path.Combine("obj", "Debug", "net10.0", "Cohesion"));
        string source = File.ReadAllText(generatedPath);

        source.ShouldContain("#nullable enable");
        source.ShouldContain("using System;");
        source.ShouldContain("using System.Collections.Generic;");
        source.ShouldContain("using System.Globalization;");
        source.ShouldContain("using Assimalign.Cohesion.Configuration;");
        source.ShouldContain("public class CatalogSettings");
        source.ShouldContain("public class CatalogSettingsService");
        source.ShouldContain("public class CatalogSettingsReplicas");
        source.ShouldContain("public double? MaxItems { get; set; }");
        source.ShouldContain("public IEnumerable<double?>? Thresholds { get; set; }");
        source.ShouldContain("public void Bind(IConfiguration configuration)");
        source.ShouldContain("configuration.GetEntry(\"Service:Host\")");
        source.ShouldContain("configuration.GetEntry(\"Tags:0\")");
        source.ShouldContain("configuration.GetEntry(\"Replicas:1:Enabled\")");
        source.ShouldContain("new List<string?>(this.Tags)");
        source.ShouldNotContain("System.Reflection");
        source.ShouldNotContain("ConfigurationBinder");
        source.ShouldNotContain("GetValue<");
        source.ShouldNotContain("Activator");

        // Arrange
        string settingsPath = Path.Combine(projectDirectory, "appsettings.json");
        string settings = File.ReadAllText(settingsPath).Replace(
            "  \"Ratio\": 1.5,",
            "  \"Ratio\": 1.5," + Environment.NewLine + "  \"AddedAfterBuild\": \"yes\",",
            StringComparison.Ordinal);
        File.WriteAllText(settingsPath, settings);
        File.SetLastWriteTimeUtc(settingsPath, DateTime.UtcNow.AddSeconds(2));

        // Act
        DotNetBuildResult rebuiltResult = await workspace.BuildAsync(
            "StronglyTypedSettingsOptIn",
            timeout.Token);

        // Assert
        rebuiltResult.ExitCode.ShouldBe(0, rebuiltResult.Output);
        File.ReadAllText(generatedPath)
            .ShouldContain("public string? AddedAfterBuild { get; set; }");
    }
}
