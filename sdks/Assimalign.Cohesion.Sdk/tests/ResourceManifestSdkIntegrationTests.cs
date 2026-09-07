using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Tests;

public sealed class ResourceManifestSdkIntegrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk] - enabled resources generate schema-valid manifests and typed accessors")]
    public async Task Build_EnabledWebReferencesEnabledDatabase_GeneratesValidManifestsAndTypedAccessors()
    {
        // Arrange
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "EnabledDependency",
            "EnabledDatabase",
            "EnabledWeb");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("EnabledWeb");

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        string webProject = workspace.ProjectDirectory("EnabledWeb");
        string databaseProject = workspace.ProjectDirectory("EnabledDatabase");
        string webManifestPath = GeneratedOutput(webProject, "resource.json");
        string databaseManifestPath = GeneratedOutput(databaseProject, "resource.json");

        File.Exists(GeneratedOutput(webProject, "Resource.g.cs")).ShouldBeTrue(result.Output);
        File.Exists(GeneratedOutput(webProject, "ResourceControlPlane.g.cs")).ShouldBeTrue(result.Output);
        File.Exists(GeneratedOutput(databaseProject, "Resource.g.cs")).ShouldBeTrue(result.Output);
        File.Exists(GeneratedOutput(databaseProject, "ResourceControlPlane.g.cs")).ShouldBeTrue(result.Output);
        Directory.EnumerateFiles(Path.Combine(webProject, "bin"), "EnabledWeb.dll", SearchOption.AllDirectories)
            .ShouldHaveSingleItem();

        string webResourceSource = File.ReadAllText(GeneratedOutput(webProject, "Resource.g.cs"));
        webResourceSource.ShouldContain("global::Assimalign.Cohesion.Hosting.ResourceRuntime.Current.GetEndpoint");
        webResourceSource.ShouldContain("global::Assimalign.Cohesion.Hosting.ResourceRuntime.Current.GetMount");
        webResourceSource.ShouldContain("global::Assimalign.Cohesion.Hosting.ResourceRuntime.Current.GetSetting");
        webResourceSource.ShouldContain("global::Assimalign.Cohesion.Hosting.ResourceRuntime.Current.GetReference");
        webResourceSource.ShouldContain("global::Assimalign.Cohesion.Hosting.ResourceRuntime.Current.GetConnectionFactory");
        webResourceSource.ShouldNotContain("Connections.Tcp.TcpConnectionFactory");
        webResourceSource.ShouldNotContain("ResourceContextShim");
        webResourceSource.ShouldNotContain("ProcessEnvironmentResourceContext");

        string webControlPlaneSource = File.ReadAllText(GeneratedOutput(webProject, "ResourceControlPlane.g.cs"));
        webControlPlaneSource.ShouldContain("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        webControlPlaneSource.ShouldContain("ResourceRuntime.RegisterControlPlane(");
        webControlPlaneSource.ShouldContain(
            "global::Assimalign.Cohesion.Web.ApplicationModel.WebResourceControlPlane.Create()");
        webControlPlaneSource.ShouldContain("context.TryGetEndpoint(\"http\", \"http\", 18080");
        webControlPlaneSource.ShouldContain("static () => CreateControlPlane(),");
        webControlPlaneSource.ShouldContain("            30);");
        webControlPlaneSource.ShouldNotContain("#if");

        string databaseControlPlaneSource = File.ReadAllText(GeneratedOutput(databaseProject, "ResourceControlPlane.g.cs"));
        databaseControlPlaneSource.ShouldContain(
            "global::Assimalign.Cohesion.Database.ApplicationModel.DatabaseResourceControlPlane.Create()");
        databaseControlPlaneSource.ShouldContain("context.TryGetEndpoint(\"db\", \"cohesion-db\", 15740");
        databaseControlPlaneSource.ShouldContain("context.TryGetEndpoint(\"admin\", \"http\", null");
        string databaseResourceSource = File.ReadAllText(GeneratedOutput(databaseProject, "Resource.g.cs"));
        databaseResourceSource.ShouldContain("ResourceRuntime.Current.GetConnectionFactory");
        databaseResourceSource.ShouldNotContain("Connections.Tcp.TcpConnectionFactory");
        AssetsContainPackage(webProject, "Assimalign.Cohesion.Web.ApplicationModel").ShouldBeTrue();
        AssetsContainPackage(databaseProject, "Assimalign.Cohesion.Database.ApplicationModel").ShouldBeTrue();

        File.Exists(ConsumerWorkspace.ResourceSchemaPath)
            .ShouldBeTrue($"Resource schema was not found at '{ConsumerWorkspace.ResourceSchemaPath}'.");
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(ConsumerWorkspace.ResourceSchemaPath));
        using JsonDocument webManifest = JsonDocument.Parse(File.ReadAllText(webManifestPath));
        using JsonDocument databaseManifest = JsonDocument.Parse(File.ReadAllText(databaseManifestPath));

        Action validateWebManifest = () => FocusedJsonSchemaValidator.Validate(webManifest.RootElement, schema.RootElement);
        Action validateDatabaseManifest = () => FocusedJsonSchemaValidator.Validate(databaseManifest.RootElement, schema.RootElement);
        validateWebManifest.ShouldNotThrow();
        validateDatabaseManifest.ShouldNotThrow();

        JsonElement web = webManifest.RootElement;
        web.GetProperty("schema").GetString().ShouldBe("cohesion/resource/v1");
        web.GetProperty("name").GetString().ShouldBe("inventory-web");
        web.GetProperty("kind").GetString().ShouldBe("Web");
        web.GetProperty("application").GetString().ShouldBe("inventory");
        web.GetProperty("applicationModel").GetString().ShouldBe("Assimalign.Cohesion.Web.ApplicationModel");
        web.GetProperty("artifact").GetProperty("composable").GetBoolean().ShouldBeTrue();
        JsonElement webEndpoint = web.GetProperty("endpoints").EnumerateArray().Single();
        webEndpoint.GetProperty("name").GetString().ShouldBe("http");
        webEndpoint.GetProperty("scheme").GetString().ShouldBe("http");
        webEndpoint.GetProperty("protocol").GetString().ShouldBe("tcp");
        webEndpoint.GetProperty("containerPort").GetInt32().ShouldBe(8080);
        webEndpoint.GetProperty("devPort").GetInt32().ShouldBe(18080);
        webEndpoint.GetProperty("public").GetBoolean().ShouldBeFalse();

        JsonElement webControlPlane = web.GetProperty("controlPlane");
        webControlPlane.GetProperty("endpoint").GetString().ShouldBe("http");
        webControlPlane.GetProperty("path").GetString().ShouldBe("/cohesion/v1");

        JsonElement webProbes = web.GetProperty("probes");
        webProbes.GetProperty("readiness").GetProperty("endpoint").GetString().ShouldBe("http");
        webProbes.GetProperty("readiness").GetProperty("http").GetString().ShouldBe("/readyz");
        webProbes.GetProperty("liveness").GetProperty("endpoint").GetString().ShouldBe("http");
        webProbes.GetProperty("liveness").GetProperty("http").GetString().ShouldBe("/livez");

        JsonElement webLifecycle = web.GetProperty("lifecycle");
        webLifecycle.GetProperty("workload").GetString().ShouldBe("Deployment");
        webLifecycle.GetProperty("replicas").GetInt32().ShouldBe(1);
        webLifecycle.GetProperty("maxReplicas").ValueKind.ShouldBe(JsonValueKind.Null);
        webLifecycle.GetProperty("stopGraceSeconds").GetInt32().ShouldBe(30);
        webLifecycle.GetProperty("restartPolicy").GetString().ShouldBe("OnFailure");
        web.GetProperty("mounts").EnumerateArray().Single().GetProperty("name").GetString().ShouldBe("cache");
        web.GetProperty("settings").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe("Orders:PageSize");
        web.GetProperty("properties").GetProperty("web.kind").GetString().ShouldBe("Api");

        JsonElement reference = web.GetProperty("references").EnumerateArray().Single();
        reference.GetProperty("resource").GetString().ShouldBe("inventory-database");
        reference.GetProperty("application").GetString().ShouldBe("inventory");
        reference.GetProperty("endpoints").EnumerateArray().Single().GetString().ShouldBe("db");
        reference.GetProperty("optional").GetBoolean().ShouldBeFalse();
        reference.GetProperty("manifest").GetString().ShouldBe("EnabledDatabase");

        JsonElement database = databaseManifest.RootElement;
        database.GetProperty("kind").GetString().ShouldBe("Database");
        database.GetProperty("applicationModel").GetString().ShouldBe("Assimalign.Cohesion.Database.ApplicationModel");
        database.GetProperty("artifact").GetProperty("composable").GetBoolean().ShouldBeTrue();

        JsonElement databaseEndpoint = database
            .GetProperty("endpoints")
            .EnumerateArray()
            .Single(endpoint => endpoint.GetProperty("name").GetString() == "db");
        databaseEndpoint.GetProperty("scheme").GetString().ShouldBe("cohesion-db");
        databaseEndpoint.GetProperty("protocol").GetString().ShouldBe("tcp");
        databaseEndpoint.GetProperty("containerPort").GetInt32().ShouldBe(5740);
        databaseEndpoint.GetProperty("devPort").GetInt32().ShouldBe(15740);
        databaseEndpoint.GetProperty("public").GetBoolean().ShouldBeFalse();

        JsonElement adminEndpoint = database
            .GetProperty("endpoints")
            .EnumerateArray()
            .Single(endpoint => endpoint.GetProperty("name").GetString() == "admin");
        adminEndpoint.GetProperty("scheme").GetString().ShouldBe("http");
        adminEndpoint.GetProperty("protocol").GetString().ShouldBe("tcp");
        adminEndpoint.GetProperty("containerPort").GetInt32().ShouldBe(8081);
        adminEndpoint.GetProperty("public").GetBoolean().ShouldBeFalse();

        JsonElement databaseControlPlane = database.GetProperty("controlPlane");
        databaseControlPlane.GetProperty("endpoint").GetString().ShouldBe("admin");
        databaseControlPlane.GetProperty("path").GetString().ShouldBe("/cohesion/v1");

        JsonElement databaseProbes = database.GetProperty("probes");
        databaseProbes.GetProperty("readiness").GetProperty("endpoint").GetString().ShouldBe("admin");
        databaseProbes.GetProperty("readiness").GetProperty("http").GetString().ShouldBe("/readyz");
        databaseProbes.GetProperty("liveness").GetProperty("endpoint").GetString().ShouldBe("admin");
        databaseProbes.GetProperty("liveness").GetProperty("http").GetString().ShouldBe("/livez");

        JsonElement dataMount = database.GetProperty("mounts").EnumerateArray().Single();
        dataMount.GetProperty("name").GetString().ShouldBe("data");
        dataMount.GetProperty("kind").GetString().ShouldBe("Volume");
        dataMount.GetProperty("containerPath").GetString().ShouldBe("/data");
        dataMount.GetProperty("size").GetString().ShouldBe("10Gi");

        JsonElement databaseLifecycle = database.GetProperty("lifecycle");
        databaseLifecycle.GetProperty("workload").GetString().ShouldBe("StatefulSet");
        databaseLifecycle.GetProperty("replicas").GetInt32().ShouldBe(1);
        databaseLifecycle.GetProperty("maxReplicas").GetInt32().ShouldBe(1);
        databaseLifecycle.GetProperty("stopGraceSeconds").GetInt32().ShouldBe(30);
        databaseLifecycle.GetProperty("restartPolicy").GetString().ShouldBe("OnFailure");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - enabled generic resources keep generated accessors without a control-plane registration")]
    public async Task Build_EnabledGenericResource_GeneratesAccessorsAndInertControlPlaneSource()
    {
        // Arrange
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("EnabledGeneric");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("EnabledGeneric");

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        string projectDirectory = workspace.ProjectDirectory("EnabledGeneric");
        File.Exists(GeneratedOutput(projectDirectory, "resource.json")).ShouldBeTrue(result.Output);
        File.Exists(GeneratedOutput(projectDirectory, "Resource.g.cs")).ShouldBeTrue(result.Output);

        string controlPlaneSource = File.ReadAllText(
            GeneratedOutput(projectDirectory, "ResourceControlPlane.g.cs"));
        controlPlaneSource.ShouldNotContain("ModuleInitializer");
        controlPlaneSource.ShouldNotContain("RegisterControlPlane");
        AssetsContainPackage(projectDirectory, "Assimalign.Cohesion.Worker.ApplicationModel").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - disabled application model produces no resource outputs")]
    public async Task Build_DisabledApplicationModel_ProducesNoManifestOrGeneratedSources()
    {
        // Arrange
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("DisabledResource");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("DisabledResource");

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        string projectDirectory = workspace.ProjectDirectory("DisabledResource");
        Directory.EnumerateFiles(projectDirectory, "resource.json", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.EnumerateFiles(projectDirectory, "Resource.g.cs", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.EnumerateFiles(projectDirectory, "ResourceControlPlane.g.cs", SearchOption.AllDirectories).ShouldBeEmpty();
        AssetsContainPackage(projectDirectory, "Assimalign.Cohesion.Web.ApplicationModel").ShouldBeFalse();
        result.Output.ShouldNotContain("COHSDK");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - referencing a disabled resource reports COHSDK001")]
    public async Task Build_ResourceReferenceTargetsDisabledProject_ReportsCOHSDK001()
    {
        // Arrange
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("DisabledReferenced", "DisabledReferrer");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("DisabledReferrer");

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.Contains("COHSDK001", StringComparison.Ordinal).ShouldBeTrue(result.Output);
        result.Output.ShouldContain(
            "DisabledReferenced has CohesionApplicationModel disabled; set it to enabled to reference it");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - enabled library output reports COHSDK008")]
    public async Task Build_EnabledApplicationModelUsesLibraryOutput_ReportsCOHSDK008()
    {
        // Arrange
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("InvalidOutputType");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("InvalidOutputType");

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("COHSDK008");
        result.Output.ShouldContain("OutputType is 'Library'");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - foreign resource property reports COHSDK009")]
    public async Task Build_ResourcePropertyUsesForeignKindPrefix_ReportsCOHSDK009()
    {
        // Arrange
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("ForeignProperty");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("ForeignProperty");

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("COHSDK009");
        result.Output.ShouldContain("database.engine");
        result.Output.ShouldContain("web.");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - unknown endpoint metadata identifies the item and metadata")]
    public async Task Build_EndpointUsesUnknownMetadata_ReportsItemAndMetadataNames()
    {
        // Arrange
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("UnknownEndpointMetadata");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("UnknownEndpointMetadata");

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("Unknown metadata 'HealthPath' on CohesionEndpoint 'http'.");
    }

    private static string GeneratedOutput(string projectDirectory, string fileName)
    {
        string path = Path.Combine(projectDirectory, "obj", "Debug", "net10.0", "cohesion", fileName);
        File.Exists(path).ShouldBeTrue($"Expected generated output '{path}'.");
        return path;
    }

    private static bool AssetsContainPackage(string projectDirectory, string packageId)
    {
        string assetsPath = Path.Combine(projectDirectory, "obj", "project.assets.json");
        File.Exists(assetsPath).ShouldBeTrue($"Expected restore assets '{assetsPath}'.");

        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        return assets.RootElement
            .GetProperty("libraries")
            .EnumerateObject()
            .Any(library => library.Name.StartsWith($"{packageId}/", StringComparison.OrdinalIgnoreCase));
    }
}
