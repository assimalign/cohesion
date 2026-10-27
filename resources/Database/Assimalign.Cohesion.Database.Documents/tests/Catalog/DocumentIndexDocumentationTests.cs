using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed class DocumentIndexDocumentationTests
{
    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Metadata XML documents scalar path guards and array path shape (#1530)")]
    public void MetadataXml_PublicContracts_ShouldDescribePathValidationAndArrayShape()
    {
        // Arrange.
        string project = ProjectDirectory();
        string source = File.ReadAllText(Path.Combine(project, "src/Catalog/ValueObjects/DocumentIndexMetadata.cs"));

        // Act.
        var single = Documentation(source, "public DocumentIndexMetadata(Guid collectionId, string name, string path,");
        var paths = Documentation(source, "public DocumentIndexMetadata(Guid collectionId, string name, IReadOnlyList<string> paths,");
        var path = Documentation(source, "public string Path =>");
        var seek = Documentation(File.ReadAllText(Path.Combine(project, "src/Catalog/DocumentCatalog.Indexes.cs")),
            "public async ValueTask<IReadOnlyList<DocumentCatalogEntry>> SearchIndexAsync(Guid collectionId, string indexName,\n        IReadOnlyList<object> equalityPrefix,");

        // Assert.
        single.Elements("exception").ShouldContain(node => (string?)node.Attribute("cref") == "ArgumentException");
        paths.Elements("exception").ShouldContain(node => (string?)node.Attribute("cref") == "ArgumentException");
        path.Value.ShouldContain("array index", Case.Sensitive);
        seek.Elements("exception").Single(node => (string?)node.Attribute("cref") == "ArgumentException")
            .Value.ShouldContain("key count", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Predicate XML precisely documents unsupported constants and bounds (#1530)")]
    public void PredicateXml_ArgumentGuards_ShouldDescribeConstantsDepthAndNodeLimit()
    {
        // Arrange.
        string source = File.ReadAllText(Path.Combine(ProjectDirectory(), "src/Catalog/ValueObjects/DocumentIndexPredicate.cs"));

        // Act.
        var compare = Documentation(source, "public static DocumentIndexPredicate Compare(");
        var logical = new[] { "And", "Or", "Not" }.Select(name => Documentation(source, "public static DocumentIndexPredicate " + name + "("));

        // Assert: these guards intentionally remain ArgumentException in the typed API.
        string constants = compare.Elements("exception").Single(node => (string?)node.Attribute("cref") == "ArgumentException").Value;
        constants.ShouldContain("Double", Case.Sensitive);
        constants.ShouldContain("Single", Case.Sensitive);
        foreach (var member in logical)
        {
            string limit = member.Elements("exception").Single(node => (string?)node.Attribute("cref") == "ArgumentException").Value;
            limit.ShouldContain("32 levels", Case.Sensitive);
            limit.ShouldContain("128 nodes", Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Registry and DESIGN preserve array refusal and typed guard contracts (#1530)")]
    public void Documentation_ArrayRefusals_ShouldMatchRegistryAndTypedGuardPolicy()
    {
        // Arrange.
        string project = ProjectDirectory();

        // Act.
        string design = File.ReadAllText(Path.Combine(project, "docs/DESIGN.md"));
        string registry = File.ReadAllText(Path.Combine(project, "../Assimalign.Cohesion.Database/docs/ERROR-CODES.md"));

        // Assert.
        registry.ShouldContain("| New (#1530) | CD03025 |", Case.Sensitive);
        design.ShouldContain("ArgumentException", Case.Sensitive);
        design.ShouldContain("Double", Case.Sensitive);
        design.ShouldContain("OQ-I2's DDL", Case.Sensitive);
        design.ShouldContain("leading array key", Case.Sensitive);
    }

    private static string ProjectDirectory([CallerFilePath] string file = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));

    private static XElement Documentation(string source, string declaration)
    {
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        int end = source.IndexOf(declaration, StringComparison.Ordinal);
        end.ShouldBeGreaterThanOrEqualTo(0);
        int start = source.LastIndexOf("    /// <summary>", end, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        return XElement.Parse("<member>" + string.Join('\n', source[start..end].Split('\n')
            .Where(line => line.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Select(line => line.TrimStart()[3..])) + "</member>");
    }
}
