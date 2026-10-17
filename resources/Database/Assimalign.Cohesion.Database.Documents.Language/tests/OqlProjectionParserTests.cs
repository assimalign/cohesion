using System;
using System.Linq;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlProjectionParserTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - SELECT quantifiers and VALUE retain their AST state")]
    [InlineData("SELECT DISTINCT category FROM items", true, false, "category")]
    [InlineData("SELECT ALL name FROM items", false, false, "name")]
    [InlineData("SELECT VALUE d.name FROM items d", false, true, "name")]
    [InlineData("SELECT RAW d.name FROM items d", false, true, "name")]
    [InlineData("SELECT DISTINCT VALUE d.category FROM items d", true, true, "category")]
    public void Parse_SelectKind_ShouldRetainAst(string text, bool distinct, bool value, string field)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var query = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        query.IsDistinct.ShouldBe(distinct);
        query.IsValue.ShouldBe(value);
        query.Collection.ShouldBe("items");
        query.Alias.ShouldBe(text.EndsWith("items d", StringComparison.Ordinal) ? "d" : null);
        var projection = query.Projections.ShouldHaveSingleItem();
        projection.Alias.ShouldBeNull();
        projection.Expression.ShouldBeOfType<OqlPathExpression>().Segments.Select(segment => segment.Name)
            .ShouldBe(query.Alias is null ? new[] { field } : new[] { "d", field });
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Constructors retain nested expressions and member order")]
    public void Parse_Constructors_ShouldRetainAst()
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse("SELECT {\"name\": d.name, tags: [d.tags[0], 1], 'addr': {city: d.address.city}} AS shaped FROM items d");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var projection = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections.ShouldHaveSingleItem();
        projection.Alias.ShouldBe("shaped");
        var members = projection.Expression.ShouldBeOfType<OqlObjectExpression>().Members;
        members.Select(member => member.Key).ShouldBe(["name", "tags", "addr"]);
        members[0].Value.ShouldBeOfType<OqlPathExpression>().Segments.Select(segment => segment.Name).ShouldBe(["d", "name"]);
        var elements = members[1].Value.ShouldBeOfType<OqlArrayExpression>().Elements;
        elements.Count.ShouldBe(2);
        elements[0].ShouldBeOfType<OqlPathExpression>().Segments.Last().Index.ShouldBe(0);
        elements[1].ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeOfType<JsonElement>().GetInt32().ShouldBe(1);
        var city = members[2].Value.ShouldBeOfType<OqlObjectExpression>().Members.ShouldHaveSingleItem();
        city.Key.ShouldBe("city");
        city.Value.ShouldBeOfType<OqlPathExpression>().Segments.Select(segment => segment.Name).ShouldBe(["d", "address", "city"]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Reserved fields use quoting or paths and AS accepts labels")]
    [InlineData("SELECT d.value AS v, \"value\" FROM items d", "v")]
    [InlineData("SELECT d.x AS value FROM items d", "value")]
    [InlineData("SELECT x AS limit FROM items", "limit")]
    [InlineData("SELECT x AS select FROM items", "select")]
    [InlineData("SELECT x AS set FROM items", "set")]
    public void Parse_ReservedLabel_ShouldAccept(string text, string alias)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var projections = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections;
        projections[0].Alias.ShouldBe(alias);
        if (projections.Count == 2)
        {
            projections[0].Expression.ShouldBeOfType<OqlPathExpression>().Segments.Select(segment => segment.Name).ShouldBe(["d", "value"]);
            projections[1].Expression.ShouldBeOfType<OqlPathExpression>().Segments.ShouldHaveSingleItem().Name.ShouldBe("value");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Invalid projections report exact located syntax errors")]
    [InlineData("SELECT VALUE a, b", 14, 15)]
    [InlineData("SELECT VALUE a AS x", 15, 17)]
    [InlineData("SELECT VALUE *", 13, 14)]
    [InlineData("SELECT {a 1} FROM items", 10, 11)]
    [InlineData("SELECT {(a): 1} FROM items", 8, 9)]
    [InlineData("SELECT [1, 2", 12, 12)]
    [InlineData("SELECT value FROM items", 7, 12)]
    [InlineData("SELECT raw FROM items", 7, 10)]
    public void Parse_InvalidProjection_ShouldReportSpan(string text, int start, int end)
    {
        // Arrange / Act
        var diagnostics = new OqlQueryParser().Parse(text).Diagnostics.ToArray();
        // Assert
        diagnostics.ShouldHaveSingleItem().Code.ShouldBe("OQL0002");
        diagnostics[0].Start.ShouldBe(start);
        diagnostics[0].End.ShouldBe(end);
        diagnostics[0].Line.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Misplaced quantifiers stay one capability refusal")]
    [InlineData("SELECT COUNT(DISTINCT x) FROM items", "COUNT(DISTINCT)")]
    [InlineData("SELECT x FROM items WHERE DISTINCT x", "DISTINCT")]
    [InlineData("SELECT {a:DISTINCT} FROM items", "DISTINCT")]
    [InlineData("SELECT {'a':ALL} FROM items", "ALL")]
    [InlineData("CREATE INDEX ix ON items (ALL tags)", "ALL")]
    [InlineData("CREATE INDEX ix ON items (DISTINCT ARRAY t FOR t IN tags END)", "DISTINCT")]
    public void Parse_MisplacedQuantifier_ShouldRejectOnce(string text, string construct)
    {
        // Arrange / Act
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Message.ShouldNotBeNull().ShouldContain(construct, Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Reserved words in rejected parameter forms keep parameter diagnostics")]
    [InlineData("SELECT :distinct FROM items")]
    [InlineData("SELECT {a: :distinct} FROM items")]
    public void Parse_AdjacentParameter_ShouldKeepSyntaxDiagnostic(string text)
    {
        // Arrange / Act
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("OQL0002");
        diagnostic.Message.ShouldNotBeNull().ShouldContain("parameter", Case.Insensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Constructor nesting is bounded without throwing")]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_DeepConstructor_ShouldReportDepth(bool objects)
    {
        // Arrange
        string expression = "0";
        for (int i = 0; i < 140; i++) { expression = objects ? "{a:" + expression + "}" : "[" + expression + "]"; }
        // Act
        var diagnostics = new OqlQueryParser().Parse("SELECT " + expression + " FROM items").Diagnostics;
        // Assert
        diagnostics.ShouldContain(diagnostic => diagnostic.Code == "OQL0005");
    }
}
