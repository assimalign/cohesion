using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlSourceParserTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - FROM: derived and expression sources retain their AST and source span")]
    [InlineData("SELECT * FROM (SELECT n FROM items) AS d", true)]
    [InlineData("SELECT * FROM (SELECT n FROM items) d", true)]
    [InlineData("SELECT * FROM (SELECT n FROM (SELECT n FROM items) d) e", true)]
    [InlineData("SELECT letting.n FROM (SELECT n FROM items GROUP BY n) letting", true)]
    [InlineData("SELECT RAW v FROM [1,2] AS v", false)]
    [InlineData("SELECT RAW v FROM @p AS v", false)]
    [InlineData("SELECT RAW v FROM {a:1} AS v", false)]
    [InlineData("SELECT RAW v FROM NULL AS v", false)]
    [InlineData("SELECT RAW v FROM MISSING AS v", false)]
    [InlineData("SELECT RAW v FROM ARRAY n FOR n IN [1,2] END AS v", false)]
    [InlineData("SELECT RAW v FROM COALESCE((SELECT RAW n FROM items), []) AS v", false)]
    public void Parse_FromSource_ShouldRetainExpressionAndText(string text, bool derived)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var query = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        query.Source.ShouldNotBeNull();
        (query.Source is OqlSubqueryExpression).ShouldBe(derived);
        query.Collection.ShouldBeEmpty();
        query.Text.ShouldBe(text);
        query.Source.Location!.Start.ShouldBeGreaterThan(text.IndexOf("FROM", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - FROM: aliases and deferred forms preserve coded diagnostics")]
    [InlineData("SELECT * FROM (SELECT n FROM items)", "CD00015", "alias")]
    [InlineData("SELECT * FROM [1,2] v", "CD00002", "v")]
    [InlineData("SELECT * FROM [1] AS a JOIN [2] AS b ON TRUE", "CK00001", "JOIN")]
    [InlineData("SELECT * FROM [1] AS a NEST [2] AS b ON TRUE", "CK00001", "NEST")]
    [InlineData("SELECT * FROM [1] AS a, [2] AS b", "CK00001", "JOIN")]
    [InlineData("INSERT INTO items (KEY k) SELECT n AS k FROM (SELECT n FROM items) d", "CK00001", "DML")]
    public void Parse_InvalidSource_ShouldRetainCodeAndForm(string text, string code, string form)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(text);
        // Assert
        if (form == "DML") { statement.Diagnostics.ShouldBeEmpty(); return; }
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code && diagnostic.Message!.Contains(form, StringComparison.OrdinalIgnoreCase));
    }
}
