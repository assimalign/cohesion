using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlUpdateParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - SET/REMOVE: supported items retain their AST (#1149)")]
    [InlineData("SET a.age = 5", "property")]
    [InlineData("SET a.age = $age", "property")]
    [InlineData("SET a.x = b.y", "property")]
    [InlineData("SET a.flag = b IS LABELED Graph", "property")]
    [InlineData("SET a.flag = b IS NOT LABELED Schema", "property")]
    [InlineData("SET a.age = a.age + 1", "property")]
    [InlineData("SET a = {name:'A'}", "replace")]
    [InlineData("SET a += {age:1}", "merge")]
    [InlineData("SET a:Admin", "labels")]
    [InlineData("SET a IS Admin", "labels")]
    [InlineData("SET a IS Graph", "labels")]
    [InlineData("SET a:A:B", "labels")]
    [InlineData("SET a.x = 1, a:Admin", "property")]
    [InlineData("REMOVE a.age", "removeProperty")]
    [InlineData("REMOVE a:Admin", "removeLabels")]
    [InlineData("REMOVE a IS Schema", "removeLabels")]
    public void Parse_UpdateItems_ShouldRetainShape(string item, string shape)
    {
        // Arrange / Act
        var query = (GqlQueryStatement)new GqlQueryParser().Parse($"MATCH (a), (b) {item} RETURN a.age");

        // Assert
        query.Diagnostics.ShouldBeEmpty();
        object actual = shape.StartsWith("remove", System.StringComparison.Ordinal)
            ? query.GqlExpression.RemoveItems[0] : query.GqlExpression.SetItems[0];
        switch (shape)
        {
            case "property": actual.ShouldBeOfType<GqlSetPropertyItem>().Variable.ShouldBe("a"); break;
            case "replace": actual.ShouldBeOfType<GqlSetPropertiesItem>().Merge.ShouldBeFalse(); break;
            case "merge": actual.ShouldBeOfType<GqlSetPropertiesItem>().Merge.ShouldBeTrue(); break;
            case "labels": actual.ShouldBeOfType<GqlSetLabelsItem>().Labels.ShouldNotBeEmpty(); break;
            case "removeProperty": actual.ShouldBeOfType<GqlRemovePropertyItem>().Property.ShouldBe("age"); break;
            case "removeLabels": actual.ShouldBeOfType<GqlRemoveLabelsItem>().Labels.ShouldNotBeEmpty(); break;
        }
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - SET/REMOVE: excluded forms report precise diagnostics (#1149)")]
    [InlineData("SET a.x=1", "GQL0002")]
    [InlineData("REMOVE a.x", "GQL0002")]
    [InlineData("MATCH (a) SET a.x.y=1", "GQL0002")]
    [InlineData("MATCH (a) SET a + = {x:1}", "GQL0002")]
    [InlineData("RETURN +=1", "GQL0002")]
    [InlineData("RETURN 1+=2", "GQL0002")]
    [InlineData("MATCH (a)->+=(b) RETURN a", "GQL0002")]
    [InlineData("MATCH (a) SET a.x += 1", "GQL0002")]
    [InlineData("MATCH (a) SET a = {x:1,x:2}", "GQL0006")]
    [InlineData("MATCH (a) SET a.x=[1,2]", "CK00001")]
    [InlineData("MATCH (a) SET a.x=([1,2])", "CK00001")]
    [InlineData("MATCH (a) SET a.x=({x:1})", "CK00001")]
    [InlineData("MATCH (a) SET a={x:([1,2])}", "CK00001")]
    [InlineData("MATCH (a) SET a+={x:({x:1})}", "CK00001")]
    [InlineData("MATCH (a) SET a = $p", "CK00001")]
    [InlineData("MATCH (a) SET a += $p", "CK00001")]
    [InlineData("MATCH (a), (b) SET a = properties(b)", "CK00001")]
    [InlineData("MERGE (n) ON CREATE SET n.x=1", "CK00001")]
    [InlineData("MERGE (n) ON MATCH SET n.x=1", "CK00001")]
    [InlineData("SHOW LABELS SET", "GQL0007")]
    [InlineData("SESSION SET GRAPH other", "CK00001")]
    public void Parse_ExcludedUpdates_ShouldReportOneDiagnostic(string source, string code)
    {
        // Arrange / Act
        var query = new GqlQueryParser().Parse(source);

        // Assert
        var error = query.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        error.Code.ShouldBe(code);
        if (source.StartsWith("MERGE", System.StringComparison.Ordinal)) { error.Message!.ShouldContain("MERGE", Case.Sensitive); }
    }
}
