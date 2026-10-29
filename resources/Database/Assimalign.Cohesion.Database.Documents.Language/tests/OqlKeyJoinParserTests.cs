using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlKeyJoinParserTests
{
    [Theory(DisplayName = "Cohesion Test [OQL] - Key joins: grammar preserves ordered binding kinds")]
    [InlineData("JOIN", false, false)]
    [InlineData("INNER JOIN", false, false)]
    [InlineData("LEFT JOIN", true, false)]
    [InlineData("LEFT OUTER JOIN", true, false)]
    [InlineData("NEST", false, true)]
    [InlineData("INNER NEST", false, true)]
    [InlineData("LEFT NEST", true, true)]
    [InlineData("LEFT OUTER NEST", true, true)]
    public void Parse_KeyLookup_ShouldPreserveBindingOrder(string form, bool left, bool nest)
    {
        // Arrange
        string text = "SELECT b.* FROM a AS a UNNEST a.keys AS k " + form + " docs AS b ON META(b).id = k LEFT UNNEST b.tags AS t";
        // Act
        var statement = new OqlQueryParser().Parse(text).ShouldBeOfType<OqlQueryStatement>();
        var select = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        select.Bindings.Count.ShouldBe(3);
        select.Bindings[0].ShouldBeOfType<OqlUnnestExpression>().Variable.ShouldBe("k");
        var join = select.Bindings[1].ShouldBeOfType<OqlJoinExpression>();
        join.Collection.ShouldBe("docs");
        join.Variable.ShouldBe("b");
        join.IsLeft.ShouldBe(left);
        join.IsNest.ShouldBe(nest);
        join.Expression.ShouldBeOfType<OqlPathExpression>().Segments.Single().Name.ShouldBe("k");
        select.Bindings[2].ShouldBeOfType<OqlUnnestExpression>().IsLeft.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Key joins: explicit aliases avoid the unresolved collection-name rule")]
    [InlineData("SELECT * FROM a JOIN b AS b ON META(b).id = 'k'")]
    [InlineData("SELECT * FROM a AS a JOIN b ON META(b).id = 'k'")]
    [InlineData("SELECT * FROM a AS a UNNEST a.keys JOIN b AS b ON META(b).id = 'k'")]
    [InlineData("SELECT * FROM a AS a JOIN b AS b ON META(b).id = 'k' UNNEST b.tags")]
    public void Parse_UnaliasedSource_ShouldRefuseWithLocatedCode(string text)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(text).ShouldBeOfType<OqlQueryStatement>();
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00016");
        statement.Diagnostics.Single().Start.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Key joins: general predicates legacy keys and DML joins remain named refusals")]
    [InlineData("SELECT * FROM a AS a JOIN b AS b ON a.k = b.k", "JOIN")]
    [InlineData("SELECT * FROM a AS a NEST b AS b ON META(b).id = a.k AND TRUE", "NEST")]
    [InlineData("SELECT * FROM a AS a JOIN b AS b ON KEYS a.k", "ON KEYS")]
    [InlineData("UPDATE a AS a JOIN b AS b ON META(b).id = a.k SET k = 1", "JOIN")]
    [InlineData("DELETE FROM a AS a NEST b AS b ON META(b).id = a.k", "NEST")]
    public void Parse_UnsupportedForm_ShouldNameConstruct(string text, string construct)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(text).ShouldBeOfType<OqlQueryStatement>();
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        statement.Diagnostics.Single().Message!.ShouldContain(construct, Case.Sensitive);
    }
}
