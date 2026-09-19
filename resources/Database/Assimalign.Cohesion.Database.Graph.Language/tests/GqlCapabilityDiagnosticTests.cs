using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>Protects capability diagnostics from cascading syntax errors in unsupported literal forms.</summary>
public sealed class GqlCapabilityDiagnosticTests
{
    /// <summary>Unsupported collection literals retain the capability diagnostic consumed by the engine.</summary>
    [Theory(DisplayName = "Cohesion Test [GQL] - Collection literals preserve the original capability diagnostic")]
    [InlineData("INSERT (:Person {tags: [1, 2]})")]
    [InlineData("INSERT (:Person {child: {name: 'Alice'}})")]
    [InlineData("INSERT (:Person)-[:KNOWS {tags: [1, 2]}]->(:Person)")]
    public void Parse_UnsupportedCollectionLiteral_DoesNotMaskCapabilityDiagnostic(string source)
    {
        var statement = new GqlQueryParser().Parse(source);

        statement.Diagnostics.Select(diagnostic => diagnostic.Code).ShouldBe(["COHDBL001"]);
    }
}
