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

    [Fact(DisplayName = "Cohesion Test [Storage] - Probe: each reconstructed page has one scratch clear site (#1744)")]
    public void Read_RecoveredPage_ShouldHaveOnlyOneScratchClearSite()
    {
        // Arrange.
        string source = File.ReadAllText(Source("Assimalign.Cohesion.Database.Storage/src/Internal/Recovery/StorageRecoveredStream.cs"));

        // Act: a source work counter pins the redundant memset, without a timing assertion.
        int clears = source.Split("Array.Clear(_page)", StringSplitOptions.None).Length - 1;

        // Assert.
        clears.ShouldBe(1);
    }

    private static string Source(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "resources", "Database"))) { directory = directory.Parent; }
        return Path.Combine(directory!.FullName, "resources", "Database", relative);
    }
}
