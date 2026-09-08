using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Gateway.Tests;

public sealed class GatewaySdkIntegrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - boundary closure generates Externals and referenced gateway Applications")]
    public async Task Build_ApplicationSetClosure_GeneratesExternalsApplicationsAndReferences()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "PlatformDatabase",
            "AppAWeb",
            "AppAGateway",
            "RootGateway");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("RootGateway", cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        string appASource = File.ReadAllText(GeneratedOutput(
            workspace.ProjectDirectory("AppAGateway"),
            "Gateway.g.cs"));
        appASource.ShouldContain("ExternalResourceDeclaration PlatformDatabase");
        appASource.ShouldContain("ApplicationName.Parse(\"platform\")");
        appASource.ShouldContain("closure: new global::Assimalign.Cohesion.ApplicationModel.ResourceManifest[]");
        appASource.ShouldContain("Manifests.PlatformDatabase");
        appASource.ShouldNotContain("AddPlatformDatabase(");

        string rootDirectory = workspace.ProjectDirectory("RootGateway");
        string rootSource = File.ReadAllText(GeneratedOutput(rootDirectory, "Gateway.g.cs"));
        rootSource.ShouldContain("ApplicationDeclaration Appa");
        rootSource.ShouldContain("ApplicationModelResolvers.ControlPlane(");
        rootSource.ShouldContain("public static class Appa");
        rootSource.ShouldContain("public const string");
        rootSource.ShouldContain("public const string WebHttp = \"web-http\"");

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(
            GeneratedOutput(rootDirectory, "resource.json")));
        manifest.RootElement.GetProperty("properties")
            .GetProperty("composite.applicationSet")
            .GetString()
            .ShouldBe("true");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - generated surface compiles and Describe preserves the inferred Web to Database edge")]
    public async Task Build_GatewaySmoke_GeneratesSurfaceAndDescribeIncludesInferredDependency()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "GatewaySmokeDatabase",
            "GatewaySmokeWeb",
            "GatewaySmoke");

        // Act
        DotNetBuildResult build = await workspace.BuildAsync(
            "GatewaySmoke",
            cancellationSource.Token);

        // Assert: generated/compiled build surface
        build.ExitCode.ShouldBe(0, build.Output);
        string gatewayDirectory = workspace.ProjectDirectory("GatewaySmoke");
        string sourcePath = GeneratedOutput(gatewayDirectory, "Gateway.g.cs");
        string source = File.ReadAllText(sourcePath);
        source.ShouldContain(
            "[assembly: global::Assimalign.Cohesion.ApplicationModel.CohesionApplicationAttribute(\"gateway-smoke\")]");
        source.ShouldContain(
            "public static global::Assimalign.Cohesion.ApplicationModel.IApplicationBuilder CreateBuilder(string[] args)");
        source.ShouldContain(
            "global::Assimalign.Cohesion.ApplicationModel.ApplicationName.Parse(\"gateway-smoke\")");
        source.ShouldContain(
            "public static readonly global::Assimalign.Cohesion.ApplicationModel.ResourceManifest GatewaySmokeDatabase");
        source.ShouldContain(
            "public static readonly global::Assimalign.Cohesion.ApplicationModel.ResourceManifest GatewaySmokeWeb");
        source.ShouldContain("AddGatewaySmokeDatabase(");
        source.ShouldContain("DatabaseResourceOptions");
        source.ShouldContain("DatabaseResourceExtensions.AddDatabase(builder, Manifests.GatewaySmokeDatabase");
        source.ShouldContain("AddGatewaySmokeWeb(");
        source.ShouldContain("WebResourceOptions");
        source.ShouldContain("WebResourceExtensions.AddWeb(builder, Manifests.GatewaySmokeWeb");
        source.ShouldContain("AddAllResources()");
        source.ShouldContain("UseGateway(string[] args)");
        source.ShouldContain("global::System.Action<CohesionGatewayProviders> configure");
        source.ShouldContain("new global::Assimalign.Cohesion.ApplicationModel.Gateway.LocalGateway()");
        source.ShouldNotContain(".InProcess(");
        source.ShouldNotContain("ApplicationModel.Gateway.InProcess");

        string aotCapture = Path.Combine(gatewayDirectory, "obj", "gateway-aot.txt");
        File.ReadAllText(aotCapture).Trim().ShouldBe("true|false");

        string manifestPath = GeneratedOutput(gatewayDirectory, "resource.json");
        using (JsonDocument manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            JsonElement manifest = manifestDocument.RootElement;
            manifest.GetProperty("kind").GetString().ShouldBe("Composite");
            manifest.GetProperty("application").GetString().ShouldBe("gateway-smoke");
            manifest.GetProperty("applicationModel").GetString()
                .ShouldBe("Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane");
            manifest.GetProperty("artifact").GetProperty("composable").GetBoolean().ShouldBeTrue();
            manifest.GetProperty("properties").GetProperty("composite.applicationSet").GetString()
                .ShouldBe("true");
            manifest.GetProperty("controlPlane").GetProperty("endpoint").GetString().ShouldBe("admin");
            string?[] endpoints = manifest.GetProperty("endpoints")
                .EnumerateArray()
                .Select(endpoint => endpoint.GetProperty("name").GetString())
                .ToArray();
            endpoints.ShouldContain("web-http");
            endpoints.ShouldContain("database-db");
            manifest.GetProperty("lifecycle").GetProperty("workload").GetString()
                .ShouldBe("StatefulSet");
            manifest.GetProperty("lifecycle").GetProperty("maxReplicas").GetInt32()
                .ShouldBe(1);
        }

        string projectReferenceCapture = Path.Combine(
            gatewayDirectory,
            "obj",
            "gateway-project-references.txt");
        File.Exists(projectReferenceCapture).ShouldBeTrue(
            $"Expected project-reference capture '{projectReferenceCapture}'.");
        string webReference = File.ReadAllLines(projectReferenceCapture)
            .Single(line => line.StartsWith("GatewaySmokeWeb|", StringComparison.Ordinal));
        string.Equals(
                webReference,
                "GatewaySmokeWeb|false|CohesionResourceManifest",
                StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue(webReference);

        string referencePathCapture = Path.Combine(
            gatewayDirectory,
            "obj",
            "gateway-reference-paths.txt");
        string[] referencePaths = File.ReadAllLines(referencePathCapture);
        ContainsOrdinalIgnoreCase(referencePaths, "GatewaySmokeWeb").ShouldBeFalse();
        ContainsOrdinalIgnoreCase(referencePaths, "GatewaySmokeDatabase").ShouldBeFalse();
        ContainsOrdinalIgnoreCase(referencePaths, "Assimalign.Cohesion.Web.Hosting").ShouldBeFalse();
        ContainsOrdinalIgnoreCase(referencePaths, "Assimalign.Cohesion.Database.Hosting").ShouldBeFalse();

        string gatewayOutput = workspace.BuildOutputDirectory("GatewaySmoke");
        Directory.EnumerateFiles(gatewayOutput, "GatewaySmokeWeb.dll", SearchOption.AllDirectories)
            .ShouldBeEmpty();
        Directory.EnumerateFiles(gatewayOutput, "GatewaySmokeDatabase.dll", SearchOption.AllDirectories)
            .ShouldBeEmpty();

        // Act: execute the package-built gateway's real Describe edge.
        DotNetBuildResult describe = await workspace.RunBuiltProjectAsync(
            "GatewaySmoke",
            ["--mode=describe", "--gateway=local", "--environment=Development"],
            cancellationSource.Token);

        // Assert: Describe emits a model document and carries the manifest-inferred edge.
        describe.ExitCode.ShouldBe(0, describe.Output);
        using JsonDocument document = JsonDocument.Parse(describe.StandardOutput);
        JsonElement root = document.RootElement;
        root.GetProperty("schema").GetString().ShouldBe("cohesion/model/v1");
        root.GetProperty("application").GetString().ShouldBe("gateway-smoke");
        root.GetProperty("gateway").GetString().ShouldBe("local");
        root.GetProperty("owner").GetString().ShouldBe("gateway-smoke@local");
        root.GetProperty("mode").GetString().ShouldBe("describe");

        JsonElement[] resources = root.GetProperty("resources").EnumerateArray().ToArray();
        resources.Length.ShouldBe(2);
        JsonElement database = resources.Single(
            resource => resource.GetProperty("name").GetString() == "gateway-smoke-database");
        database.GetProperty("dependencies").GetArrayLength().ShouldBe(0);
        database.GetProperty("manifest").GetProperty("kind").GetString().ShouldBe("Database");
        database.GetProperty("plan").GetProperty("schema").GetString().ShouldBe("cohesion/plan/v1");

        JsonElement web = resources.Single(
            resource => resource.GetProperty("name").GetString() == "gateway-smoke-web");
        web.GetProperty("dependencies").EnumerateArray().Single().GetString()
            .ShouldBe("gateway-smoke-database");
        web.GetProperty("manifest").GetProperty("kind").GetString().ShouldBe("Web");
        web.GetProperty("plan").GetProperty("schema").GetString().ShouldBe("cohesion/plan/v1");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - disabled resource references report COHSDK001")]
    public async Task Build_GatewayReferencesDisabledResource_ReportsCOHSDK001()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "DisabledResource",
            "DisabledGateway");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync(
            "DisabledGateway",
            cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("COHSDK001");
        result.Output.ShouldContain(
            "DisabledResource has CohesionApplicationModel disabled; set it to enabled to reference it");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - out-of-process Hosting references report COHGW001")]
    public async Task Build_OutOfProcessGatewayResolvesAreaHostingAssembly_ReportsCOHGW001()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "ForbiddenHosting",
            "ForbiddenHostingGateway");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync(
            "ForbiddenHostingGateway",
            cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("COHGW001");
        result.Output.ShouldContain("Assimalign.Cohesion.Test.Hosting");
        result.Output.ShouldContain("CohesionGatewayInProcess=true");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - in-process opt-in fails clearly until design item 24 lands")]
    public async Task Build_InProcessGatewayBeforeItem24_FailsWithGuardedDiagnostic()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("InProcessGateway");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync(
            "InProcessGateway",
            cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain(
            "Assimalign.Cohesion.ApplicationModel.Gateway.InProcess is not present in this checkout");
        result.Output.ShouldContain("design item 24");
        result.Output.ShouldContain("Remove InProcess or land item 24 before enabling it");
        result.Output.ShouldNotContain("COHGW001");
    }

    private static string GeneratedOutput(string projectDirectory, string fileName)
    {
        string path = Path.Combine(
            projectDirectory,
            "obj",
            "Debug",
            ConsumerWorkspace.TargetFramework,
            "cohesion",
            fileName);
        File.Exists(path).ShouldBeTrue($"Expected generated output '{path}'.");
        return path;
    }

    private static bool ContainsOrdinalIgnoreCase(string[] values, string expected)
    {
        return values.Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
    }
}
