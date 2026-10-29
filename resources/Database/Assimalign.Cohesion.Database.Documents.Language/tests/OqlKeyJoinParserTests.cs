using System;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlKeyJoinParserTests
{
    [Fact(DisplayName = "Cohesion Test [OQL] - Key joins: source documentation links supported joins without blanket refusals")]
    public void Dialect_ExpressionSourceSection_ShouldDescribeKeyJoinSupport()
    {
        // Arrange
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "DIALECT.md")))
        { directory = directory.Parent; }
        directory.ShouldNotBeNull();
        string dialect = File.ReadAllText(Path.Combine(directory.FullName, "docs", "DIALECT.md"));
        int start = dialect.IndexOf("## Derived and expression FROM sources", StringComparison.Ordinal);
        int end = dialect.IndexOf("## RETURNING", start, StringComparison.Ordinal);
        // Act
        string sources = dialect[start..end];
        // Assert: the source section must defer to the executable key-join contract.
        sources.ShouldContain("](#key-join-and-nest-1732-oq-j1)", Case.Sensitive);
        sources.ShouldNotContain("JOIN/NEST (including comma joins) retain named CK00001 refusals", Case.Sensitive);
        string design = File.ReadAllText(Path.Combine(directory.Parent!.FullName,
            "Assimalign.Cohesion.Database.Documents", "docs", "DESIGN.md"));
        design.ShouldNotContain("does not add JOIN or cross-collection execution", Case.Sensitive);
        design.ShouldNotContain("nested queries, deferred document mutations", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OQL] - Key joins: statement-wide alias requirements reset when the parser is reused")]
    public void Parse_JoinedThenOrdinary_ShouldResetAliasRequirement()
    {
        // Arrange
        var parser = new OqlQueryParser();
        // Act
        var joined = parser.Parse("SELECT (SELECT RAW 1 FROM a AS a JOIN b AS b ON META(b).id = 'k') FROM c");
        var ordinary = parser.Parse("SELECT * FROM c UNNEST tags");
        // Assert
        joined.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00016");
        ordinary.Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [OQL] - Source bindings: an array-only direct AST retains the ordinary UNNEST representation")]
    public void Construct_ArrayOnlyBindings_ShouldPreserveOrdinarySourceRepresentation()
    {
        // Arrange
        var parsed = new OqlQueryParser().Parse("SELECT * FROM items UNNEST tags AS t").ShouldBeOfType<OqlQueryStatement>();
        var original = parsed.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        // Act
        var copy = new OqlSelectExpression(original.Collection, original.Alias, original.Projections, original.Predicate,
            original.GroupBy, original.Having, original.OrderBy, original.IsDistinct, original.IsValue, bindings: original.Unnest);
        // Assert
        copy.Bindings.ShouldBeSameAs(copy.Unnest);
        copy.Bindings.Single().ShouldBeSameAs(original.Unnest.Single());
        copy.Collection.ShouldBe(original.Collection);
        copy.Alias.ShouldBeNull();
    }

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
    [InlineData("SELECT (SELECT RAW 1 FROM a AS a JOIN b AS b ON META(b).id = 'k') FROM c")]
    [InlineData("SELECT (SELECT RAW n FROM c) FROM a AS a JOIN b AS b ON META(b).id = 'k'")]
    [InlineData("SELECT * FROM (SELECT * FROM c) AS a JOIN b AS b ON META(b).id = 'k'")]
    [InlineData("SELECT (SELECT RAW 1 FROM a AS a JOIN b AS b ON META(b).id = 'k') FROM c AS c UNNEST c.tags")]
    public void Parse_UnaliasedSource_ShouldRefuseWithLocatedCode(string text)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(text).ShouldBeOfType<OqlQueryStatement>();
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00016");
        statement.Diagnostics.Single().Start.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Key joins: general predicates legacy keys and DML joins remain named refusals")]
    [InlineData("SELECT * FROM a AS a JOIN b AS b ON a.k = b.k", "JOIN general ON predicate")]
    [InlineData("SELECT * FROM a AS a NEST b AS b ON META(b).id = a.k AND TRUE", "NEST general ON predicate")]
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
