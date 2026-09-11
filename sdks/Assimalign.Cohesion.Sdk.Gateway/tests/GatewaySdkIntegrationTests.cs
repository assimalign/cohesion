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
        appASource.ShouldContain("ExternalResourceDeclaration PlatformConfigurationStore");
        appASource.ShouldContain("ApplicationName.Parse(\"platform\")");
        appASource.ShouldContain("closure: new global::Assimalign.Cohesion.ApplicationModel.ResourceManifest[]");
        appASource.ShouldContain("Manifests.PlatformConfigurationStore");
        appASource.ShouldNotContain("AddPlatformConfigurationStore(");

        string rootDirectory = workspace.ProjectDirectory("RootGateway");
        string rootSource = File.ReadAllText(GeneratedOutput(rootDirectory, "Gateway.g.cs"));
        rootSource.ShouldContain("ApplicationDeclaration AppA");
        rootSource.ShouldContain("ApplicationModelResolvers.ControlPlane(");
        rootSource.ShouldContain("public static class AppA");
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
            "GatewaySmokeSupport",
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
        source.ShouldContain("public global::Assimalign.Cohesion.Database.ApplicationModel.IDatabaseResourceDescriptor AddGatewaySmokeDatabase(", Case.Sensitive);
        source.ShouldContain("DatabaseResourceExtensions.AddDatabase(builder, Manifests.GatewaySmokeDatabase");
        source.ShouldContain("AddGatewaySmokeWeb(");
        source.ShouldContain("WebResourceOptions");
        source.ShouldContain("public global::Assimalign.Cohesion.Web.ApplicationModel.IWebResourceDescriptor AddGatewaySmokeWeb(", Case.Sensitive);
        source.ShouldContain("WebResourceExtensions.AddWeb(builder, Manifests.GatewaySmokeWeb");
        source.ShouldContain("AddAllResources()");
        source.ShouldContain("UseGateway(string[] args)");
        source.ShouldContain("global::System.Action<CohesionGatewayProviders> configure");
        source.ShouldContain("ApplicationGatewayCommandLine.Apply(options, args)");
        source.ShouldContain("global::GatewaySmoke.JitTestGatewayCommandLine.Apply(options, args)");
        source.Split(
            "global::GatewaySmoke.JitTestGatewayCommandLine.Apply(options, args);",
            StringSplitOptions.None).Length.ShouldBe(3);
        source.ReplaceLineEndings("\n").ShouldContain(
            "global::Assimalign.Cohesion.ApplicationModel.Gateway.ApplicationGatewayCommandLine.Apply(options, args);\n" +
            "        global::GatewaySmoke.JitTestGatewayCommandLine.Apply(options, args);\n" +
            "        global::Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.GatewayControlPlane.Configure(options, runMode);");
        source.ShouldContain("Gateway.ControlPlane.GatewayControlPlane.Configure(options, runMode)");
        source.ShouldContain("new CohesionGatewayProviders(selected, args, builder.RunMode)");
        source.ShouldContain("new global::Assimalign.Cohesion.ApplicationModel.Gateway.LocalGateway(options)");
        source.ShouldNotContain(".InProcess(");
        source.ShouldNotContain("ApplicationModel.Gateway.InProcess");

        string aotCapture = Path.Combine(gatewayDirectory, "obj", "gateway-aot.txt");
        string[] aotState = File.ReadAllText(aotCapture).Trim().Split('|');
        aotState.Length.ShouldBe(3);
        aotState[0].ShouldBe("true");
        aotState[1].ShouldBe("false");
        aotState[2].ShouldNotBe("true");

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
        ContainsOrdinalIgnoreCase(
            referencePaths,
            "Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane").ShouldBeTrue();

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
        File.Exists(Path.Combine(
            gatewayDirectory,
            ".cohesion",
            "gateway-smoke",
            "control-plane.json")).ShouldBeFalse();

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

        // Act: select the contributed provider and pass an option understood only by it.
        DotNetBuildResult providerDescribe = await workspace.RunBuiltProjectAsync(
            "GatewaySmoke",
            [
                "--mode=describe",
                "--gateway=jit-test",
                "--environment=Development",
                "--jit-provider-token=received",
            ],
            cancellationSource.Token);

        // Assert: UseGateway forwards the full argument array to the selected provider hook.
        providerDescribe.ExitCode.ShouldBe(0, providerDescribe.Output);
        using JsonDocument providerDocument = JsonDocument.Parse(providerDescribe.StandardOutput);
        providerDocument.RootElement.GetProperty("gateway").GetString().ShouldBe("jit-test");

        DotNetBuildResult configuredProviderDescribe = await workspace.RunBuiltProjectAsync(
            "GatewaySmoke",
            [
                "--mode=describe",
                "--gateway=jit-test",
                "--environment=Development",
                "--jit-provider-token=received",
                "--configure-provider",
            ],
            cancellationSource.Token);
        configuredProviderDescribe.ExitCode.ShouldBe(0, configuredProviderDescribe.Output);
        using JsonDocument configuredProviderDocument = JsonDocument.Parse(
            configuredProviderDescribe.StandardOutput);
        configuredProviderDocument.RootElement.GetProperty("gateway").GetString().ShouldBe("jit-test");
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

    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - packed InProcess provider binds the composable project closure")]
    public async Task Build_InProcessGateway_BindsComposableProjectClosure()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "GatewaySmokeDatabase",
            "GatewaySmokeSupport",
            "GatewaySmokeWeb",
            "InProcessNamedEntry",
            "InProcessNonComposable",
            "InProcessGateway");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync(
            "InProcessGateway",
            cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldNotContain("COHGW001");

        string source = File.ReadAllText(GeneratedOutput(
            workspace.ProjectDirectory("InProcessGateway"),
            "Gateway.g.cs"));
        source.ShouldContain(
            "using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;");
        source.ShouldContain(
            "new global::Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.InProcessGateway(options)");
        source.ShouldContain("AddGatewaySmokeWeb(");
        source.ShouldContain("AddGatewaySmokeDatabase(");
        source.ShouldContain("AddInprocessNamedEntry(");
        source.ShouldContain("AddInprocessNoncomposable(");
        source.ShouldContain(
            "global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |");
        source.ShouldContain(
            "global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicMethods,");
        source.ShouldContain("\"Program\",");
        source.ShouldContain("\"GatewaySmokeWeb\")]");
        source.ShouldContain("\"GatewaySmokeDatabase\")]");
        source.ShouldContain("\"InProcessNamedEntry.CustomEntry\",");
        source.ShouldContain("\"InProcessNamedEntry\")]");
        source.ShouldContain(EntryAnchor("GatewaySmokeWeb", "GatewaySmokeWeb"));
        source.ShouldContain(EntryAnchor("GatewaySmokeDatabase", "GatewaySmokeDatabase"));
        source.ShouldContain(EntryAnchor("GatewaySmokeWeb", "InProcessNamedEntry"));
        source.ShouldContain("global::System.AppContext.BaseDirectory");
        source.ShouldContain("\"cohesion\",");
        source.ShouldContain("\"resources\",");
        source.ShouldContain("\"gateway-smoke-web\"));");
        source.ShouldContain("\"gateway-smoke-database\"));");
        source.ShouldContain("\"inprocess-named-entry\"));");
        source.ShouldNotContain(EntryAnchor("InProcessNonComposable", "InProcessNonComposable"));
        source.Split(".InProcess(", StringSplitOptions.None).Length.ShouldBe(4);
        source.Split("[global::System.Diagnostics.CodeAnalysis.DynamicDependency(", StringSplitOptions.None)
            .Length.ShouldBe(4);
        source.Split("[global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(", StringSplitOptions.None)
            .Length.ShouldBe(4);
        source.ShouldContain(
            "Justification = \"The generated DynamicDependency roots the resource entry point used by the in-process binding.\"");

        string outputDirectory = workspace.BuildOutputDirectory("InProcessGateway");
        File.Exists(Path.Combine(
            outputDirectory,
            "cohesion",
            "resources",
            "gateway-smoke-web",
            "content",
            "web.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(
            outputDirectory,
            "cohesion",
            "resources",
            "inprocess-named-entry",
            "content",
            "named-entry.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(
            outputDirectory,
            "cohesion",
            "resources",
            "gateway-smoke-database",
            "content",
            "database.txt")).ShouldBeTrue();
        Directory.Exists(Path.Combine(
            outputDirectory,
            "cohesion",
            "resources",
            "inprocess-noncomposable")).ShouldBeFalse();
        File.Exists(Path.Combine(outputDirectory, "InProcessNonComposable.dll")).ShouldBeFalse();
        File.Exists(Path.Combine(outputDirectory, "InProcessNonComposable.pdb")).ShouldBeFalse();
        File.Exists(Path.Combine(outputDirectory, "NUlid.dll")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(outputDirectory, "InProcessGateway.deps.json"))
            .ShouldNotContain("InProcessNonComposable");
        File.Exists(Path.Combine(outputDirectory, "content", "database.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(outputDirectory, "content", "noncomposable.txt")).ShouldBeFalse();
        FindNativeSqliteLibrary(outputDirectory).ShouldNotBeNull();

        DotNetBuildResult publish = await workspace.PublishAsync(
            "InProcessGateway",
            cancellationSource.Token);
        publish.ExitCode.ShouldBe(0, publish.Output);
        string publishDirectory = workspace.PublishOutputDirectory("InProcessGateway");
        File.Exists(Path.Combine(
            publishDirectory,
            "cohesion",
            "resources",
            "gateway-smoke-web",
            "content",
            "web.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(
            publishDirectory,
            "cohesion",
            "resources",
            "inprocess-named-entry",
            "content",
            "named-entry.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(
            publishDirectory,
            "cohesion",
            "resources",
            "gateway-smoke-database",
            "content",
            "database.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(publishDirectory, "InProcessNonComposable.dll")).ShouldBeFalse();
        File.Exists(Path.Combine(publishDirectory, "NUlid.dll")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(publishDirectory, "InProcessGateway.deps.json"))
            .ShouldNotContain("InProcessNonComposable");
        FindNativeSqliteLibrary(publishDirectory).ShouldNotBeNull();

        // A referenced entry assembly is a generator input even when its manifest is unchanged.
        string entryPointPath = Path.Combine(
            workspace.ProjectDirectory("InProcessNamedEntry"),
            "Program.cs");
        string entryPointSource = File.ReadAllText(entryPointPath);
        File.WriteAllText(
            entryPointPath,
            entryPointSource.Replace("CustomEntry", "RenamedEntry", StringComparison.Ordinal));

        DotNetBuildResult incrementalBuild = await workspace.BuildAsync(
            "InProcessGateway",
            cancellationSource.Token);
        incrementalBuild.ExitCode.ShouldBe(0, incrementalBuild.Output);
        string regeneratedSource = File.ReadAllText(GeneratedOutput(
            workspace.ProjectDirectory("InProcessGateway"),
            "Gateway.g.cs"));
        regeneratedSource.ShouldContain("\"InProcessNamedEntry.RenamedEntry\",");
        regeneratedSource.ShouldNotContain("\"InProcessNamedEntry.CustomEntry\",");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - clean parallel InProcess builds use evaluation-time runtime references")]
    public async Task Build_InProcessGateway_FromCleanInParallelTwice_UsesRuntimeReferences()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "GatewaySmokeDatabase",
            "GatewaySmokeSupport",
            "GatewaySmokeWeb",
            "InProcessNamedEntry",
            "InProcessNonComposable",
            "InProcessGateway");

        for (int attempt = 0; attempt < 2; attempt++)
        {
            // Act
            DotNetBuildResult clean = await workspace.CleanAsync(
                "InProcessGateway",
                cancellationSource.Token);
            DotNetBuildResult build = await workspace.BuildInParallelAsync(
                "InProcessGateway",
                cancellationSource.Token);

            // Assert
            clean.ExitCode.ShouldBe(0, clean.Output);
            build.ExitCode.ShouldBe(0, build.Output);
            build.Output.ShouldNotContain("CS0234");

            string captureDirectory = Path.Combine(
                workspace.ProjectDirectory("InProcessGateway"),
                "obj");
            string[] projectReferences = File.ReadAllLines(Path.Combine(
                captureDirectory,
                "inprocess-project-references.txt"));
            projectReferences.ShouldContain("GatewaySmokeWeb|true||false|all|all");
            projectReferences.ShouldContain("InProcessNamedEntry|true||false|all|all");
            projectReferences.ShouldContain("InProcessNonComposable|true||false|all|all");

            string[] frameworkReferences = File.ReadAllLines(Path.Combine(
                captureDirectory,
                "inprocess-framework-references.txt"));
            frameworkReferences.ShouldContain("Assimalign.Cohesion.App");
            frameworkReferences.ShouldContain("Assimalign.Cohesion.App.Web");
            frameworkReferences.ShouldContain("Assimalign.Cohesion.App.Database");

            string[] properties = File.ReadAllText(Path.Combine(
                    captureDirectory,
                    "inprocess-properties.txt"))
                .Trim()
                .Split('|');
            properties.Length.ShouldBe(5);
            properties[0].ShouldBe("true");
            properties[1].ShouldBe("false");
            properties[2].ShouldBe("true");
            properties[3].ShouldNotBeNullOrWhiteSpace();
            properties[3].ShouldBe(properties[4]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - RID publish binds the transitive composable project closure")]
    public async Task Publish_RidInProcessGateway_BindsTransitiveComposableProjectClosure()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "GatewaySmokeDatabase",
            "GatewaySmokeSupport",
            "GatewaySmokeWeb",
            "InProcessTransitiveGateway");
        const string runtimeIdentifier = "win-x64";

        // Act
        DotNetBuildResult result = await workspace.PublishSelfContainedAsync(
            "InProcessTransitiveGateway",
            runtimeIdentifier,
            cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        string source = File.ReadAllText(GeneratedOutput(
            workspace.ProjectDirectory("InProcessTransitiveGateway"),
            "Gateway.g.cs",
            runtimeIdentifier));
        source.ShouldContain("AddGatewaySmokeWeb(");
        source.ShouldContain("AddGatewaySmokeDatabase(");
        source.ShouldContain(EntryAnchor("GatewaySmokeWeb", "GatewaySmokeWeb"));
        source.ShouldContain(EntryAnchor("GatewaySmokeDatabase", "GatewaySmokeDatabase"));
        source.Split(".InProcess(", StringSplitOptions.None).Length.ShouldBe(3);
        source.Split("[global::System.Diagnostics.CodeAnalysis.DynamicDependency(", StringSplitOptions.None)
            .Length.ShouldBe(3);

        string publishDirectory = workspace.PublishOutputDirectory(
            "InProcessTransitiveGateway",
            runtimeIdentifier);
        File.Exists(Path.Combine(publishDirectory, "GatewaySmokeWeb.dll")).ShouldBeTrue(
            $"Expected the transitive Web entry assembly in '{publishDirectory}'.");
        File.Exists(Path.Combine(publishDirectory, "GatewaySmokeDatabase.dll")).ShouldBeTrue(
            $"Expected the transitive Database entry assembly in '{publishDirectory}'.");
        File.Exists(Path.Combine(publishDirectory, "GatewaySmokeSupport.dll")).ShouldBeTrue(
            $"Expected the Web resource's project-produced dependency in '{publishDirectory}'.");
        FindNativeSqliteLibrary(publishDirectory, runtimeIdentifier).ShouldNotBeNull();

        foreach ((string resource, string contentFile) in new[]
        {
            ("gateway-smoke-web", "web.txt"),
            ("gateway-smoke-database", "database.txt"),
        })
        {
            string contentRoot = Path.Combine(
                publishDirectory,
                "cohesion",
                "resources",
                resource);
            File.Exists(Path.Combine(contentRoot, "content", contentFile)).ShouldBeTrue(
                $"Expected isolated content for '{resource}' in '{contentRoot}'.");

            string manifestPath = Path.Combine(contentRoot, ".cohesion-resource.json");
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            string? appHost = manifest.RootElement
                .GetProperty("artifact")
                .GetProperty("apphost")
                .GetString();
            appHost.ShouldNotBeNull();
            appHost.ShouldContain(runtimeIdentifier);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - InProcess project resources require explicit opt-in")]
    public async Task Build_InProcessProviderWithoutOptIn_ReportsCOHGW002()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "GatewaySmokeDatabase",
            "GatewaySmokeSupport",
            "GatewaySmokeWeb",
            "InProcessNamedEntry",
            "InProcessNonComposable",
            "InProcessGateway");
        string projectPath = workspace.ProjectFile("InProcessGateway");
        string project = File.ReadAllText(projectPath);
        File.WriteAllText(
            projectPath,
            project.Replace(
                "    <CohesionGatewayInProcess>true</CohesionGatewayInProcess>\r\n",
                string.Empty,
                StringComparison.Ordinal).Replace(
                "    <CohesionGatewayInProcess>true</CohesionGatewayInProcess>\n",
                string.Empty,
                StringComparison.Ordinal));

        // Act
        DotNetBuildResult result = await workspace.BuildAsync(
            "InProcessGateway",
            cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("COHGW002");
        result.Output.ShouldContain("Set CohesionGatewayInProcess=true");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - InProcess opt-in without provider keeps resources build-only")]
    public async Task Build_InProcessOptInWithoutProvider_DoesNotActivateRuntimeClosure()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "GatewaySmokeDatabase",
            "GatewaySmokeSupport",
            "GatewaySmokeWeb",
            "InProcessNamedEntry",
            "InProcessNonComposable",
            "InProcessGateway");
        string projectPath = workspace.ProjectFile("InProcessGateway");
        string project = File.ReadAllText(projectPath);
        File.WriteAllText(
            projectPath,
            project.Replace(
                "<CohesionGateways>InProcess</CohesionGateways>",
                "<CohesionGateways>Local</CohesionGateways>",
                StringComparison.Ordinal));

        // Act
        DotNetBuildResult result = await workspace.BuildAsync(
            "InProcessGateway",
            cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        string source = File.ReadAllText(GeneratedOutput(
            workspace.ProjectDirectory("InProcessGateway"),
            "Gateway.g.cs"));
        source.ShouldNotContain("Gateway.InProcess");
        source.ShouldNotContain("CohesionResourceEntry");

        string outputDirectory = workspace.BuildOutputDirectory("InProcessGateway");
        File.Exists(Path.Combine(outputDirectory, "GatewaySmokeWeb.dll")).ShouldBeFalse();
        File.Exists(Path.Combine(outputDirectory, "GatewaySmokeDatabase.dll")).ShouldBeFalse();
        File.Exists(Path.Combine(outputDirectory, "InProcessNamedEntry.dll")).ShouldBeFalse();
        File.Exists(Path.Combine(outputDirectory, "NUlid.dll")).ShouldBeFalse();
        FindNativeSqliteLibrary(outputDirectory).ShouldBeNull();
    }

    private static string GeneratedOutput(string projectDirectory, string fileName)
    {
        string path = Path.Combine(
            projectDirectory,
            "obj",
            "Debug",
            ConsumerWorkspace.TargetFramework,
            ConsumerWorkspace.HostRuntimeIdentifier,
            "cohesion",
            fileName);
        File.Exists(path).ShouldBeTrue($"Expected generated output '{path}'.");
        return path;
    }

    private static string GeneratedOutput(
        string projectDirectory,
        string fileName,
        string runtimeIdentifier)
    {
        string path = Path.Combine(
            projectDirectory,
            "obj",
            "Debug",
            ConsumerWorkspace.TargetFramework,
            runtimeIdentifier,
            "cohesion",
            fileName);
        File.Exists(path).ShouldBeTrue($"Expected generated output '{path}'.");
        return path;
    }

    private static string EntryAnchor(string rootNamespace, string assemblyName) =>
        $"typeof(global::{rootNamespace}.CohesionResourceEntry{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(assemblyName))}).Assembly";

    private static string? FindNativeSqliteLibrary(
        string directory,
        string? runtimeIdentifier = null)
    {
        string fileName = runtimeIdentifier?.StartsWith("win-", StringComparison.OrdinalIgnoreCase) is true
            ? "e_sqlite3.dll"
            : runtimeIdentifier?.StartsWith("osx-", StringComparison.OrdinalIgnoreCase) is true
                ? "libe_sqlite3.dylib"
                : runtimeIdentifier is not null
                    ? "libe_sqlite3.so"
                    : OperatingSystem.IsWindows()
                        ? "e_sqlite3.dll"
                        : OperatingSystem.IsMacOS()
                            ? "libe_sqlite3.dylib"
                            : "libe_sqlite3.so";
        return Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static bool ContainsOrdinalIgnoreCase(string[] values, string expected)
    {
        return values.Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
    }

}
