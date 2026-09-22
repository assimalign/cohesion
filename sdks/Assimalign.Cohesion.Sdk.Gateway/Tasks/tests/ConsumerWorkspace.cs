using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Assimalign.Cohesion.Sdk.Gateway.Tests;

internal sealed class ConsumerWorkspace : IDisposable
{
    private static readonly string[] RequiredSdkPackageIds =
    [
        "Assimalign.Cohesion.Sdk",
        "Assimalign.Cohesion.Sdk.Web",
        "Assimalign.Cohesion.Sdk.ConfigurationStore",
        "Assimalign.Cohesion.Sdk.Database",
        "Assimalign.Cohesion.Sdk.Gateway",
        "Assimalign.Cohesion.Sdk.SecretStore",
        "Assimalign.Cohesion.Sdk.IdentityHub",
        "Assimalign.Cohesion.Sdk.Rezolvr",
        "Assimalign.Cohesion.Sdk.LogSpace"
    ];

    private static readonly string[] RequiredPackageIds =
    [
        .. RequiredSdkPackageIds,
        "Assimalign.Cohesion.Sdk.ApplicationModel",
        "Assimalign.Cohesion.Core",
        "Assimalign.Cohesion.ApplicationModel",
        "Assimalign.Cohesion.ApplicationModel.Gateway",
        "Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane",
        "Assimalign.Cohesion.ApplicationModel.Gateway.InProcess",
        "Assimalign.Cohesion.Connections",
        "Assimalign.Cohesion.Connections.Tcp",
        "Assimalign.Cohesion.IdentityModel",
        "Assimalign.Cohesion.IdentityModel.Token",
        "Assimalign.Cohesion.IdentityModel.Token.JsonWebToken",
        "Assimalign.Cohesion.Hosting",
        "Assimalign.Cohesion.Hosting.Health",
        "Assimalign.Cohesion.Hosting.Resources",
        "Assimalign.Cohesion.Http",
        "Assimalign.Cohesion.Http.Connections",
        "Assimalign.Cohesion.Security.DataProtection",
        "Assimalign.Cohesion.Web",
        "Assimalign.Cohesion.Web.Routing",
        "Assimalign.Cohesion.Web.ApplicationModel",
        "Assimalign.Cohesion.Database.ApplicationModel",
        "Assimalign.Cohesion.ConfigurationStore.ApplicationModel",
        "Assimalign.Cohesion.SecretStore.ApplicationModel",
        "Assimalign.Cohesion.IdentityHub.ApplicationModel",
        "Assimalign.Cohesion.Rezolvr.ApplicationModel",
        "Assimalign.Cohesion.LogSpace.ApplicationModel",
        "Assimalign.Cohesion.Database",
        "Assimalign.Cohesion.Database.Client",
        "Assimalign.Cohesion.Database.Execution",
        "Assimalign.Cohesion.Database.Governance",
        "Assimalign.Cohesion.Database.Indexing",
        "Assimalign.Cohesion.Database.Language",
        "Assimalign.Cohesion.Database.Protocol",
        "Assimalign.Cohesion.Database.Security",
        "Assimalign.Cohesion.Database.Storage",
        "Assimalign.Cohesion.Database.Transactions",
        "Assimalign.Cohesion.Database.Types",
        // Database.Storage routes its data and WAL files through IFileSystem, so both
        // FileSystem packages are real dependencies of every gateway consumer's restore.
        "Assimalign.Cohesion.FileSystem",
        "Assimalign.Cohesion.FileSystem.Physical",
        "Assimalign.Cohesion.SecretStore.Client",
        "Assimalign.Cohesion.ConfigurationStore.Client"
    ];

    private static readonly string RepositoryRoot = FindRepositoryRoot();
    public static string TargetFramework { get; } = ResolveTargetFramework();
    public static string HostRuntimeIdentifier { get; } = RuntimeInformation.RuntimeIdentifier;
    private static readonly string PackageVersion =
        Environment.GetEnvironmentVariable("COHESION_GATEWAY_TEST_PACKAGE_VERSION")
        ?? ResolvePackageVersion();
    // The SDK family layout is sdks/<family>/Tasks/{src,tests,docs}; the fixtures sit beside
    // this test project's sources, so the path carries the Tasks/ segment.
    private static readonly string TestProjectsRoot = Path.Combine(
        RepositoryRoot,
        "sdks",
        "Assimalign.Cohesion.Sdk.Gateway",
        "Tasks",
        "tests",
        "TestProjects");

    private ConsumerWorkspace(string rootDirectory, string localPackageFeedDirectory)
    {
        RootDirectory = rootDirectory;
        LocalPackageFeedDirectory = localPackageFeedDirectory;
    }

    public string RootDirectory { get; }

    public string LocalPackageFeedDirectory { get; }

    public static ConsumerWorkspace Create(params string[] fixtureNames)
    {
        string? configuredFeed = Environment.GetEnvironmentVariable(
            "COHESION_GATEWAY_TEST_PACKAGE_FEED");
        string feedDirectory = string.IsNullOrWhiteSpace(configuredFeed)
            ? Path.Combine(RepositoryRoot, "_out", "packages")
            : Path.GetFullPath(configuredFeed);
        string[] missingPackages = RequiredPackageIds
            .Select(packageId => Path.Combine(feedDirectory, $"{packageId}.{PackageVersion}.nupkg"))
            .Where(packagePath => !File.Exists(packagePath))
            .ToArray();
        if (missingPackages.Length > 0)
        {
            throw new InvalidOperationException(
                $"The package-boundary Gateway SDK tests require exact-version packages " +
                $"'{string.Join("', '", missingPackages)}'. Run " +
                "the canonical feed preparation from .github/workflows/sdk-smoke.yml before this test project.");
        }

        string workspaceId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string rootDirectory = Path.Combine(
            RepositoryRoot, "_out", "g15", workspaceId[..8]);
        string localPackageFeedDirectory = Path.Combine(rootDirectory, "packages");
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(localPackageFeedDirectory);
        File.WriteAllText(Path.Combine(rootDirectory, "Directory.Build.props"), "<Project />");

        var workspace = new ConsumerWorkspace(rootDirectory, localPackageFeedDirectory);
        try
        {
            foreach (string fixtureName in fixtureNames)
            {
                workspace.CopyFixture(fixtureName);
            }

            workspace.WriteNuGetConfig(feedDirectory);
            workspace.WriteCentralPackageReferences();
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

    public string BuildOutputDirectory(string fixtureName)
    {
        return Path.Combine(
            ProjectDirectory(fixtureName),
            "bin",
            "Debug",
            TargetFramework,
            HostRuntimeIdentifier);
    }

    public string PublishOutputDirectory(string fixtureName)
    {
        return Path.Combine(BuildOutputDirectory(fixtureName), "publish");
    }

    public string PublishOutputDirectory(string fixtureName, string runtimeIdentifier)
    {
        return Path.Combine(
            ProjectDirectory(fixtureName),
            "bin",
            "Debug",
            TargetFramework,
            runtimeIdentifier,
            "publish");
    }

    public Task<DotNetBuildResult> BuildAsync(
        string fixtureName,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "build",
            ProjectFile(fixtureName),
            "--configuration",
            "Debug",
            "--nologo",
            "--verbosity:minimal"
        };
        return RunDotNetAsync(RootDirectory, arguments, cancellationToken);
    }

    public Task<DotNetBuildResult> PackAsync(
        string fixtureName,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "pack",
            ProjectFile(fixtureName),
            "--configuration",
            "Debug",
            "--output",
            LocalPackageFeedDirectory,
            "--nologo",
            "--verbosity:minimal",
            "-p:IsPackable=true",
            $"-p:PackageVersion={PackageVersion}"
        };
        return RunDotNetAsync(RootDirectory, arguments, cancellationToken);
    }

    public Task<DotNetBuildResult> BuildInParallelAsync(
        string fixtureName,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "build",
            ProjectFile(fixtureName),
            "--configuration",
            "Debug",
            "-m",
            "--nologo",
            "--verbosity:minimal"
        };
        return RunDotNetAsync(RootDirectory, arguments, cancellationToken);
    }

    public Task<DotNetBuildResult> CleanAsync(
        string fixtureName,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "clean",
            ProjectFile(fixtureName),
            "--configuration",
            "Debug",
            "--nologo",
            "--verbosity:minimal"
        };
        return RunDotNetAsync(RootDirectory, arguments, cancellationToken);
    }

    public Task<DotNetBuildResult> RunTargetAsync(
        string fixtureName, string target, CancellationToken cancellationToken = default)
    {
        return RunDotNetAsync(RootDirectory,
            ["msbuild", ProjectFile(fixtureName), "-t:" + target, "-p:Configuration=Debug", "--nologo", "-v:minimal"],
            cancellationToken);
    }

    public Task<DotNetBuildResult> PublishAsync(
        string fixtureName,
        CancellationToken cancellationToken = default,
        string? target = null,
        IEnumerable<string>? properties = null)
    {
        var arguments = new List<string>
        {
            "publish",
            ProjectFile(fixtureName),
            "--configuration",
            "Debug",
            "--no-restore",
            "--nologo",
            "--verbosity:minimal",
            "-p:CohesionGatewayAot=false"
        };
        if (target is not null)
        {
            arguments.Remove("--no-restore");
            arguments.Add("-t:" + target);
        }
        if (properties is not null)
        {
            arguments.AddRange(properties.Select(property => "-p:" + property));
        }
        return RunDotNetAsync(RootDirectory, arguments, cancellationToken);
    }

    public Task<DotNetBuildResult> PublishSelfContainedAsync(
        string fixtureName,
        string runtimeIdentifier,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "publish",
            ProjectFile(fixtureName),
            "--configuration",
            "Debug",
            "--runtime",
            runtimeIdentifier,
            "--self-contained",
            "true",
            "-maxcpucount:1",
            "-nodeReuse:false",
            "--nologo",
            "--verbosity:minimal",
            "-p:CohesionGatewayAot=false"
        };
        return RunDotNetAsync(RootDirectory, arguments, cancellationToken);
    }

    public Task<DotNetBuildResult> RunBuiltProjectAsync(
        string fixtureName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string assemblyPath = Path.Combine(
            BuildOutputDirectory(fixtureName),
            $"{fixtureName}.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                $"Build fixture '{fixtureName}' before running it.",
                assemblyPath);
        }

        var processArguments = new List<string> { assemblyPath };
        processArguments.AddRange(arguments);
        return RunDotNetAsync(ProjectDirectory(fixtureName), processArguments, cancellationToken, environment);
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

    private static async Task<DotNetBuildResult> RunDotNetAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(workingDirectory, ".dotnet");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["NUGET_PACKAGES"] = Path.Combine(workingDirectory, ".nuget", "packages");
        if (environment is not null)
        {
            foreach ((string name, string? value) in environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the dotnet process.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            throw;
        }

        return new DotNetBuildResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
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
        XDocument versionDocument = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "build",
            "Targets",
            "Build.Version.props"));
        Version frameworkVersion = Version.Parse(TargetFramework[3..]);
        string minorVersion = RequiredElementValue(versionDocument, "CohesionMinorVersion");
        string patchVersion = RequiredElementValue(versionDocument, "CohesionPatchVersion");
        return $"{frameworkVersion.Major}.{minorVersion}.{patchVersion}";
    }

    private static string ResolveTargetFramework()
    {
        XDocument frameworkDocument = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "build",
            "Targets",
            "Build.TargetFramework.props"));
        return RequiredElementValue(frameworkDocument, "TargetFrameworkLatest");
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
            throw new DirectoryNotFoundException($"Gateway SDK test fixture '{sourceDirectory}' does not exist.");
        }

        string destinationDirectory = ProjectDirectory(fixtureName);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            if (relativePath
                .Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

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
        var sdkPackages = new JsonObject();
        foreach (string packageId in RequiredSdkPackageIds)
        {
            sdkPackages[packageId] = PackageVersion;
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

    private void WriteCentralPackageReferences()
    {
        File.Copy(
            Path.Combine(RepositoryRoot, "build", "Targets", "Build.References.Packages.targets"),
            Path.Combine(RootDirectory, "Directory.Build.targets"));
    }

    private void WriteNuGetConfig(string feedDirectory)
    {
        var document = new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "packageSources",
                    new XElement("clear"),
                    new XElement("add", new XAttribute("key", "gateway-test"), new XAttribute("value", LocalPackageFeedDirectory)),
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
                        new XAttribute("key", "gateway-test"),
                        new XElement("package", new XAttribute("pattern", "ThirdParty.*"))),
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
