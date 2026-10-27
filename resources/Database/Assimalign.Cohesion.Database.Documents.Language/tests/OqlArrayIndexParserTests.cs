using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlArrayIndexParserTests
{
    [Theory(DisplayName = "Cohesion Test [OQL] - DISTINCT ARRAY index keys retain binding and source spans (#1530)")]
    [InlineData("DISTINCT ARRAY v.price FOR v IN variants END")]
    [InlineData("DISTINCT ARRAY v FOR v IN tags WHEN v != 'skip' END")]
    [InlineData("DISTINCT ARRAY v['odd.name'] FOR v IN \"odd.source\" WHEN v.stock > 0 AND NOT (v.x IS MISSING) END")]
    public void Parse_ArrayIndex_ShouldKeepOrderedKeysAndBinding(string key)
    {
        // Arrange / Act.
        string text = $"CREATE INDEX IF NOT EXISTS ix ON items (category, {key}, tenant)";
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(text);

        // Assert.
        statement.Diagnostics.ShouldBeEmpty();
        var create = statement.OqlExpression.ShouldBeOfType<OqlCreateIndexExpression>();
        create.Keys.Count.ShouldBe(3);
        create.Paths.ShouldBeEmpty();
        create.IfNotExists.ShouldBeTrue();
        create.Keys[0].ShouldBeOfType<OqlPathExpression>();
        var array = create.Keys[1].ShouldBeOfType<OqlComprehensionExpression>();
        array.Variable.ShouldBe("v");
        array.IsFirst.ShouldBeFalse();
        array.Collection.ShouldBeOfType<OqlPathExpression>();
        array.Projection.ShouldBeOfType<OqlPathExpression>();
        array.Location!.Start.ShouldBe(text.IndexOf("ARRAY", System.StringComparison.Ordinal));
        create.Keys[2].ShouldBeOfType<OqlPathExpression>();
        create.Text.ShouldBe(text);
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Array index grammar keeps syntax and capability refusals (#1530)")]
    [InlineData("ALL ARRAY v FOR v IN tags END", "CK00001")]
    [InlineData("DISTINCT tags", "CD00002")]
    [InlineData("DISTINCT ARRAY v FOR v IN END", "CD00002")]
    [InlineData("DISTINCT ARRAY v FOR v IN tags", "CD00002")]
    [InlineData("DISTINCT ARRAY v FOR v WITHIN tags END", "CK00001")]
    [InlineData("DISTINCT ARRAY v FOR v IN tags, w IN other END", "CK00001")]
    public void Parse_InvalidArrayIndex_ShouldReturnOneLocatedDiagnostic(string key, string code)
    {
        // Arrange / Act.
        var statement = new OqlQueryParser().Parse($"CREATE INDEX ix ON items ({key})");

        // Assert.
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldNotBeNull();
    }
}
