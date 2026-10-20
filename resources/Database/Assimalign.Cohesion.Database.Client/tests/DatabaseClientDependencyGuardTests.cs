using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Client.Tests;

public sealed class DatabaseClientDependencyGuardTests
{
    public static IEnumerable<object[]> ForbiddenProjects()
    {
        foreach (string model in new[] { "Sql", "Graph", "Documents", "KeyValuePair", "Blob", "Cache" })
        {
            yield return ["Assimalign.Cohesion.Database." + model];
            foreach (string family in new[] { "Catalog", "Storage", "Security", "Tcp" })
            {
                yield return [$"Assimalign.Cohesion.Database.{model}.{family}"];
                yield return [$"Assimalign.Cohesion.Database.{model}.{family}.Implementation"];
            }
        }
        yield return ["Assimalign.Cohesion.Database"];
        foreach (string family in new[] { "Storage", "Transactions", "Indexing", "Memory", "Embedded", "Hosting" })
        {
            yield return ["Assimalign.Cohesion.Database." + family];
            yield return [$"Assimalign.Cohesion.Database.{family}.Implementation"];
        }
    }

    public static IEnumerable<object[]> AllowedProjects()
    {
        foreach (string contract in new[] { "Types", "Execution", "Language", "Protocol", "Security",
            "Sql.Protocol", "Graph.Protocol", "Documents.Protocol", "KeyValuePair.Protocol", "Blob.Protocol",
            "Sql.Language", "Graph.Language", "Documents.Language", "Sql.Schema",
            "Sqlite", "Sql.Catalogue", "Sql.StorageX", "StorageX", "HostingX" })
        {
            yield return ["Assimalign.Cohesion.Database." + contract];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Guard rejects every model, retired satellite and shared engine project")]
    [MemberData(nameof(ForbiddenProjects))]
    public async Task ProjectReferences_EngineIdentity_ShouldReject(string identity)
    {
        // Arrange
        using var probe = new GuardProbe();
        string items = $"<ProjectReference Include=\"{identity}.csproj\" />";
        // Act
        var result = await probe.ThinAsync(items);
        // Assert
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("COHDBCLIENT001", Case.Sensitive);
        result.Output.ShouldContain(identity, Case.Sensitive);
        result.Output.ShouldContain(probe.DirectoryPath, Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Guard permits contracts, parsers and exact suffix boundaries")]
    [MemberData(nameof(AllowedProjects))]
    public async Task ProjectReferences_ContractOrNonmatchingBoundary_ShouldAllow(string identity)
    {
        // Arrange
        using var probe = new GuardProbe();
        // Act
        var result = await probe.ThinAsync($"<ProjectReference Include=\"{identity}.csproj\" />");
        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldNotContain("COHDBCLIENT001", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Guard covers real name-only and private reference conversion")]
    [InlineData("CohesionProjectReference", "Sql")]
    [InlineData("CohesionProjectReference", "Graph")]
    [InlineData("CohesionPrivateProjectReference", "Sql")]
    [InlineData("CohesionPrivateProjectReference", "Graph")]
    public async Task ProjectReferences_NameOnlyOrPrivate_ShouldReject(string item, string model)
    {
        // Arrange
        using var probe = new GuardProbe();
        // Act
        var result = await probe.ThinAsync($"<{item} Include=\"Assimalign.Cohesion.Database.{model}\" />");
        // Assert
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("COHDBCLIENT001", Case.Sensitive);
        result.Output.ShouldContain($"Assimalign.Cohesion.Database.{model}", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Guard includes build-order-only references and the CoreCompile entry")]
    public async Task ProjectReferences_BuildOrderFalse_ShouldRejectBeforeCompile()
    {
        // Arrange
        using var probe = new GuardProbe();
        // Act
        var result = await probe.ThinAsync("<ProjectReference Include=\"Assimalign.Cohesion.Database.Graph.csproj\" ReferenceOutputAssembly=\"false\" />", target: "CoreCompile");
        // Assert
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("COHDBCLIENT001", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Either project or late assembly identity activates the guard, ignoring case")]
    [InlineData("Assimalign.Cohesion.Database.Sql.Client", "Renamed.Output")]
    [InlineData("Renamed.Project", "Assimalign.Cohesion.Database.Graph.Client")]
    [InlineData("Assimalign.Cohesion.Database.Client", "Renamed.Output")]
    [InlineData("Renamed.Project", "Assimalign.Cohesion.Database.Client")]
    [InlineData("assimalign.cohesion.database.sql.client", "renamed.output")]
    public async Task Scope_ProjectOrAssemblyIdentity_ShouldReject(string projectName, string assemblyName)
    {
        // Arrange
        using var probe = new GuardProbe();
        // Act
        var result = await probe.ThinAsync("<ProjectReference Include=\"ASSIMALIGN.COHESION.DATABASE.GRAPH.STORAGE.Impl.csproj\" />", projectName, assemblyName);
        // Assert
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("COHDBCLIENT001", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Embedded, Studio and client-name suffixes are outside scope")]
    [InlineData("Assimalign.Cohesion.Database.Embedded")]
    [InlineData("Assimalign.Cohesion.Database.Studio")]
    [InlineData("Assimalign.Cohesion.Database.Sql.Client.Tests")]
    [InlineData("Assimalign.Cohesion.Database.Blob.Client.StreamingFixture")]
    [InlineData("Assimalign.Cohesion.Database.Sql.Client.Mapping")]
    public async Task Scope_NonClientIdentity_ShouldAllow(string identity)
    {
        // Arrange
        using var probe = new GuardProbe();
        // Act
        var result = await probe.ThinAsync("<ProjectReference Include=\"Assimalign.Cohesion.Database.Sql.csproj\" />", identity, identity);
        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Only complete repository-relative harness segments exempt clients")]
    [InlineData("TeStS")]
    [InlineData("eXaMpLeS")]
    [InlineData("SaMpLeS")]
    [InlineData("FiXtUrEs")]
    public async Task Scope_HarnessSegment_ShouldAllow(string segment)
    {
        // Arrange
        using var probe = new GuardProbe();
        // Act
        var result = await probe.ThinAsync("<ProjectReference Include=\"Assimalign.Cohesion.Database.Sql.csproj\" />", subdirectory: $"shipping/{segment}/component");
        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Checkout ancestors and harness-like substrings cannot exempt shipped clients")]
    [InlineData("tests/checkout", "shipping")]
    [InlineData("checkout", "mytests/component")]
    [InlineData("checkout", "samples-extra/component")]
    public async Task Scope_AncestorOrSubstring_ShouldReject(string checkout, string projectDirectory)
    {
        // Arrange
        using var probe = new GuardProbe();
        string repository = Path.Combine(probe.DirectoryPath, checkout);
        // Act
        var result = await probe.ThinAsync("<ProjectReference Include=\"Assimalign.Cohesion.Database.Sql.csproj\" />",
            subdirectory: checkout + "/" + projectDirectory, repository: repository);
        // Assert
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("COHDBCLIENT001", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Resolved identity catches engine DLLs even under safe filenames and framework metadata")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvedReferences_RenamedAsset_ShouldRejectActualIdentity(bool framework)
    {
        // Arrange
        using var probe = new GuardProbe();
        string renamed = probe.CopyEngine("Completely.Safe.dll");
        string route = framework ? "<ReferenceSourceTarget>ResolveFrameworkReferences</ReferenceSourceTarget>" : "";
        // Act
        var result = await probe.ThinAsync("", referencePaths: $"<ReferencePath Include=\"{GuardProbe.Escape(renamed)}\">{route}</ReferencePath>");
        // Assert
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("COHDBCLIENT001", Case.Sensitive);
        result.Output.ShouldContain("Assimalign.Cohesion.Database.Sql", Case.Sensitive);
        result.Output.ShouldContain(renamed, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Guard reports all offending resolved identities")]
    public async Task ResolvedReferences_MultipleEngines_ShouldReportEveryIdentity()
    {
        // Arrange
        using var probe = new GuardProbe();
        string sql = probe.CopyEngine("Safe.Sql.dll");
        string root = Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.Database.dll");
        // Act
        var result = await probe.ThinAsync("", referencePaths: $"<ReferencePath Include=\"{GuardProbe.Escape(sql)};{GuardProbe.Escape(root)}\" />");
        // Assert
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("Assimalign.Cohesion.Database.Sql [", Case.Sensitive);
        result.Output.ShouldContain("Assimalign.Cohesion.Database [", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Actual resolved contract assemblies remain allowed")]
    public async Task ResolvedReferences_ContractClosure_ShouldAllow()
    {
        // Arrange
        using var probe = new GuardProbe();
        string[] identities = ["Types", "Execution", "Protocol", "Sql.Protocol", "Graph.Protocol", "Documents.Protocol", "KeyValuePair.Protocol", "Blob.Protocol"];
        string paths = string.Join(';', identities.Select(identity => Path.Combine(AppContext.BaseDirectory, $"Assimalign.Cohesion.Database.{identity}.dll")));
        // Act
        var result = await probe.ThinAsync("", referencePaths: $"<ReferencePath Include=\"{GuardProbe.Escape(paths)}\" />");
        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Real SDK resolution rejects HintPath, package and transitive bridge routes")]
    [InlineData("hint")]
    [InlineData("package")]
    [InlineData("bridge")]
    [InlineData("renamed-project")]
    public async Task Build_IndirectEngineRoute_ShouldReject(string route)
    {
        // Arrange
        using var probe = new GuardProbe();
        // Act
        var result = await probe.SdkRouteAsync(route);
        // Assert
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("COHDBCLIENT001", Case.Sensitive);
        result.Output.ShouldContain("Assimalign.Cohesion.Database.Sql", Case.Sensitive);
    }

    private sealed class GuardProbe : IDisposable
    {
        private readonly string _repository;
        private readonly string _configuration;
        internal string DirectoryPath { get; }

        internal GuardProbe()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "resources/Database/Directory.Build.targets")))
            { directory = directory.Parent; }
            _repository = directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
            _configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            DirectoryPath = Path.Combine(_repository, "_out", "database-client-guard", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Path.Combine(DirectoryPath, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(DirectoryPath, "Directory.Build.targets"), "<Project />");
            // A tiny repository index exercises the real name-only/private conversion
            // without enumerating every unrelated build output for each theory row.
            string index = Path.Combine(DirectoryPath, "resources", "engines");
            Directory.CreateDirectory(index);
            foreach (string model in new[] { "Sql", "Graph" })
            { File.WriteAllText(Path.Combine(index, $"Assimalign.Cohesion.Database.{model}.csproj"), "<Project />"); }
        }

        internal static string Escape(string text) => SecurityElement.Escape(text)!;

        internal string CopyEngine(string filename)
        {
            string path = Path.Combine(DirectoryPath, filename);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.Database.Sql.dll"), path);
            return path;
        }

        internal async Task<ProbeResult> ThinAsync(string items, string projectName = "Assimalign.Cohesion.Database.Sql.Client",
            string assemblyName = "Assimalign.Cohesion.Database.Sql.Client", string target = "ResolveAssemblyReferences",
            string subdirectory = "shipping", string? repository = null, string referencePaths = "")
        {
            string directory = Path.Combine(DirectoryPath, subdirectory);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, projectName + ".proj");
            File.WriteAllText(path, $"""
                <Project>
                  <PropertyGroup>
                    <CohesionRepositoryDirectory>{Escape((repository ?? DirectoryPath) + Path.DirectorySeparatorChar)}</CohesionRepositoryDirectory>
                    <Configuration>{_configuration}</Configuration><TargetFramework>net10.0</TargetFramework>
                    <RootNamespace>{Escape(projectName)}</RootNamespace>
                  </PropertyGroup>
                  <ItemGroup>{items}</ItemGroup>
                  <Import Project="{Escape(Path.Combine(_repository, "resources/Database/Directory.Build.targets"))}" />
                  <PropertyGroup><AssemblyName>{Escape(assemblyName)}</AssemblyName></PropertyGroup>
                  <Target Name="ResolveAssemblyReferences"><ItemGroup>{referencePaths}</ItemGroup></Target>
                  <Target Name="CoreCompile" />
                </Project>
                """);
            return await RunAsync(path, "-t:" + target);
        }

        internal async Task<ProbeResult> SdkRouteAsync(string route)
        {
            string engine = CopyEngine("Harmless.Asset.dll");
            string item;
            if (route == "package")
            {
                string feed = Path.Combine(DirectoryPath, "feed");
                Directory.CreateDirectory(feed);
                using (var archive = ZipFile.Open(Path.Combine(feed, "Guard.Engine.1.0.0.nupkg"), ZipArchiveMode.Create))
                {
                    using (var writer = new StreamWriter(archive.CreateEntry("Guard.Engine.nuspec").Open()))
                    { writer.Write("<package><metadata><id>Guard.Engine</id><version>1.0.0</version><authors>Guard</authors><description>Guard input</description></metadata></package>"); }
                    archive.CreateEntryFromFile(engine, "lib/net10.0/Harmless.Asset.dll");
                }
                item = "<PackageReference Include=\"Guard.Engine\" Version=\"1.0.0\" />";
            }
            else if (route == "bridge" || route == "renamed-project")
            {
                string engineDirectory = Path.Combine(DirectoryPath, "engine");
                string bridgeDirectory = Path.Combine(DirectoryPath, "bridge");
                Directory.CreateDirectory(engineDirectory);
                Directory.CreateDirectory(bridgeDirectory);
                string engineProject = Path.Combine(engineDirectory, "Assimalign.Cohesion.Database.Sql.csproj");
                File.WriteAllText(engineProject, SdkProject("", "Assimalign.Cohesion.Database.Sql", false));
                string bridge = Path.Combine(bridgeDirectory, "Safe.Bridge.csproj");
                string bridgeItem = route == "bridge" ? $"<ProjectReference Include=\"{Escape(engineProject)}\" />" : "";
                File.WriteAllText(bridge, SdkProject(bridgeItem, route == "bridge" ? "Safe.Bridge" : "Assimalign.Cohesion.Database.Sql", false));
                item = $"<ProjectReference Include=\"{Escape(bridge)}\" />";
            }
            else
            { item = $"<Reference Include=\"Safe.Alias\"><HintPath>{Escape(engine)}</HintPath></Reference>"; }
            string path = Path.Combine(DirectoryPath, "Renamed.Client.csproj");
            File.WriteAllText(path, SdkProject(item, "Assimalign.Cohesion.Database.Sql.Client", true));
            File.WriteAllText(Path.Combine(DirectoryPath, "NuGet.Config"), $"<configuration><packageSources><clear /><add key=\"local\" value=\"{Escape(Path.Combine(DirectoryPath, "feed"))}\" /></packageSources></configuration>");
            Directory.CreateDirectory(Path.Combine(DirectoryPath, "feed"));
            return await RunAsync(path, "-restore", "-t:Build");
        }

        private string SdkProject(string items, string assemblyName, bool guarded) => $"""
            <Project>
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><RootNamespace>Guard.Input</RootNamespace>
                <AssemblyName>{assemblyName}</AssemblyName><Configuration>{_configuration}</Configuration>
                <CohesionRepositoryDirectory>{Escape(DirectoryPath + Path.DirectorySeparatorChar)}</CohesionRepositoryDirectory>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <NuGetAudit>false</NuGetAudit><RestorePackagesPath>{Escape(Path.Combine(DirectoryPath, "packages"))}</RestorePackagesPath>
              </PropertyGroup>
              <ItemGroup>{items}</ItemGroup>
              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
              {(guarded ? $"<Import Project=\"{Escape(Path.Combine(_repository, "resources/Database/Directory.Build.targets"))}\" />" : "")}
            </Project>
            """;

        private async Task<ProbeResult> RunAsync(string project, params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = DirectoryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            start.ArgumentList.Add("msbuild");
            start.ArgumentList.Add(project);
            foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
            start.ArgumentList.Add("-nr:false");
            start.ArgumentList.Add("-p:UseSharedCompilation=false");
            start.ArgumentList.Add("-p:Configuration=" + _configuration);
            start.ArgumentList.Add("-v:minimal");
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
            return new(process.ExitCode, await output + await error);
        }

        public void Dispose()
        {
            string parent = Path.Combine(_repository, "_out", "database-client-guard") + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(DirectoryPath).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Guard input escaped its workspace."); }
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed record ProbeResult(int ExitCode, string Output);
}
