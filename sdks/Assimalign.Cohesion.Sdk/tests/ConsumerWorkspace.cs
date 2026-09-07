using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Assimalign.Cohesion.Sdk.Tests;

internal sealed class ConsumerWorkspace : IDisposable
{
    private const string BaseSdkPackageId = "Assimalign.Cohesion.Sdk";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string PackageVersion = ResolvePackageVersion();
    private static readonly string TestProjectsRoot = Path.Combine(
        RepositoryRoot,
        "sdks",
        BaseSdkPackageId,
        "tests",
        "TestProjects");

    private ConsumerWorkspace(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    public string RootDirectory { get; }

    public static string ResourceSchemaPath => Path.Combine(
        RepositoryRoot,
        "assets",
        "schemas",
        "cohesion.resource.schema.json");

    public static ConsumerWorkspace Create(params string[] fixtureNames)
    {
        string feedDirectory = Path.Combine(RepositoryRoot, "_out", "packages");
        string sdkPackage = Path.Combine(feedDirectory, $"{BaseSdkPackageId}.{PackageVersion}.nupkg");
        if (!File.Exists(sdkPackage))
        {
            throw new InvalidOperationException(
                $"The package-boundary SDK tests require '{sdkPackage}'. " +
                "Run ./installer/scripts/Install-Local.ps1 before running this test project.");
        }

        string rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "cohesion-sdk-integration",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(rootDirectory);

        var workspace = new ConsumerWorkspace(rootDirectory);
        try
        {
            foreach (string fixtureName in fixtureNames)
            {
                workspace.CopyFixture(fixtureName);
            }

            workspace.WriteNuGetConfig(feedDirectory);
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

    public async Task<DotNetBuildResult> BuildAsync(string fixtureName)
    {
        string projectFile = ProjectFile(fixtureName);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = RootDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectFile);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Debug");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity:minimal");
        startInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(RootDirectory, ".dotnet");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["NUGET_PACKAGES"] = Path.Combine(RootDirectory, ".nuget", "packages");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the dotnet build process.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().ConfigureAwait(false);
        return new DotNetBuildResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
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
        string repositoryGlobalJson = Path.Combine(RepositoryRoot, "global.json");
        using JsonDocument repositorySettings = JsonDocument.Parse(
            File.ReadAllText(repositoryGlobalJson),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        JsonElement sdkSettings = repositorySettings.RootElement.GetProperty("sdk");

        var sdk = new JsonObject
        {
            ["version"] = sdkSettings.GetProperty("version").GetString(),
            ["rollForward"] = sdkSettings.GetProperty("rollForward").GetString()
        };
        var document = new JsonObject
        {
            ["sdk"] = sdk,
            ["msbuild-sdks"] = new JsonObject
            {
                [BaseSdkPackageId] = PackageVersion
            }
        };

        File.WriteAllText(
            Path.Combine(RootDirectory, "global.json"),
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private void WriteNuGetConfig(string feedDirectory)
    {
        var document = new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "packageSources",
                    new XElement("clear"),
                    new XElement("add", new XAttribute("key", "cohesion-local"), new XAttribute("value", feedDirectory)),
                    new XElement(
                        "add",
                        new XAttribute("key", "nuget.org"),
                        new XAttribute("value", "https://api.nuget.org/v3/index.json"),
                        new XAttribute("protocolVersion", "3"))),
                new XElement(
                    "packageSourceMapping",
                    new XElement(
                        "packageSource",
                        new XAttribute("key", "cohesion-local"),
                        new XElement("package", new XAttribute("pattern", "Assimalign.Cohesion.*"))),
                    new XElement(
                        "packageSource",
                        new XAttribute("key", "nuget.org"),
                        new XElement("package", new XAttribute("pattern", "*"))))));

        document.Save(Path.Combine(RootDirectory, "nuget.config"));
    }
}
