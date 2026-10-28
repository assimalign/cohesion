using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphMergeThirdPassDocumentationTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - MERGE design and registry describe the same bound node condition (#1735)")]
    public void Documentation_BoundNodeDiagnostic_ShouldMatchRegistry()
    {
        // Arrange
        string project = Path.GetFullPath(Path.Combine(TestDirectory(), ".."));

        // Act / Assert
        File.ReadLines(Path.Combine(project, "docs", "DESIGN.md")).Single(line => line.StartsWith("| `CG03005` |", StringComparison.Ordinal))
            .ShouldBe("| `CG03005` | Bound MERGE node is null, absent or incompatible with its node pattern |");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - recovery fixture documents its allocation mode (#1735)")]
    [InlineData("docs/OVERVIEW.md", "allocations")]
    [InlineData("docs/DESIGN.md", "allocations")]
    [InlineData("Program.cs", "Usage: seed|verify directory; allocations injectedKind")]
    public void Documentation_AllocationMode_ShouldDescribeUsage(string file, string expected)
    {
        // Arrange
        string fixture = Path.Combine(TestDirectory(), "..", "fixtures", "Assimalign.Cohesion.Database.Graph.RecoveryFixture");

        // Act / Assert
        File.ReadAllText(Path.Combine(fixture, file)).ShouldContain(expected, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - overlay index access cannot silently install a budget (#1735)")]
    public void Source_UnusedIndexHelper_ShouldBeAbsent()
    {
        // Arrange
        string project = Path.Combine(TestDirectory(), "..");

        // Act / Assert
        File.ReadAllText(Path.Combine(project, "src", "Internal", "Execution", "GraphUpdates.cs"))
            .ShouldNotContain("SearchIndexAsync", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - MERGE design describes session close in every lock queue (#1735)")]
    public void Documentation_EndedOperation_ShouldIncludeEveryQueue()
    {
        // Arrange
        string project = Path.Combine(TestDirectory(), "..");

        // Act / Assert
        File.ReadAllText(Path.Combine(project, "docs", "DESIGN.md"))
            .ShouldContain("writer, parent and pattern lock queues", Case.Sensitive);
    }

    private static string TestDirectory([CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;
}
