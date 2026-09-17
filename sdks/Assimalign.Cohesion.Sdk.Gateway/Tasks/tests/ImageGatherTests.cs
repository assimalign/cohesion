using System;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Gateway.Tests;

/// <summary>Tests the packaged gateway gather using contract-valid immutable producer fixtures.</summary>
public sealed class ImageGatherTests
{
    /// <summary>Real project producers exercise the complete restore, source closure, and gather boundary.</summary>
    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - Images: Should publish and gather real referenced Linux resource archives")]
    public async Task Publish_SourceClosure_ShouldGatherRealArchivesAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("AppAGateway", "AppAWeb", "PlatformDatabase");
        File.WriteAllText(Path.Combine(workspace.RootDirectory, "Directory.Build.props"),
            "<Project><PropertyGroup><CohesionOrganization>example</CohesionOrganization></PropertyGroup></Project>");

        DotNetBuildResult result = await workspace.PublishAsync("AppAGateway", cancellation.Token, "CohesionPublishImages");

        result.ExitCode.ShouldBe(0, result.Output);
        string output = Path.Combine(workspace.PublishOutputDirectory("AppAGateway"), "application.images.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(output));
        JsonElement images = document.RootElement.GetProperty("images");
        images.GetArrayLength().ShouldBe(2);
        images[0].GetProperty("resource").GetString().ShouldBe("appa-web");
        images[1].GetProperty("resource").GetString().ShouldBe("platform-configuration-store");
        foreach (JsonElement image in images.EnumerateArray())
        {
            image.GetProperty("platform").GetString().ShouldBe("linux/amd64");
            image.GetProperty("aot").GetBoolean().ShouldBeFalse();
            image.TryGetProperty("schema", out _).ShouldBeFalse();
            File.Exists(Path.Combine(Path.GetDirectoryName(output)!, image.GetProperty("archive").GetString()!)).ShouldBeTrue();
        }
    }

    /// <summary>Mixed providers need member entries even when InProcess is active.</summary>
    [Theory(DisplayName = "Cohesion Test [Sdk.Gateway] - Images: Should gather members in declaration order and relocate archives")]
    [InlineData("Local", false)]
    [InlineData("InProcess;Local", true)]
    public async Task Gather_MemberImages_ShouldRelocateInOrderAsync(string gateways, bool inProcess)
    {
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("AppAGateway");
        string project = workspace.ProjectFile("AppAGateway");
        string first = WriteImage(workspace.RootDirectory, "first", "z-resource", true);
        string second = WriteImage(workspace.RootDirectory, "second", "a-resource", false);
        WriteGateway(project, gateways, inProcess, first, second);

        DotNetBuildResult result = await workspace.RunTargetAsync("AppAGateway", "CohesionPublishImages");

        result.ExitCode.ShouldBe(0, result.Output);
        string output = Path.Combine(workspace.ProjectDirectory("AppAGateway"), "published", "application.images.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(output));
        document.RootElement.GetProperty("schema").GetString().ShouldBe("cohesion/images/v1");
        document.RootElement.GetProperty("application").GetString().ShouldBe("appa");
        JsonElement images = document.RootElement.GetProperty("images");
        images.GetArrayLength().ShouldBe(inProcess ? 3 : 2);
        images[0].GetProperty("resource").GetString().ShouldBe("z-resource");
        images[1].GetProperty("resource").GetString().ShouldBe("a-resource");
        foreach (JsonElement image in images.EnumerateArray())
        {
            image.TryGetProperty("schema", out _).ShouldBeFalse();
            image.GetProperty("aot").ValueKind.ShouldBe(JsonValueKind.False);
            image.GetProperty("platform").GetString().ShouldBe("linux/amd64");
        }
        images[1].TryGetProperty("archive", out _).ShouldBeFalse();
        string archive = images[0].GetProperty("archive").GetString()!;
        archive.ShouldBe("images/0/resource.tar");
        File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(output)!, archive))
            .ShouldBe(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(first)!, "images", "resource.tar")));
    }

    /// <summary>Only the InProcess-only set selects the gateway's single image.</summary>
    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - Images: Should emit one composite image for InProcess only")]
    public async Task Gather_InProcessOnly_ShouldUseCompositeAsync()
    {
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("AppAGateway");
        string own = WriteImage(workspace.RootDirectory, "own", "appa-gateway", true);
        WriteGateway(workspace.ProjectFile("AppAGateway"), "InProcess", true, own, own);

        DotNetBuildResult result = await workspace.RunTargetAsync("AppAGateway", "CohesionPublishImages");

        result.ExitCode.ShouldBe(0, result.Output);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.ProjectDirectory("AppAGateway"), "published", "application.images.json")));
        JsonElement images = document.RootElement.GetProperty("images");
        images.GetArrayLength().ShouldBe(1);
        images[0].GetProperty("resource").GetString().ShouldBe("appa-gateway");
    }

    /// <summary>A package supplies its pinned index and never acquires a project build.</summary>
    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - Images: Should read Pinned package indexes without building")]
    public async Task Gather_PinnedPackage_ShouldReadItsIndexAsync()
    {
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("AppAGateway");
        string pinned = WriteImage(workspace.RootDirectory, "package/cohesion", "pinned-worker", false);
        WriteGateway(workspace.ProjectFile("AppAGateway"), "Local", false, pinned, pinned);
        XDocument project = XDocument.Load(workspace.ProjectFile("AppAGateway"));
        project.Descendants("CohesionResourceReference").Remove();
        project.Root!.Add(new XElement("ItemGroup",
            new XElement("CohesionResourceReference", new XAttribute("Include", "Worker.Manifest")),
            new XElement("CohesionResourceManifest", new XAttribute("Include", Path.Combine(Path.GetDirectoryName(pinned)!, "resource.json")),
                new XAttribute("IsSelf", "false"), new XAttribute("ReferenceIdentity", "Worker.Manifest"))));
        project.Save(workspace.ProjectFile("AppAGateway"));

        DotNetBuildResult result = await workspace.RunTargetAsync("AppAGateway", "CohesionPublishImages");

        result.ExitCode.ShouldBe(0, result.Output);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.ProjectDirectory("AppAGateway"), "published", "application.images.json")));
        document.RootElement.GetProperty("images")[0].GetProperty("resource").GetString().ShouldBe("pinned-worker");
    }

    /// <summary>Duplicate ownership cannot produce an ambiguous ArtifactRef.Self lookup.</summary>
    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - Images: Should reject duplicate resource ownership")]
    public async Task Gather_DuplicateResource_ShouldRejectAsync()
    {
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("AppAGateway");
        string first = WriteImage(workspace.RootDirectory, "first", "duplicate", false);
        string second = WriteImage(workspace.RootDirectory, "second", "duplicate", false);
        WriteGateway(workspace.ProjectFile("AppAGateway"), "Local", false, first, second);

        DotNetBuildResult result = await workspace.RunTargetAsync("AppAGateway", "CohesionPublishImages");

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("Duplicate image resource 'duplicate'", Case.Sensitive);
    }

    private static void WriteGateway(string path, string gateways, bool active, string first, string second)
    {
        // Imported after the SDK to replace only fixture producer targets, never production behavior.
        string fixtureTargets = Path.Combine(Path.GetDirectoryName(path)!, "Images.fixture.targets");
        File.WriteAllText(path, $"""
            <Project>
              <Import Project="Sdk.props" Sdk="Assimalign.Cohesion.Sdk.Gateway" />
              <PropertyGroup>
                <CohesionApplicationName>appa</CohesionApplicationName>
                <CohesionResourceName>appa-gateway</CohesionResourceName>
                <CohesionGateways>{gateways}</CohesionGateways>
                <CohesionGatewayInProcess>{active.ToString().ToLowerInvariant()}</CohesionGatewayInProcess>
                <PublishDir>published/</PublishDir>
              </PropertyGroup>
              <ItemGroup>
                <CohesionResourceReference Include="first.csproj" />
                <CohesionResourceReference Include="second.csproj" />
              </ItemGroup>
              <Import Project="Sdk.targets" Sdk="Assimalign.Cohesion.Sdk.Gateway" />
              <Import Project="Images.fixture.targets" />
            </Project>
            """);
        string own = WriteImage(Path.GetDirectoryName(path)!, "composite", "appa-gateway", false);
        var targets = new XDocument(new XElement("Project",
            new XElement("Target", new XAttribute("Name", "CohesionResolveResourceReferences")),
            new XElement("Target", new XAttribute("Name", "CohesionPublishImage"), new XAttribute("Returns", own))));
        targets.Save(fixtureTargets);
        foreach ((string name, string image) in new[] { ("first", first), ("second", second) })
        {
            string child = Path.Combine(Path.GetDirectoryName(path)!, name + ".csproj");
            new XDocument(new XElement("Project", new XElement("Target", new XAttribute("Name", "CohesionPublishImage"),
                new XAttribute("Returns", "@(Image)"), new XElement("ItemGroup",
                    new XElement("Image", new XAttribute("Include", image), new XAttribute("ProjectFullPath", child)))))).Save(child);
        }
    }

    private static string WriteImage(string directory, string folder, string resource, bool archive)
    {
        string path = Path.Combine(directory, folder, "image.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] manifest = Encoding.UTF8.GetBytes("{\"schemaVersion\":2}");
        string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifest));
        if (archive)
        {
            string tarPath = Path.Combine(Path.GetDirectoryName(path)!, "images", "resource.tar");
            Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);
            using FileStream stream = File.Create(tarPath);
            using var writer = new TarWriter(stream);
            using var indexBytes = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"manifests\":[{{\"digest\":\"{digest}\"}}]}}"));
            using var manifestBytes = new MemoryStream(manifest);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "index.json") { DataStream = indexBytes });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "blobs/sha256/" + digest[7..]) { DataStream = manifestBytes });
        }
        string archiveField = archive ? ",\"archive\":\"images/resource.tar\"" : "";
        File.WriteAllText(path, $$"""
            {"schema":"cohesion/image/v1","resource":"{{resource}}","repository":"example/{{resource}}","registry":"ghcr.io","tag":null,"digest":"{{digest}}","platform":"linux/amd64","aot":false,"baseImage":"mcr.microsoft.com/dotnet/runtime-deps:10.0"{{archiveField}}}
            """);
        return path;
    }
}
