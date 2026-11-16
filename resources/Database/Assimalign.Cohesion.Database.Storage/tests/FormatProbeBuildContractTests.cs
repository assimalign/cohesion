using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

// These are build/source ownership contracts; running Storage must not compile engine test inputs.
public sealed class FormatProbeBuildContractTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Build contract: Storage and all engines explicitly select JournalFormat (#1744)")]
    public void Compile_StorageAndEngines_ShouldSelectJournalFormat()
    {
        // Arrange: shared-source opt-ins are a build contract, not a runtime source-shape pin.
        string[] projects = ["Storage", "Sql", "Documents", "Graph", "KeyValuePair", "Blob"];

        // Act and assert.
        foreach (string project in projects)
        {
            string name = "Assimalign.Cohesion.Database." + project;
            var source = XDocument.Load(Source($"{name}/src/{name}.csproj"));
            source.Descendants("CohesionSharedSource").ShouldContain(item =>
                (string?)item.Attribute("Include") == "Assimalign.Cohesion.Database.Storage"
                && (string?)item.Attribute("Module") == "JournalFormat");
        }
        Directory.Exists(Source("Assimalign.Cohesion.Database.Storage/shared/JournalFormat")).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Probe fixture: Storage tests exclude the engine process launcher (#1744)")]
    public void Compile_StorageTests_ShouldExcludeEngineLauncher()
    {
        // Arrange.
        var project = XDocument.Load(Source("Assimalign.Cohesion.Database.Storage/tests/Assimalign.Cohesion.Database.Storage.Tests.csproj"));

        // Act.
        var items = project.Descendants("Compile").ToArray();

        // Assert.
        items.ShouldContain(item => (string?)item.Attribute("Remove") == @"TestObjects\FormatProbeCrash.cs");
        items.Any(item => ((string?)item.Attribute("Include"))?.Contains("Database.Blob", StringComparison.Ordinal) == true).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Probe fixture: consuming projects own their explicit source links (#1744)")]
    public void Compile_SharedFixtureTargets_ShouldHaveNoProjectNameExceptions()
    {
        // Arrange.
        string targets = File.ReadAllText(Source("FormatProbeTests.targets"));

        // Act.
        var project = XDocument.Parse(targets);

        // Assert.
        project.Descendants("Compile").ShouldBeEmpty();
        targets.ShouldNotContain("MSBuildProjectName", Case.Sensitive);
        string design = File.ReadAllText(Source("Assimalign.Cohesion.Database.Storage/fixtures/Assimalign.Cohesion.Database.Storage.FormatProbeFixture/docs/DESIGN.md"));
        design.ShouldContain("five engine test projects", Case.Sensitive);
    }

    private static string Source(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "resources", "Database"))) { directory = directory.Parent; }
        return Path.Combine(directory!.FullName, "resources", "Database", relative);
    }
}
