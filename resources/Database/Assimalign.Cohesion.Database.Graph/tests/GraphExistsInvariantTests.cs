using System.IO;
using System.Runtime.CompilerServices;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphExistsInvariantTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - EXISTS: the synchronous seam records suspended inner evaluations")]
    public void Source_SynchronousSeam_ShouldRecordIncompleteEvaluations()
    {
        // Arrange
        string project = Path.Combine(TestDirectory(), "..");
        // Act
        string source = File.ReadAllText(Path.Combine(project, "src", "Internal", "Evaluation", "GraphExpressionEvaluator.Exists.cs"));
        // Assert: the corpus counter must sit at the actual sync/async join.
        source.ShouldContain("registry.ObserveInnerEvaluation(result.IsCompleted)", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - EXISTS: documentation distinguishes supported braces and the async prerequisite")]
    public void Documentation_ExistsBoundary_ShouldDescribeSupportedFormsAndInvariant()
    {
        // Arrange
        string project = Path.Combine(TestDirectory(), "..");
        // Act
        string dialect = File.ReadAllText(Path.Combine(project, "..", "Assimalign.Cohesion.Database.Graph.Language", "docs", "DIALECT.md"));
        string design = File.ReadAllText(Path.Combine(project, "docs", "DESIGN.md"));
        // Assert
        dialect.ShouldContain("parenthesized EXISTS", Case.Sensitive);
        dialect.ShouldNotContain("\nEXISTS, quantified predicates and nested property access remain CK00001", Case.Sensitive);
        design.ShouldContain("before any inner read can suspend", Case.Sensitive);
        design.ShouldContain("OQ-Q1", Case.Sensitive);
    }

    private static string TestDirectory([CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;
}
