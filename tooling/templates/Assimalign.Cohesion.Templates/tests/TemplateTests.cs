using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Templates.Tests;

/// <summary>Verifies the package as a dotnet new consumer, including both landing-zone topologies.</summary>
public sealed class TemplateTests : IClassFixture<TemplatePackageFixture>
{
    private readonly TemplateWorkspace _workspace;

    /// <summary>Uses the test collection's private template installation.</summary>
    /// <param name="fixture">The packed and installed template package.</param>
    public TemplateTests(TemplatePackageFixture fixture) => _workspace = fixture.Workspace;

    /// <summary>Instantiates the complete roster and checks every feed-independent scaffold contract.</summary>
    /// <returns>A task representing package instantiation and structural verification.</returns>
    [Fact(DisplayName = "Cohesion Test [Templates] - Instantiate: Every template follows the scaffold contract")]
    public async Task Instantiate_AllTemplates_ShouldFollowScaffoldContractAsync()
    {
        // Arrange: the source roster and unique identities must match the signed-off design exactly.
        string[] configs = Directory.GetFiles(TemplateRepository.ContentRoot, "template.json", SearchOption.AllDirectories);
        configs.Length.ShouldBe(TemplateRepository.Roster.Length);
        string[] identities = configs.Select(file =>
        {
            using JsonDocument config = TemplateRepository.ReadJson(file);
            return config.RootElement.GetProperty("identity").GetString()!;
        }).ToArray();
        identities.Distinct(StringComparer.Ordinal).Count().ShouldBe(identities.Length);
        configs.Select(file => Directory.GetParent(Path.GetDirectoryName(file)!)!.Name).Order().ToArray()
            .ShouldBe(TemplateRepository.Roster.Order().ToArray());
        AssertNoRetiredContent(TemplateRepository.ContentRoot, emitted: false);

        // Act and assert: the installed package, rather than raw content copying, owns every output.
        string? singleLanding = null;
        foreach (string template in TemplateRepository.Roster)
        {
            string name = template == "cohesion-app" ? "Acme" : template == "cohesion-landing-zone" ? "Example" : "Northwind.Service";
            string output = await _workspace.InstantiateAsync(template, name, cancellationToken: CancellationToken.None);
            AssertScaffold(output, template, name);
            if (template == "cohesion-landing-zone")
            {
                singleLanding = output;
            }
        }

        string federated = await _workspace.InstantiateAsync("cohesion-landing-zone", "Example", "federated");
        AssertScaffold(federated, "cohesion-landing-zone", "Example", "federated");
        AssertTopologyDifferences(singleLanding!, federated);
    }

    /// <summary>Checks name replacement and the explicit gateway application-name override.</summary>
    /// <returns>A task representing template instantiation and identity checks.</returns>
    [Fact(DisplayName = "Cohesion Test [Templates] - Identity: Name replacement and gateway override are explicit")]
    public async Task Instantiate_CustomIdentity_ShouldReplaceNamesAsync()
    {
        string application = await _workspace.InstantiateAsync("cohesion-app", "Northwind");
        AssertScaffold(application, "cohesion-app", "Northwind");
        File.Exists(Path.Combine(application, "Northwind.Api", "Northwind.Api.csproj")).ShouldBeTrue();
        XDocument.Load(Path.Combine(application, "Directory.Build.props")).Descendants("CohesionApplication").Single().Value.ShouldBe("northwind");
        string landing = await _workspace.InstantiateAsync("cohesion-landing-zone", "Northwind", "federated");
        AssertScaffold(landing, "cohesion-landing-zone", "Northwind", "federated");
        string gateway = await _workspace.InstantiateAsync("cohesion-gateway", "Northwind.Gateway", applicationName: "orders");
        AssertScaffold(gateway, "cohesion-gateway", "Northwind.Gateway", applicationName: "orders");
        XDocument.Load(Path.Combine(gateway, "Northwind.Gateway.csproj")).Descendants("CohesionApplicationName").Single().Value.ShouldBe("orders");
    }

    private async Task BuildTemplateAsync(string template, string? topology = null, CancellationToken cancellationToken = default)
    {
        string name = template == "cohesion-app" ? "Acme" : template == "cohesion-landing-zone" ? "Example" : "Northwind.Service";
        string output = await _workspace.InstantiateAsync(template, name, topology, cancellationToken: cancellationToken);
        // Assert canonical shipped pins before the single feed-version substitution.
        AssertScaffold(output, template, name, topology ?? "single");
        await _workspace.BuildAsync(output, cancellationToken);
        if (template == "cohesion-spa")
        {
            Directory.GetFiles(Path.Combine(output, "bin"), "index.html", SearchOption.AllDirectories)
                .ShouldNotBeEmpty("The standalone SPA must carry the page served by Program.cs into its output.");
        }
        foreach (string project in SourceProjects(output))
        {
            XDocument document = XDocument.Load(project);
            bool enabled = document.Root!.Attribute("Sdk")!.Value.EndsWith(".Gateway", StringComparison.Ordinal)
                || document.Descendants("CohesionApplicationModel").Single().Value == "enabled";
            string obj = Path.Combine(Path.GetDirectoryName(project)!, "obj");
            string[] manifests = Directory.Exists(obj)
                ? Directory.GetFiles(obj, "resource.json", SearchOption.AllDirectories)
                    .Where(file => Path.GetFileName(Path.GetDirectoryName(file)) == "cohesion").ToArray()
                : [];
            if (enabled)
            {
                manifests.ShouldNotBeEmpty($"{project} must generate its opted-in manifest.");
            }
            else
            {
                manifests.ShouldBeEmpty($"{project} must remain a plain executable.");
                Directory.GetFiles(obj, "Resource*.g.cs", SearchOption.AllDirectories).ShouldBeEmpty();
            }
        }
    }

    private static void AssertScaffold(string output, string template, string name, string topology = "single", string? applicationName = null)
    {
        using JsonDocument global = TemplateRepository.ReadJson(Path.Combine(output, "global.json"));
        using JsonDocument repositoryGlobal = TemplateRepository.ReadJson(Path.Combine(TemplateRepository.Root, "global.json"));
        JsonProperty[] pins = global.RootElement.GetProperty("msbuild-sdks").EnumerateObject().ToArray();
        pins.Select(pin => pin.Name).Order().ToArray().ShouldBe(TemplateRepository.SdkIds().Order().ToArray());
        pins.Length.ShouldBe(20);
        foreach (JsonProperty pin in pins)
        {
            pin.Value.GetString().ShouldBe(TemplateRepository.Version);
        }

        foreach (string property in new[] { "version", "rollForward" })
        {
            global.RootElement.GetProperty("sdk").GetProperty(property).GetString()
                .ShouldBe(repositoryGlobal.RootElement.GetProperty("sdk").GetProperty(property).GetString());
        }

        string[] projects = SourceProjects(output);
        projects.Length.ShouldBe(template == "cohesion-app" ? 3 : template == "cohesion-landing-zone" ? topology == "federated" ? 24 : 25 : 1);
        bool multi = template is "cohesion-app" or "cohesion-landing-zone";
        foreach (string project in projects)
        {
            File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "Program.cs")).ShouldBeTrue(project);
            XDocument document = XDocument.Load(project);
            string sdk = document.Root!.Attribute("Sdk")!.Value;
            if (sdk.EndsWith(".Gateway", StringComparison.Ordinal))
            {
                document.Descendants("CohesionApplicationModel").ShouldBeEmpty();
                document.Descendants("CohesionResourceReferencesAreRuntime").ShouldBeEmpty();
                string relativeProject = Path.GetRelativePath(output, project).Replace('\\', '/');
                bool localOnly = template == "cohesion-landing-zone"
                    && (relativeProject.StartsWith("Networking/", StringComparison.Ordinal)
                        || relativeProject.StartsWith("Gateway/", StringComparison.Ordinal));
                document.Descendants("CohesionGateways").Single().Value.ShouldBe(localOnly ? "Local" : "Local;InProcess");
                if (!localOnly)
                {
                    document.Descendants("CohesionGatewayInProcess").Single().Value.ShouldBe("true");
                }
            }
            else
            {
                document.Descendants("CohesionApplicationModel").Single().Value.ShouldBe(multi ? "enabled" : "disabled");
                if (!multi)
                {
                    string text = File.ReadAllText(project);
                    text.ShouldContain("<CohesionApplicationModel>enabled</CohesionApplicationModel>", Case.Sensitive);
                    text.ShouldContain("ResourceControlPlane.g.cs", Case.Sensitive);
                    text.ShouldContain($"Assimalign.Cohesion.{sdk.Split('.').Last()}.ApplicationModel", Case.Sensitive);
                    File.ReadAllText(Path.Combine(Path.GetDirectoryName(project)!, "Program.cs")).ShouldNotContain("Resource.", Case.Sensitive);
                }
            }
        }

        foreach (string file in projects.Concat(Directory.GetFiles(output, "Directory.Build.props", SearchOption.AllDirectories)))
        {
            XDocument document = XDocument.Load(file);
            foreach (string property in TemplateRepository.SdkDefaults)
            {
                document.Descendants(property).ShouldBeEmpty($"{file} duplicates SDK-owned {property}.");
            }

            document.Descendants("ProjectReference").ShouldBeEmpty();
            document.Descendants("PackageReference").ShouldBeEmpty();
        }

        string app = multi ? name.ToLowerInvariant() : applicationName ?? name.Split('.')[0].ToLowerInvariant();
        XDocument.Load(Path.Combine(output, "Directory.Build.props")).Descendants("CohesionApplication").Single().Value.ShouldBe(app);
        if (template == "cohesion-landing-zone")
        {
            foreach (string domain in new[] { "Identity", "Networking", "Platform", "Zones/AppA", "Zones/AppB", "Zones/AppC" })
            {
                XDocument props = XDocument.Load(Path.Combine(output, domain, "Directory.Build.props"));
                props.Descendants("CohesionApplication").Single().Value.ShouldBe(domain.Split('/').Last().ToLowerInvariant());
                props.Descendants("Import").Single().Attribute("Project")!.Value.ShouldContain("GetPathOfFileAbove", Case.Sensitive);
            }

            Directory.Exists(Path.Combine(output, "Gateway")).ShouldBe(topology == "single");
            File.Exists(Path.Combine(output, name + (topology == "single" ? ".K8s.slnx" : ".Federated.slnx"))).ShouldBeTrue();
            Directory.Exists(Path.Combine(output, ".topologies")).ShouldBeFalse();
        }

        string ignore = File.ReadAllText(Path.Combine(output, ".gitignore"));
        ignore.ShouldContain(".cohesion/", Case.Sensitive);
        ignore.ShouldContain("parameters.json", Case.Sensitive);
        File.Exists(Path.Combine(output, ".github", "workflows", "credential-guard.yml")).ShouldBeTrue();
        XDocument nuget = XDocument.Load(Path.Combine(output, "nuget.config"));
        nuget.Descendants("packageSource" + "Credentials").ShouldBeEmpty();
        foreach (XElement source in nuget.Descendants("packageSources").Elements("add"))
        {
            source.Attribute("key")!.Value.ShouldNotBe("cohesion-local");
            source.Attribute("key")!.Value.ShouldNotBe("cohesion-smoke");
            source.Attribute("value")!.Value.ShouldStartWith("https://", Case.Sensitive);
        }

        nuget.Descendants("packageSource").Single(source => source.Attribute("key")!.Value == "nuget.org")
            .Elements("package").Select(package => package.Attribute("pattern")!.Value).ToArray()
            .ShouldBe(["Assimalign.Cohesion.*", "*"]);
        AssertNoRetiredContent(output, emitted: true);
    }

    private static string[] SourceProjects(string output) => Directory.GetFiles(output, "*.csproj", SearchOption.AllDirectories);

    private static void AssertTopologyDifferences(string single, string federated)
    {
        string[] singleFiles = Directory.GetFiles(single, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(single, file).Replace('\\', '/')).ToArray();
        string[] federatedFiles = Directory.GetFiles(federated, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(federated, file).Replace('\\', '/')).ToArray();
        singleFiles.Except(federatedFiles).Order().ToArray().ShouldBe(new[]
        {
            "Example.K8s.slnx", "Gateway/Example.Gateway/Example.Gateway.csproj",
            "Gateway/Example.Gateway/Program.cs", "Gateway/Example.Gateway/appsettings.json"
        }.Order().ToArray());
        federatedFiles.Except(singleFiles).ToArray().ShouldBe(["Example.Federated.slnx"]);
        string[] differences = singleFiles.Intersect(federatedFiles)
            .Where(file => File.ReadAllText(Path.Combine(single, file)) != File.ReadAllText(Path.Combine(federated, file)))
            .Order().ToArray();
        differences.ShouldBe(new[]
        {
            "README.md", "Identity/Example.Identity.Gateway/appsettings.json",
            "Networking/Example.Networking.Gateway/appsettings.json", "Platform/Example.Platform.Gateway/appsettings.json",
            "Zones/AppA/Example.AppA.Gateway/appsettings.json", "Zones/AppB/Example.AppB.Gateway/appsettings.json",
            "Zones/AppC/Example.AppC.Gateway/appsettings.json"
        }.Order().ToArray());
    }

    private static void AssertNoRetiredContent(string directory, bool emitted)
    {
        foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            Path.GetFileName(file).ShouldNotBe("Resource.cs");
            Path.GetFileName(file).ShouldNotBe("Gateway.cs");
            string text = File.ReadAllText(file);
            text.ShouldNotContain("Aspire");
            text.ShouldNotContain("!!REPLACE_WITH_", Case.Sensitive);
            foreach (string token in new[] { "packageSource" + "Credentials", "ClearText" + "Password", "gh" + "p_", "github_" + "pat_" })
            {
                text.ShouldNotContain(token, Case.Sensitive);
            }

            if (emitted)
            {
                text.ShouldNotContain("__COHESION_", Case.Sensitive);
                text.ShouldNotContain("__APPLICATION_NAME__", Case.Sensitive);
                text.ShouldNotContain("__ORGANIZATION_NAME__", Case.Sensitive);
                text.ShouldNotContain("__RESOURCE_NAME__", Case.Sensitive);
            }
        }
    }

    /// <summary>Builds the app scaffold against its available package closure.</summary>
    /// <returns>A task representing consumer build and manifest verification.</returns>
    [TemplateFeedFact("cohesion-app", DisplayName = "Cohesion Test [Templates] - Build: app generates only enabled manifests")]
    public Task Build_App_ShouldHonorOrchestrationAsync() => BuildTemplateAsync("cohesion-app");

    /// <summary>Builds the landing-zone scaffold against its available package closure.</summary>
    /// <returns>A task representing consumer build and manifest verification.</returns>
    [TemplateFeedFact("cohesion-landing-zone", DisplayName = "Cohesion Test [Templates] - Build: landing-zone generates only enabled manifests")]
    public Task Build_LandingZoneSingle_ShouldHonorOrchestrationAsync() => BuildTemplateAsync("cohesion-landing-zone");

    /// <summary>Builds the landing-zone:federated scaffold against its available package closure.</summary>
    /// <returns>A task representing consumer build and manifest verification.</returns>
    [TemplateFeedFact("cohesion-landing-zone", DisplayName = "Cohesion Test [Templates] - Build: landing-zone:federated generates only enabled manifests")]
    public Task Build_LandingZoneFederated_ShouldHonorOrchestrationAsync() => BuildTemplateAsync("cohesion-landing-zone", "federated");

    /// <summary>Builds the gateway scaffold against its available package closure.</summary>
    /// <returns>A task representing consumer build and manifest verification.</returns>
    [TemplateFeedFact("cohesion-gateway", DisplayName = "Cohesion Test [Templates] - Build: gateway generates only enabled manifests")]
    public Task Build_Gateway_ShouldHonorOrchestrationAsync() => BuildTemplateAsync("cohesion-gateway");

    /// <summary>Builds the composite scaffold against its available package closure.</summary>
    /// <returns>A task representing consumer build and manifest verification.</returns>
    [TemplateFeedFact("cohesion-composite", DisplayName = "Cohesion Test [Templates] - Build: composite generates only enabled manifests")]
    public Task Build_Composite_ShouldHonorOrchestrationAsync() => BuildTemplateAsync("cohesion-composite");

    /// <summary>Builds the web scaffold against its available package closure.</summary>
    /// <returns>A task representing consumer build and manifest verification.</returns>
    [TemplateFeedFact("cohesion-web", DisplayName = "Cohesion Test [Templates] - Build: web generates only enabled manifests")]
    public Task Build_Web_ShouldHonorOrchestrationAsync() => BuildTemplateAsync("cohesion-web");

    /// <summary>Builds the spa scaffold against its available package closure.</summary>
    /// <returns>A task representing consumer build and manifest verification.</returns>
    [TemplateFeedFact("cohesion-spa", DisplayName = "Cohesion Test [Templates] - Build: spa generates only enabled manifests")]
    public Task Build_Spa_ShouldHonorOrchestrationAsync() => BuildTemplateAsync("cohesion-spa");

    /// <summary>Builds the database scaffold against its available package closure.</summary>
    /// <returns>A task representing consumer build and manifest verification.</returns>
    [TemplateFeedFact("cohesion-database", DisplayName = "Cohesion Test [Templates] - Build: database generates only enabled manifests")]
    public Task Build_Database_ShouldHonorOrchestrationAsync() => BuildTemplateAsync("cohesion-database");

    /// <summary>Builds the secretstore scaffold against its available package closure.</summary>
    /// <returns>A task representing consumer build and manifest verification.</returns>
    [TemplateFeedFact("cohesion-secretstore", DisplayName = "Cohesion Test [Templates] - Build: secretstore generates only enabled manifests")]
    public Task Build_SecretStore_ShouldHonorOrchestrationAsync() => BuildTemplateAsync("cohesion-secretstore");

    /// <summary>Builds the configurationstore scaffold against its available package closure.</summary>
    /// <returns>A task representing consumer build and manifest verification.</returns>
    [TemplateFeedFact("cohesion-configurationstore", DisplayName = "Cohesion Test [Templates] - Build: configurationstore generates only enabled manifests")]
    public Task Build_ConfigurationStore_ShouldHonorOrchestrationAsync() => BuildTemplateAsync("cohesion-configurationstore");

    /// <summary>Builds the identityhub scaffold against its available package closure.</summary>
    /// <returns>A task representing consumer build and manifest verification.</returns>
    [TemplateFeedFact("cohesion-identityhub", DisplayName = "Cohesion Test [Templates] - Build: identityhub generates only enabled manifests")]
    public Task Build_IdentityHub_ShouldHonorOrchestrationAsync() => BuildTemplateAsync("cohesion-identityhub");

    /// <summary>Builds the rezolvr scaffold against its available package closure.</summary>
    /// <returns>A task representing consumer build and manifest verification.</returns>
    [TemplateFeedFact("cohesion-rezolvr", DisplayName = "Cohesion Test [Templates] - Build: rezolvr generates only enabled manifests")]
    public Task Build_Rezolvr_ShouldHonorOrchestrationAsync() => BuildTemplateAsync("cohesion-rezolvr");
}
