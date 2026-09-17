using System;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Tests;

/// <summary>Exercises the packaged SDK's image producer contract.</summary>
public sealed class ImagePublishTests
{
    /// <summary>A daemon-free archive contains a Linux payload and remains current until its sources change.</summary>
    [Fact(DisplayName = "Cohesion Test [Sdk] - Image publish: Should create a Linux OCI archive and rebuild only on changed inputs")]
    public async Task Publish_DebugArchive_ShouldVerifyPayloadAndFreshnessAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using ConsumerWorkspace workspace = CreateWeb();
        string[] properties = ["CohesionOrganization=example", "CohesionContainerRegistry=ghcr.io/example", "Version=1.4.0"];

        DotNetBuildResult first = await workspace.PublishAsync("EnabledWeb", properties, cancellationToken: cancellation.Token);

        first.ExitCode.ShouldBe(0, first.Output);
        string imagePath = FindImage(workspace);
        using JsonDocument image = JsonDocument.Parse(File.ReadAllText(imagePath));
        JsonElement root = image.RootElement;
        root.EnumerateObject().Select(property => property.Name).Order().ShouldBe(new[]
        { "schema", "resource", "repository", "registry", "tag", "digest", "platform", "aot", "baseImage", "archive" }.Order());
        root.GetProperty("schema").GetString().ShouldBe("cohesion/image/v1");
        root.GetProperty("resource").GetString().ShouldBe("inventory-web");
        root.GetProperty("repository").GetString().ShouldBe("example/inventory-web");
        root.GetProperty("registry").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("tag").GetString().ShouldBe("1.4.0");
        root.GetProperty("platform").GetString().ShouldBe("linux/amd64");
        root.GetProperty("aot").GetBoolean().ShouldBeFalse();
        root.GetProperty("baseImage").GetString().ShouldBe("mcr.microsoft.com/dotnet/runtime-deps:10.0");
        root.GetProperty("archive").GetString().ShouldBe("images/inventory-web.tar");
        string archive = Path.Combine(Path.GetDirectoryName(imagePath)!, "images", "inventory-web.tar");
        File.Exists(archive).ShouldBeTrue();
        using (FileStream tar = File.OpenRead(archive))
        using (var reader = new TarReader(tar))
        {
            TarEntry? entry;
            bool found = false;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                if (entry.Name.TrimStart('.', '/') == "index.json")
                {
                    using JsonDocument index = JsonDocument.Parse(entry.DataStream!);
                    index.RootElement.GetProperty("manifests")[0].GetProperty("digest").GetString().ShouldBe(root.GetProperty("digest").GetString());
                    found = true;
                }
            }
            found.ShouldBeTrue();
        }
        string payload = Path.Combine(workspace.ProjectDirectory("EnabledWeb"), "bin", "Debug", "net10.0", "linux-x64", "publish");
        byte[] apphost = File.ReadAllBytes(Path.Combine(payload, "EnabledWeb"));
        apphost[..4].ShouldBe(new byte[] { 0x7f, 0x45, 0x4c, 0x46 });
        apphost[18].ShouldBe((byte)0x3e); // ELF e_machine = x86-64.
        using JsonDocument deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(payload, "EnabledWeb.deps.json")));
        deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!.ShouldEndWith("/linux-x64");
        DateTime timestamp = File.GetLastWriteTimeUtc(archive);
        DotNetBuildResult second = await workspace.PublishAsync("EnabledWeb", properties, cancellationToken: cancellation.Token);
        second.ExitCode.ShouldBe(0, second.Output);
        second.Output.ShouldContain("skipping CreateNewImage", Case.Sensitive);
        File.GetLastWriteTimeUtc(archive).ShouldBe(timestamp);

        string program = Path.Combine(workspace.ProjectDirectory("EnabledWeb"), "Program.cs");
        File.AppendAllText(program, "\ninternal static class ImageRevision { internal const string Value = \"changed\"; }\n");
        DotNetBuildResult third = await workspace.PublishAsync("EnabledWeb", properties, cancellationToken: cancellation.Token);
        third.ExitCode.ShouldBe(0, third.Output);
        third.Output.ShouldNotContain("skipping CreateNewImage", Case.Sensitive);
        File.GetLastWriteTimeUtc(archive).ShouldNotBe(timestamp);
    }

    /// <summary>Invalid user properties never acquire an unrelated diagnostic code.</summary>
    [Theory(DisplayName = "Cohesion Test [Sdk] - Image publish: Should reject invalid inputs before image creation")]
    [InlineData("CohesionContainerRepository=team/API", "lowercase OCI repository")]
    [InlineData("CohesionContainerRepository=team/-api", "lowercase OCI repository")]
    [InlineData("CohesionContainerRepository=team/api.", "lowercase OCI repository")]
    [InlineData("CohesionContainerRepository=team/api..worker", "lowercase OCI repository")]
    [InlineData("CohesionContainerRepository=team/api._worker", "lowercase OCI repository")]
    [InlineData("CohesionImageAot=maybe", "auto, true, or false")]
    [InlineData("CohesionImageFreshness=force", "Rebuild or Pinned")]
    [InlineData("CohesionImageFreshness=Pinned", "manifest packages")]
    [InlineData("RuntimeIdentifier=win-x64", "support linux-x64")]
    public async Task Publish_InvalidInput_ShouldRejectAsync(string property, string expected)
    {
        using ConsumerWorkspace workspace = CreateWeb();
        DotNetBuildResult result = await workspace.PublishAsync("EnabledWeb", ["CohesionOrganization=example", property]);
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain(expected, Case.Sensitive);
        result.Output.ShouldNotContain("COHSDK003", Case.Sensitive);
        result.Output.ShouldNotContain("COHSDK005", Case.Sensitive);
    }

    /// <summary>An external archive cannot be silently packed without a usable index reference.</summary>
    [Fact(DisplayName = "Cohesion Test [Sdk] - Image publish: Should reject an uncontained archive with pack opt-in")]
    public async Task Publish_UncontainedArchive_ShouldRejectAsync()
    {
        using ConsumerWorkspace workspace = CreateWeb();
        string archive = Path.Combine(workspace.RootDirectory, "outside.tar");
        File.WriteAllBytes(archive, [1, 2, 3]);
        DotNetBuildResult result = await workspace.PublishAsync("EnabledWeb",
            ["CohesionOrganization=example", "CohesionPackImageArchive=true", "CohesionContainerArchiveOutputPath=" + archive]);
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("must be contained", Case.Sensitive);
        Directory.GetFiles(workspace.RootDirectory, "image.json", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    /// <summary>Framework-dependent images have one dedicated diagnostic.</summary>
    [Fact(DisplayName = "Cohesion Test [Sdk] - Image publish: Should reject framework-dependent payloads with COHSDK005")]
    public async Task Publish_FrameworkDependent_ShouldRejectAsync()
    {
        using ConsumerWorkspace workspace = CreateWeb();
        DotNetBuildResult result = await workspace.PublishAsync("EnabledWeb", ["CohesionOrganization=example", "SelfContained=false"]);
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("COHSDK005", Case.Sensitive);
    }

    /// <summary>Release false is visible as a design deviation rather than a JIT escape hatch.</summary>
    [Fact(DisplayName = "Cohesion Test [Sdk] - Image publish: Should reject explicit Release JIT")]
    public async Task Publish_ReleaseFalse_ShouldRejectDeviationAsync()
    {
        using ConsumerWorkspace workspace = CreateWeb();
        DotNetBuildResult result = await workspace.PublishAsync("EnabledWeb", ["CohesionOrganization=example", "CohesionImageAot=false"], "Release");
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("deviates from T12/O11", Case.Sensitive);
    }

    /// <summary>The native host route is not silently replaced by a Release JIT payload.</summary>
    [NonLinuxImageFact(DisplayName = "Cohesion Test [Sdk] - Image publish: Should diagnose unavailable Release NativeAOT with COHSDK003")]
    public async Task Publish_ReleaseNonLinux_ShouldRejectUnavailableRouteAsync()
    {
        using ConsumerWorkspace workspace = CreateWeb();
        DotNetBuildResult result = await workspace.PublishAsync("EnabledWeb", ["CohesionOrganization=example"], "Release");
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("COHSDK003", Case.Sensitive);
        result.Output.ShouldContain("NativeAOT", Case.Sensitive);
    }

    /// <summary>Only the SDK's single-container path can produce a digest, for exactly one sink.</summary>
    [Fact(DisplayName = "Cohesion Test [Sdk] - Image targets: Should select one CreateNewImage route and preserve raw SDK publishing")]
    public void Targets_SinkSelection_ShouldRemainExclusive()
    {
        string root = FindRepository();
        XDocument targets = XDocument.Load(Path.Combine(root, "sdks", "Assimalign.Cohesion.Sdk", "Targets", "Sdk.Image.targets"));
        targets.Descendants("CreateNewImage").ShouldBeEmpty();
        targets.Descendants("CallTarget").Where(element => (string?)element.Attribute("Targets") == "PublishContainer").Count().ShouldBe(1);
        string properties = targets.Descendants("_CohesionImageProperties").Single().Value;
        properties.ShouldContain("ContainerImageFormat=OCI", Case.Sensitive);
        properties.ShouldContain("RuntimeIdentifiers=;ContainerRuntimeIdentifiers=;", Case.Sensitive);
        properties.ShouldContain("ContainerRegistry=$(_CohesionImageRegistry);ContainerArchiveOutputPath=$(_CohesionImageArchiveSink)", Case.Sensitive);
        XElement guard = targets.Descendants("Target").Single(element => (string?)element.Attribute("Name") == "CohesionValidateContainerRuntime");
        guard.ToString().ShouldNotContain("CohesionOrganization", Case.Sensitive);
        guard.ToString().ShouldNotContain("CohesionContainerRepository", Case.Sensitive);
    }

    private static ConsumerWorkspace CreateWeb() => ConsumerWorkspace.Create("EnabledDependency", "EnabledDatabase", "EnabledWeb");

    /// <summary>The raw sdk-smoke flag vector remains independent of Cohesion repository defaults.</summary>
    [Fact(DisplayName = "Cohesion Test [Sdk] - Container guard: Should preserve raw Linux CI SDK options without an organization")]
    public async Task Guard_RawSdkFlags_ShouldNotRequireOrganizationAsync()
    {
        using ConsumerWorkspace workspace = CreateWeb();
        XDocument project = XDocument.Load(workspace.ProjectFile("EnabledWeb"));
        project.Root!.Add(new XElement("Target", new XAttribute("Name", "RawContainerProbe"),
            new XAttribute("DependsOnTargets", "CohesionValidateContainerRuntime"),
            new XElement("Message", new XAttribute("Importance", "High"),
                new XAttribute("Text", "Raw=$(ContainerRepository)|$(ContainerImageFormat)|$(RuntimeIdentifier)|$(SelfContained)|$(CohesionOrganization)|$(CohesionContainerRepository)"))));
        project.Save(workspace.ProjectFile("EnabledWeb"));

        DotNetBuildResult result = await workspace.PublishAsync("EnabledWeb",
            ["RuntimeIdentifier=linux-x64", "SelfContained=true", "EnableSdkContainerSupport=true", "CohesionGatewayAot=true", "PublishAot=true",
                "ContainerImageFormat=OCI", "ContainerArchiveOutputPath=" + Path.Combine(workspace.RootDirectory, "gateway-smoke-linux-x64.oci.tar.gz"),
                "ContainerRepository=gateway-smoke", "ContainerImageTag=ci"], "Release", "RawContainerProbe");

        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldContain("Raw=gateway-smoke|OCI|linux-x64|true||", Case.Sensitive);
    }

    /// <summary>Registry production omits archive entirely and verifies the returned digest.</summary>
    [Fact(DisplayName = "Cohesion Test [Sdk] - Image index: Should omit archive for a registry sink and validate returned digests")]
    public async Task Write_RegistrySink_ShouldOmitArchiveAsync()
    {
        using ConsumerWorkspace workspace = CreateWeb();
        const string digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string output = Path.Combine(workspace.RootDirectory, "registry-image.json");
        XDocument project = XDocument.Load(workspace.ProjectFile("EnabledWeb"));
        project.Root!.Add(new XElement("Target", new XAttribute("Name", "WriteImageProbe"),
            new XElement("CohesionVerifyImageDigest", new XAttribute("Digest", digest), new XAttribute("RegistryDigest", digest)),
            new XElement("CohesionWriteImageIndex", new XAttribute("OutputPath", output), new XAttribute("Resource", "worker"),
                new XAttribute("Repository", "example/worker"), new XAttribute("Registry", "ghcr.io"), new XAttribute("Tag", "1.4.0"),
                new XAttribute("Digest", digest), new XAttribute("Aot", "true"), new XAttribute("BaseImage", "mcr.microsoft.com/dotnet/runtime-deps:10.0"))));
        project.Save(workspace.ProjectFile("EnabledWeb"));

        DotNetBuildResult result = await workspace.PublishAsync("EnabledWeb", target: "WriteImageProbe");

        result.ExitCode.ShouldBe(0, result.Output);
        using JsonDocument image = JsonDocument.Parse(File.ReadAllText(output));
        image.RootElement.TryGetProperty("archive", out _).ShouldBeFalse();
        image.RootElement.GetProperty("registry").GetString().ShouldBe("ghcr.io");
        image.RootElement.GetProperty("aot").GetBoolean().ShouldBeTrue();
        image.RootElement.GetProperty("digest").GetString().ShouldBe(digest);
        project.Descendants("CohesionVerifyImageDigest").Single().SetAttributeValue("RegistryDigest", "sha256:" + new string('a', 64));
        project.Save(workspace.ProjectFile("EnabledWeb"));
        DotNetBuildResult mismatch = await workspace.PublishAsync("EnabledWeb", target: "WriteImageProbe");
        mismatch.ExitCode.ShouldNotBe(0);
        mismatch.Output.ShouldContain("differs from the registry", Case.Sensitive);
    }

    /// <summary>Missing and malformed digests cannot be accepted as immutable identities.</summary>
    [Theory(DisplayName = "Cohesion Test [Sdk] - Image digest: Should reject absent or malformed producer digests")]
    [InlineData("")]
    [InlineData("sha256:1234")]
    [InlineData("sha512:0123456789abcdef")]
    public async Task Verify_InvalidDigest_ShouldRejectAsync(string digest)
    {
        using ConsumerWorkspace workspace = CreateWeb();
        XDocument project = XDocument.Load(workspace.ProjectFile("EnabledWeb"));
        project.Root!.Add(new XElement("Target", new XAttribute("Name", "VerifyImageProbe"),
            new XElement("CohesionVerifyImageDigest", new XAttribute("Digest", digest), new XAttribute("RegistryDigest", digest))));
        project.Save(workspace.ProjectFile("EnabledWeb"));

        DotNetBuildResult result = await workspace.PublishAsync("EnabledWeb", target: "VerifyImageProbe");

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("Digest");
    }
    private static string FindImage(ConsumerWorkspace workspace) => Directory.GetFiles(Path.Combine(workspace.ProjectDirectory("EnabledWeb"), "obj"), "image.json", SearchOption.AllDirectories).Single();
    private static string FindRepository()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "docs", "DEVELOPER_EXPERIENCE_DESIGN.md")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException();
    }
}
