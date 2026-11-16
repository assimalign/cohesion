using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

[CollectionDefinition(nameof(ModelArchitectureManifestTests), DisableParallelization = true)]
[Collection(nameof(ModelArchitectureManifestTests))]
public sealed class ModelArchitectureManifestTests
{
    [Theory(DisplayName = "Cohesion Test [Database] - Model manifests follow the referenced library rather than the test RID (#1809)")]
    [InlineData(null, null)]
    [InlineData("win-x64", null)]
    [InlineData("win-x64", "win-arm64")]
    public async Task CopyInputs_TestRuntimeIdentifier_ShouldUseModelLibraryManifests(string? testRid, string? modelRid)
    {
        // Arrange: evaluate the real targets without compiling or restoring a fixture.
        string repository = FindRepository();
        string root = Path.Combine(Path.GetTempPath(), "cohesion-model-manifests-" + Guid.NewGuid().ToString("N"));
        string model = Path.Combine(root, "Assimalign.Cohesion.Database.Sql", "src");
        string tests = Path.Combine(root, "Assimalign.Cohesion.Database.Sql", "tests");
        string output = Path.Combine(root, "output");
        Directory.CreateDirectory(model); Directory.CreateDirectory(tests);
        try
        {
            File.Copy(Path.Combine(repository, "resources", "Database", "ModelArchitecture.targets"), Path.Combine(root, "ModelArchitecture.targets"));
            var modelProperties = new XElement("PropertyGroup",
                new XElement("AssemblyName", "Assimalign.Cohesion.Database.Sql"),
                new XElement("IntermediateOutputPath", "obj/$(Configuration)/$(TargetFramework)/"),
                new XElement("IntermediateOutputPath", new XAttribute("Condition", "'$(RuntimeIdentifier)' != ''"), "obj/$(Configuration)/$(TargetFramework)/$(RuntimeIdentifier)/"));
            if (modelRid is not null) { modelProperties.AddFirst(new XElement("RuntimeIdentifier", modelRid)); }
            new XDocument(new XElement("Project", modelProperties, Import())).Save(Path.Combine(model, "Assimalign.Cohesion.Database.Sql.csproj"));
            string project = Path.Combine(tests, "Assimalign.Cohesion.Database.Sql.Tests.csproj");
            new XDocument(new XElement("Project", new XElement("PropertyGroup",
                new XElement("Configuration", "Release"), new XElement("TargetFramework", "net10.0"),
                new XElement("TargetDir", output + Path.DirectorySeparatorChar)), Import())).Save(project);
            string manifests = Path.Combine(model, "obj", "Release", "net10.0", modelRid ?? "");
            Directory.CreateDirectory(manifests);
            string[] names = ["sources", "references", "settings"];
            foreach (string name in names) { File.WriteAllText(Path.Combine(manifests, "DatabaseArchitecture." + name + ".txt"), name); }
            using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var start = new ProcessStartInfo("dotnet")
            { WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            start.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
            foreach (string argument in new[] { "msbuild", project, "-nologo", "-nodeReuse:false", "-verbosity:quiet", "-target:CopyDatabaseArchitectureInputs" })
            { start.ArgumentList.Add(argument); }
            if (testRid is not null) { start.ArgumentList.Add("-p:RuntimeIdentifier=" + testRid); }
            using var process = new Process { StartInfo = start };
            process.Start().ShouldBeTrue();
            var stdout = process.StandardOutput.ReadToEndAsync(guard.Token);
            var stderr = process.StandardError.ReadToEndAsync(guard.Token);
            try
            {
                // Act: only query/copy targets run; no build or test process is nested.
                await process.WaitForExitAsync(guard.Token);
                string diagnostics = await stdout + await stderr;
                // Assert
                process.ExitCode.ShouldBe(0, diagnostics);
                foreach (string name in names)
                { File.ReadAllText(Path.Combine(output, "ModelArchitecture", "DatabaseArchitecture." + name + ".txt")).ShouldBe(name); }
                Directory.GetFiles(Path.Combine(output, "ModelArchitecture")).Length.ShouldBe(names.Length);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static XElement Import() => new("Import", new XAttribute("Project", "../../ModelArchitecture.targets"));

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        { if (File.Exists(Path.Combine(directory.FullName, "resources", "Database", "ModelArchitecture.targets"))) { return directory.FullName; } }
        throw new InvalidOperationException("Database architecture targets were not found.");
    }
}
