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
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("EnabledDatabase", "EnabledWeb");

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
        web.GetProperty("endpoints").EnumerateArray().Single().GetProperty("name").GetString().ShouldBe("http");
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
        database.GetProperty("mounts").EnumerateArray().Single().GetProperty("size").GetString().ShouldBe("10Gi");
        database.GetProperty("lifecycle").GetProperty("workload").GetString().ShouldBe("StatefulSet");
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
}
