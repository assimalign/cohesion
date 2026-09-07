using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Tests;

public sealed class ResourceManifestPackIntegrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk] - pack: disabled application model keeps normal packing")]
    public async Task Pack_DisabledApplicationModel_KeepsNormalPacking()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("DisabledResource");

        // Act
        DotNetBuildResult result = await workspace.PackAsync(
            "DisabledResource",
            cancellationToken: cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldNotContain("COHSDK004");
        string packagePath = SinglePackage(workspace, "DisabledResource");
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        archive.Entries.Any(entry => string.Equals(
            entry.FullName.Replace('\\', '/'),
            "lib/net10.0/DisabledResource.dll",
            StringComparison.Ordinal)).ShouldBeTrue();
        archive.Entries.Any(entry => entry.FullName.StartsWith("cohesion/", StringComparison.Ordinal)).ShouldBeFalse();
        archive.Entries.Any(entry => entry.FullName.StartsWith("buildTransitive/", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - pack: enabled resource produces a portable manifest package consumed through buildTransitive")]
    public async Task Pack_EnabledResource_ProducesPortableManifestPackageAndRegistersConsumerItem()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "EnabledDependency",
            "EnabledDatabase",
            "EnabledWeb",
            "ManifestConsumer");
        string imageArchivePath = Path.Combine(workspace.ProjectDirectory("EnabledWeb"), "inventory-web.oci.tar");
        File.WriteAllBytes(imageArchivePath, [0x43, 0x4f, 0x48]);

        // Act
        DotNetBuildResult packResult = await workspace.PackAsync(
            "EnabledWeb",
            [$"CohesionContainerArchiveOutputPath={imageArchivePath}"],
            cancellationToken: cancellationSource.Token);

        // Assert
        packResult.ExitCode.ShouldBe(0, packResult.Output);
        packResult.Output.ShouldContain("COHSDK004");
        packResult.Output.ShouldContain("the package will be manifest-only");

        string packagePath = SinglePackage(workspace, "EnabledWeb.Manifest");
        using (ZipArchive archive = ZipFile.OpenRead(packagePath))
        {
            string[] entries = archive.Entries
                .Select(entry => entry.FullName.Replace('\\', '/'))
                .OrderBy(entry => entry, StringComparer.Ordinal)
                .ToArray();
            string[] payloadEntries = entries
                .Where(entry => !IsNuGetInfrastructure(entry, "EnabledWeb.Manifest"))
                .ToArray();
            string[] expectedPayloadEntries =
            [
                "README.md",
                "buildTransitive/EnabledWeb.Manifest.props",
                "cohesion/resource.json"
            ];

            payloadEntries.SequenceEqual(expectedPayloadEntries, StringComparer.Ordinal)
                .ShouldBeTrue($"Unexpected package entries: {string.Join(", ", entries)}");
            entries.Any(entry => entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
            entries.Any(entry => entry.StartsWith("images/", StringComparison.Ordinal)).ShouldBeFalse();

            using JsonDocument manifest = JsonDocument.Parse(ReadEntry(archive, "cohesion/resource.json"));
            JsonElement artifact = manifest.RootElement.GetProperty("artifact");
            artifact.GetProperty("project").ValueKind.ShouldBe(JsonValueKind.Null);
            artifact.GetProperty("apphost").ValueKind.ShouldBe(JsonValueKind.Null);

            XDocument nuspec = XDocument.Parse(ReadEntry(archive, "EnabledWeb.Manifest.nuspec"));
            XNamespace packageNamespace = nuspec.Root!.Name.Namespace;
            XElement metadata = nuspec.Root.Element(packageNamespace + "metadata")!;
            metadata.Element(packageNamespace + "id")!.Value.ShouldBe("EnabledWeb.Manifest");
            metadata.Element(packageNamespace + "readme")!.Value.ShouldBe("README.md");
            metadata
                .Descendants(packageNamespace + "packageType")
                .Single()
                .Attribute("name")!
                .Value
                .ShouldBe("CohesionResourceManifest");
            metadata.Descendants(packageNamespace + "dependency").ShouldBeEmpty();
            metadata.Descendants(packageNamespace + "frameworkReference").ShouldBeEmpty();
        }

        string sourceManifestPath = Path.Combine(
            workspace.ProjectDirectory("EnabledWeb"),
            "obj",
            "Debug",
            "net10.0",
            "cohesion",
            "resource.json");
        using (JsonDocument sourceManifest = JsonDocument.Parse(File.ReadAllText(sourceManifestPath)))
        {
            JsonElement artifact = sourceManifest.RootElement.GetProperty("artifact");
            artifact.GetProperty("project").GetString().ShouldNotBeNullOrWhiteSpace();
            artifact.GetProperty("apphost").GetString().ShouldNotBeNullOrWhiteSpace();
        }

        DotNetBuildResult consumerResult = await workspace.BuildAsync(
            "ManifestConsumer",
            cancellationSource.Token);
        consumerResult.ExitCode.ShouldBe(0, consumerResult.Output);

        string capturedItemsPath = Path.Combine(
            workspace.ProjectDirectory("ManifestConsumer"),
            "obj",
            "cohesion-resource-manifests.txt");
        string[] capturedItems = File.ReadAllLines(capturedItemsPath);
        string packageItem = capturedItems.Single(line => line.Contains("|EnabledWeb.Manifest|", StringComparison.Ordinal));
        string[] packageItemParts = packageItem.Split('|');
        packageItemParts.Length.ShouldBe(5);
        string manifestPath = packageItemParts[0];
        packageItemParts[1].ShouldBe(manifestPath);
        packageItemParts[2].ShouldBe("EnabledWeb.Manifest");
        packageItemParts[3].ShouldBe("EnabledWeb.Manifest");
        packageItemParts[4].ShouldBe("false");
        File.Exists(manifestPath).ShouldBeTrue($"The buildTransitive manifest item did not resolve to '{manifestPath}'.");
        Path.GetFullPath(manifestPath).ShouldStartWith(
            Path.GetFullPath(Path.Combine(workspace.RootDirectory, ".nuget", "packages")),
            Case.Insensitive);

        string consumerManifestPath = Path.Combine(
            workspace.ProjectDirectory("ManifestConsumer"),
            "obj",
            "Debug",
            "net10.0",
            "cohesion",
            "resource.json");
        using JsonDocument consumerManifest = JsonDocument.Parse(File.ReadAllText(consumerManifestPath));
        consumerManifest.RootElement
            .GetProperty("references")[0]
            .GetProperty("manifest")
            .GetString()
            .ShouldBe("EnabledWeb.Manifest@1.0.0");
        Directory
            .EnumerateFiles(
                Path.Combine(workspace.ProjectDirectory("ManifestConsumer"), "bin"),
                "EnabledWeb.dll",
                SearchOption.AllDirectories)
            .ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - pack: CohesionImageRequired promotes COHSDK004 to an error")]
    public async Task Pack_ImageRequiredWithoutDigest_FailsWithCOHSDK004()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "EnabledDependency",
            "EnabledDatabase",
            "EnabledWeb");
        string imageManifestPath = Path.Combine(workspace.ProjectDirectory("EnabledWeb"), "image.json");
        File.WriteAllText(imageManifestPath, "{\"schema\":\"cohesion/image/v1\",\"tag\":\"latest\"}");

        // Act
        DotNetBuildResult result = await workspace.PackAsync(
            "EnabledWeb",
            [
                $"CohesionImageManifestPath={imageManifestPath}",
                "CohesionImageRequired=true"
            ],
            cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("COHSDK004");
        result.Output.ShouldContain("CohesionImageRequired=true");
        Directory.EnumerateFiles(workspace.LocalPackageFeedDirectory, "*.nupkg").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - pack: CohesionPackRuntime adds a library-style package")]
    public async Task Pack_RuntimeOptIn_ProducesManifestAndLibraryPackages()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "EnabledDependency",
            "EnabledDatabase",
            "EnabledWeb");

        // Act
        DotNetBuildResult result = await workspace.PackAsync(
            "EnabledWeb",
            ["CohesionPackRuntime=true"],
            cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldContain("COHSDK004");
        string[] packages = Directory.EnumerateFiles(workspace.LocalPackageFeedDirectory, "*.nupkg").ToArray();
        packages.Length.ShouldBe(2, $"Expected manifest and runtime packages, found: {string.Join(", ", packages)}");

        string manifestPackagePath = SinglePackage(workspace, "EnabledWeb.Manifest");
        using (ZipArchive manifestArchive = ZipFile.OpenRead(manifestPackagePath))
        {
            manifestArchive.Entries.Any(entry => string.Equals(
                entry.FullName.Replace('\\', '/'),
                "cohesion/resource.json",
                StringComparison.Ordinal)).ShouldBeTrue();
            manifestArchive.Entries.Any(entry => entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
        }

        string runtimePackagePath = SinglePackage(workspace, "EnabledWeb");
        using ZipArchive runtimeArchive = ZipFile.OpenRead(runtimePackagePath);
        runtimeArchive.Entries.Any(entry => string.Equals(
            entry.FullName.Replace('\\', '/'),
            "lib/net10.0/EnabledWeb.dll",
            StringComparison.Ordinal)).ShouldBeTrue();
        runtimeArchive.Entries.Any(entry => entry.FullName.StartsWith("buildTransitive/", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - pack: image manifest and opted-in OCI archive use their staged package slots")]
    public async Task Pack_ImageAndArchivePresent_IncludesStagedAssets()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "EnabledDependency",
            "EnabledDatabase",
            "EnabledWeb");
        string imageManifestPath = Path.Combine(workspace.ProjectDirectory("EnabledWeb"), "image.json");
        string imageArchivePath = Path.Combine(workspace.ProjectDirectory("EnabledWeb"), "inventory-web.oci.tar");
        byte[] expectedArchive = [0x43, 0x4f, 0x48];
        File.WriteAllText(
            imageManifestPath,
            "{\"schema\":\"cohesion/image/v1\",\"digest\":\"sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"}");
        File.WriteAllBytes(imageArchivePath, expectedArchive);

        // Act
        DotNetBuildResult result = await workspace.PackAsync(
            "EnabledWeb",
            [
                $"CohesionImageManifestPath={imageManifestPath}",
                $"CohesionContainerArchiveOutputPath={imageArchivePath}",
                "CohesionPackImageArchive=true",
                "CohesionImageRequired=true"
            ],
            cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldNotContain("COHSDK004");
        string packagePath = SinglePackage(workspace, "EnabledWeb.Manifest");
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        string[] entries = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
        entries.ShouldContain("cohesion/image.json");
        entries.ShouldContain("images/inventory-web.oci.tar");
        ReadEntryBytes(archive, "images/inventory-web.oci.tar").ShouldBe(expectedArchive);
    }

    private static string SinglePackage(ConsumerWorkspace workspace, string packageId)
    {
        string[] packages = Directory
            .EnumerateFiles(workspace.LocalPackageFeedDirectory, "*.nupkg")
            .Where(package => string.Equals(ReadPackageId(package), packageId, StringComparison.Ordinal))
            .ToArray();
        packages.Length.ShouldBe(1, $"Expected one {packageId} package, found: {string.Join(", ", packages)}");
        return packages[0];
    }

    private static string ReadPackageId(string packagePath)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry nuspec = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        XDocument document = XDocument.Parse(ReadEntry(archive, nuspec.FullName));
        XNamespace packageNamespace = document.Root!.Name.Namespace;
        return document.Root.Element(packageNamespace + "metadata")!.Element(packageNamespace + "id")!.Value;
    }

    private static bool IsNuGetInfrastructure(string entry, string packageId)
    {
        return string.Equals(entry, "[Content_Types].xml", StringComparison.Ordinal)
            || string.Equals(entry, "_rels/.rels", StringComparison.Ordinal)
            || string.Equals(entry, $"{packageId}.nuspec", StringComparison.Ordinal)
            || entry.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal);
    }

    private static string ReadEntry(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path)
            ?? throw new InvalidDataException($"Package entry '{path}' was not found.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static byte[] ReadEntryBytes(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path)
            ?? throw new InvalidDataException($"Package entry '{path}' was not found.");
        using Stream source = entry.Open();
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }
}
