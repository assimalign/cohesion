using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Assimalign.Cohesion.Sdk.Tests;

internal sealed class ConsumerWorkspace : IDisposable
{
    private const string BaseSdkPackageId = "Assimalign.Cohesion.Sdk";
    private const string TestFeedEnvironmentVariable = "COHESION_SDK_TEST_FEED";
    private const string TestPackageVersionEnvironmentVariable = "COHESION_SDK_TEST_PACKAGE_VERSION";
    private static readonly string[] RequiredSdkPackageIds =
    [
        BaseSdkPackageId,
        "Assimalign.Cohesion.Sdk.Web",
        "Assimalign.Cohesion.Sdk.Database"
    ];
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string PackageVersion = ResolvePackageVersion();
    private static readonly string TestProjectsRoot = Path.Combine(
        RepositoryRoot,
        "sdks",
        BaseSdkPackageId,
        "tests",
        "TestProjects");

    private ConsumerWorkspace(string rootDirectory, string localPackageFeedDirectory)
    {
        RootDirectory = rootDirectory;
        LocalPackageFeedDirectory = localPackageFeedDirectory;
    }

    public string RootDirectory { get; }

    public string LocalPackageFeedDirectory { get; }

    internal static string SdkPackageVersion => PackageVersion;

    public static string ResourceSchemaPath => Path.Combine(
        RepositoryRoot,
        "assets",
        "schemas",
        "cohesion.resource.schema.json");

    public static ConsumerWorkspace Create(params string[] fixtureNames)
    {
        string feedDirectory = Path.Combine(RepositoryRoot, "_out", "packages");
        string sdkFeedDirectory = ResolveSdkFeedDirectory(feedDirectory);
        string[] missingSdkPackages = RequiredSdkPackageIds
            .Select(packageId => Path.Combine(sdkFeedDirectory, $"{packageId}.{PackageVersion}.nupkg"))
            .Where(packagePath => !File.Exists(packagePath))
            .ToArray();
        if (missingSdkPackages.Length > 0)
        {
            throw new InvalidOperationException(
                $"The package-boundary SDK tests require '{string.Join("', '", missingSdkPackages)}'. " +
                "Prepare the SDK package feed before running this test project.");
        }

        string workspaceId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "cohesion-sdk-integration",
            workspaceId);
        string localPackageFeedDirectory = Path.Combine(
            RepositoryRoot,
            "_out",
            "sdk-tests",
            workspaceId);
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(localPackageFeedDirectory);

        var workspace = new ConsumerWorkspace(rootDirectory, localPackageFeedDirectory);
        try
        {
            foreach (string fixtureName in fixtureNames)
            {
                workspace.CopyFixture(fixtureName);
            }

            workspace.WriteNuGetConfig(feedDirectory, sdkFeedDirectory);
            workspace.WriteGlobalJson();
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public string ProjectDirectory(string fixtureName)
    {
        return Path.Combine(RootDirectory, fixtureName);
    }

    public string ProjectFile(string fixtureName)
    {
        return Path.Combine(ProjectDirectory(fixtureName), $"{fixtureName}.csproj");
    }

    public Task<DotNetBuildResult> BuildAsync(
        string fixtureName,
        CancellationToken cancellationToken = default)
    {
        return RunDotNetAsync("build", fixtureName, [], cancellationToken);
    }

    public Task<DotNetBuildResult> RestoreAsync(
        string fixtureName,
        CancellationToken cancellationToken = default)
    {
        return RunDotNetAsync("restore", fixtureName, [], cancellationToken);
    }

    public Task<DotNetBuildResult> BuildAsync(
        string fixtureName,
        IEnumerable<string> properties,
        CancellationToken cancellationToken = default)
    {
        return RunDotNetAsync("build", fixtureName, properties, cancellationToken);
    }

    internal static Dictionary<string, string> CreateMatchingSdkPins()
    {
        using JsonDocument repositorySettings = ReadRepositoryGlobalJson();
        return repositorySettings.RootElement
            .GetProperty("msbuild-sdks")
            .EnumerateObject()
            .Where(property =>
                string.Equals(property.Name, BaseSdkPackageId, StringComparison.OrdinalIgnoreCase) ||
                property.Name.StartsWith($"{BaseSdkPackageId}.", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                property => property.Name,
                _ => PackageVersion,
                StringComparer.OrdinalIgnoreCase);
    }

    internal void WriteGlobalJson(
        string dotNetSdkVersion,
        IReadOnlyDictionary<string, string> sdkPins,
        string rollForward = "latestFeature")
    {
        var sdk = new JsonObject
        {
            ["version"] = dotNetSdkVersion,
            ["rollForward"] = rollForward
        };
        var sdkPackages = new JsonObject();
        foreach (KeyValuePair<string, string> sdkPin in sdkPins)
        {
            sdkPackages[sdkPin.Key] = sdkPin.Value;
        }

        var document = new JsonObject
        {
            ["sdk"] = sdk,
            ["msbuild-sdks"] = sdkPackages
        };

        File.WriteAllText(
            Path.Combine(RootDirectory, "global.json"),
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    internal void UseInlineBaseSdkVersion(string fixtureName)
    {
        string projectFile = ProjectFile(fixtureName);
        string project = File.ReadAllText(projectFile);
        const string unversionedSdk = "<Project Sdk=\"Assimalign.Cohesion.Sdk\">";
        string versionedSdk = $"<Project Sdk=\"Assimalign.Cohesion.Sdk/{PackageVersion}\">";
        if (!project.Contains(unversionedSdk, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Fixture '{projectFile}' does not use the unversioned base SDK declaration.");
        }

        File.WriteAllText(projectFile, project.Replace(unversionedSdk, versionedSdk, StringComparison.Ordinal));
    }

    internal void RemoveGlobalJsonAndUseInlineBaseSdkVersion(string fixtureName)
    {
        UseInlineBaseSdkVersion(fixtureName);
        File.Delete(Path.Combine(RootDirectory, "global.json"));
    }

    public Task<DotNetBuildResult> PackAsync(
        string fixtureName,
        IEnumerable<string>? properties = null,
        CancellationToken cancellationToken = default)
    {
        return RunDotNetAsync("pack", fixtureName, properties ?? [], cancellationToken);
    }

    private async Task<DotNetBuildResult> RunDotNetAsync(
        string command,
        string fixtureName,
        IEnumerable<string> properties,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = RootDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(ProjectFile(fixtureName));
        if (!string.Equals(command, "restore", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("--configuration");
            startInfo.ArgumentList.Add("Debug");
        }
        if (string.Equals(command, "pack", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(LocalPackageFeedDirectory);
        }
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity:minimal");
        foreach (string property in properties)
        {
            startInfo.ArgumentList.Add($"-p:{property}");
        }
        startInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(RootDirectory, ".dotnet");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["NUGET_PACKAGES"] = Path.Combine(RootDirectory, ".nuget", "packages");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the dotnet build process.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new DotNetBuildResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    public void Dispose()
    {
        DeleteDirectory(RootDirectory);
        DeleteDirectory(LocalPackageFeedDirectory);
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A failed cleanup must not hide the build assertion that owns this workspace.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup must not hide the build assertion that owns this workspace.
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string versionProps = Path.Combine(current.FullName, "build", "Targets", "Build.Version.props");
            if (File.Exists(versionProps))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the Cohesion repository above '{AppContext.BaseDirectory}'.");
    }

    private static string ResolvePackageVersion()
    {
        string? testPackageVersion = Environment.GetEnvironmentVariable(TestPackageVersionEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(testPackageVersion))
        {
            return testPackageVersion;
        }

        XDocument frameworkDocument = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "build",
            "Targets",
            "Build.TargetFramework.props"));
        XDocument versionDocument = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "build",
            "Targets",
            "Build.Version.props"));

        string targetFramework = RequiredElementValue(frameworkDocument, "TargetFrameworkLatest");
        string minorVersion = RequiredElementValue(versionDocument, "CohesionMinorVersion");
        string patchVersion = RequiredElementValue(versionDocument, "CohesionPatchVersion");
        Version frameworkVersion = Version.Parse(targetFramework[3..]);
        return $"{frameworkVersion.Major}.{minorVersion}.{patchVersion}";
    }

    private static string RequiredElementValue(XDocument document, string localName)
    {
        return document
            .Descendants()
            .First(element => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal))
            .Value
            .Trim();
    }

    private void CopyFixture(string fixtureName)
    {
        string sourceDirectory = Path.Combine(TestProjectsRoot, fixtureName);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"SDK test fixture '{sourceDirectory}' does not exist.");
        }

        string destinationDirectory = ProjectDirectory(fixtureName);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            string destinationFile = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile);
        }
    }

    private void WriteGlobalJson()
    {
        using JsonDocument repositorySettings = ReadRepositoryGlobalJson();
        JsonElement sdkSettings = repositorySettings.RootElement.GetProperty("sdk");
        WriteGlobalJson(
            sdkSettings.GetProperty("version").GetString()!,
            CreateMatchingSdkPins(),
            sdkSettings.GetProperty("rollForward").GetString()!);
    }

    private void WriteNuGetConfig(string feedDirectory, string sdkFeedDirectory)
    {
        var packageSources = new XElement(
            "packageSources",
            new XElement("clear"),
            new XElement(
                "add",
                new XAttribute("key", "cohesion-sdk-test"),
                new XAttribute("value", LocalPackageFeedDirectory)));
        var sourceMappings = new XElement(
            "packageSourceMapping",
            new XElement(
                "packageSource",
                new XAttribute("key", "cohesion-sdk-test"),
                new XElement("package", new XAttribute("pattern", "EnabledWeb.Manifest"))));

        if (!string.Equals(feedDirectory, sdkFeedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            packageSources.Add(
                new XElement(
                    "add",
                    new XAttribute("key", "cohesion-sdk-under-test"),
                    new XAttribute("value", sdkFeedDirectory)));
            sourceMappings.Add(
                new XElement(
                    "packageSource",
                    new XAttribute("key", "cohesion-sdk-under-test"),
                    new XElement("package", new XAttribute("pattern", "Assimalign.Cohesion.Sdk*"))));
        }

        packageSources.Add(
            new XElement(
                "add",
                new XAttribute("key", "cohesion-local"),
                new XAttribute("value", feedDirectory)),
            new XElement(
                "add",
                new XAttribute("key", "nuget.org"),
                new XAttribute("value", "https://api.nuget.org/v3/index.json"),
                new XAttribute("protocolVersion", "3")));
        sourceMappings.Add(
            new XElement(
                "packageSource",
                new XAttribute("key", "cohesion-local"),
                new XElement("package", new XAttribute("pattern", "Assimalign.Cohesion.*"))),
            new XElement(
                "packageSource",
                new XAttribute("key", "nuget.org"),
                new XElement("package", new XAttribute("pattern", "*"))));

        var document = new XDocument(
            new XElement(
                "configuration",
                packageSources,
                sourceMappings));

        document.Save(Path.Combine(RootDirectory, "nuget.config"));
    }

    private static string ResolveSdkFeedDirectory(string defaultFeedDirectory)
    {
        string? testFeed = Environment.GetEnvironmentVariable(TestFeedEnvironmentVariable);
        return string.IsNullOrWhiteSpace(testFeed)
            ? defaultFeedDirectory
            : Path.GetFullPath(testFeed);
    }

    private static JsonDocument ReadRepositoryGlobalJson()
    {
        return JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot, "global.json")),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
    }
}
