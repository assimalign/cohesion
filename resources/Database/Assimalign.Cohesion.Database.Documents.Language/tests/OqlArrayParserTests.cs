using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlArrayParserTests
{
    [Theory(DisplayName = "Cohesion Test [OQL] - Arrays: UNNEST retains its binding and kind")]
    [InlineData("UNNEST d.tags AS t", false, "t")]
    [InlineData("INNER UNNEST d.tags t", false, "t")]
    [InlineData("LEFT UNNEST d.tags AS t", true, "t")]
    [InlineData("LEFT OUTER UNNEST d.tags AS t", true, "t")]
    [InlineData("UNNEST d.tags", false, "tags")]
    public void Parse_Unnest_ShouldRetainBinding(string clause, bool left, string variable)
    {
        // Arrange / Act
        var statement = Parse($"SELECT d.name, t FROM items AS d {clause}");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var binding = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Unnest.ShouldHaveSingleItem();
        binding.IsLeft.ShouldBe(left);
        binding.Variable.ShouldBe(variable);
        binding.Collection.ShouldBeOfType<OqlPathExpression>().Segments.Count.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Arrays: ANY SOME and EVERY retain the scoped predicate")]
    [InlineData("ANY", false)]
    [InlineData("SOME", false)]
    [InlineData("EVERY", true)]
    public void Parse_Quantifier_ShouldRetainPredicate(string quantifier, bool every)
    {
        // Arrange / Act
        var statement = Parse($"SELECT * FROM c WHERE {quantifier} x IN c.items SATISFIES x > 1 END");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var predicate = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Predicate.ShouldBeOfType<OqlQuantifiedExpression>();
        predicate.Variable.ShouldBe("x");
        predicate.IsEvery.ShouldBe(every);
        predicate.Predicate.ShouldBeOfType<OqlBinaryExpression>().Operator.ShouldBe(">");
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Arrays: former unsupported pins and nested bindings now parse")]
    [InlineData("SELECT * FROM c UNNEST c.items AS i")]
    [InlineData("SELECT * FROM c AS x UNNEST x.items AS i")]
    [InlineData("SELECT * FROM c WHERE a = 1 AND EVERY x IN c.items SATISFIES x IN [1, 2] END")]
    [InlineData("SELECT * FROM c WHERE EVERY x IN c.items SATISFIES ANY y IN x.tags SATISFIES y = 1 END END")]
    [InlineData("SELECT * FROM c WHERE EVERY x IN c.items SATISFIES CASE WHEN x > 1 THEN TRUE END AND x IN c.tags END")]
    [InlineData("SELECT CASE WHEN ANY x IN a SATISFIES x END THEN 1 END FROM c")]
    [InlineData("SELECT * FROM c WHERE ANY x IN a SATISFIES CASE WHEN x THEN TRUE ELSE FALSE END END")]
    [InlineData("UPDATE c unnest SET a = 1")]
    [InlineData("UPDATE c SET unnest = TRUE WHERE ANY x IN tags SATISFIES unnest END")]
    [InlineData("UPDATE c SET a = ANY x IN tags SATISFIES unnest END")]
    [InlineData("DELETE FROM c WHERE ANY x IN tags SATISFIES unnest END")]
    [InlineData("DELETE FROM c unnest WHERE unnest.tags IN [[1]]")]
    [InlineData("DELETE FROM c WHERE ANY x IN tags SATISFIES unnest + 1 END")]
    public void Parse_SupportedArrayForm_ShouldParse(string query)
    {
        // Arrange / Act
        var statement = Parse(query);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [OQL] - Arrays: chained UNNEST preserves source order")]
    public void Parse_ChainedUnnest_ShouldRetainOrder()
    {
        // Arrange / Act
        var statement = Parse("SELECT l FROM items d UNNEST d.orders AS o UNNEST o.lines AS l");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var query = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        query.Unnest.Count.ShouldBe(2);
        query.Unnest[0].Variable.ShouldBe("o");
        query.Unnest[1].Variable.ShouldBe("l");
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Arrays: array IN keeps a distinct operand from list IN")]
    [InlineData("'blue' IN d.tags")]
    [InlineData("d.code IN $codes")]
    [InlineData("1 IN [1, 2]")]
    [InlineData("'blue' IN tags")]
    public void Parse_ArrayIn_ShouldRetainArray(string predicate)
    {
        // Arrange / Act
        var statement = Parse($"SELECT * FROM items d WHERE {predicate} AND TRUE");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var conjunction = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Predicate.ShouldBeOfType<OqlBinaryExpression>();
        conjunction.Operator.ShouldBe("AND");
        var membership = conjunction.Left.ShouldBeOfType<OqlInExpression>();
        membership.Array.ShouldNotBeNull();
        membership.Values.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Arrays: malformed and deferred forms have one exact diagnostic")]
    [InlineData("SELECT * FROM c UNNEST «»", "CD00002", null)]
    [InlineData("SELECT * FROM c UNNEST [1] «WHERE» TRUE", "CD00002", null)]
    [InlineData("SELECT ANY x IN [1] «END» FROM c", "CD00002", null)]
    [InlineData("SELECT ANY x IN [1] SATISFIES TRUE«»", "CD00002", null)]
    [InlineData("SELECT * FROM c d UNNEST d.a AS «d»", "CD00010", null)]
    [InlineData("SELECT * FROM c d UNNEST d.a x UNNEST x.b AS «x»", "CD00010", null)]
    [InlineData("SELECT * FROM items LEFT UNNEST tags «items»", "CD00010", null)]
    [InlineData("SELECT * FROM items d UNNEST d.tags UNNEST d.«tags» WHERE TRUE", "CD00010", null)]
    [InlineData("SELECT * FROM items d UNNEST d.tags UNNEST d.«tags»", "CD00010", null)]
    [InlineData("SELECT * FROM items d UNNEST d.tags UNNEST (d.«tags») WHERE TRUE", "CD00010", null)]
    [InlineData("SELECT * FROM items d UNNEST d.tags UNNEST d[«'tags'»] WHERE TRUE", "CD00010", null)]
    [InlineData("SELECT ANY x IN [1], «x» IN [2] SATISFIES TRUE END FROM c", "CD00010", null)]
    [InlineData("SELECT ANY x IN [1]«,» y IN [2] SATISFIES TRUE END FROM c", "CK00001", "multi-binding quantifier")]
    [InlineData("SELECT * FROM c d «NEST» e ON TRUE", "CK00001", "NEST")]
    [InlineData("SELECT * FROM c d LEFT «JOIN» e ON TRUE", "CK00001", "JOIN")]
    [InlineData("SELECT «ANY» AND EVERY x IN a SATISFIES TRUE END FROM c", "CK00001", "ANY AND EVERY")]
    [InlineData("SELECT EVERY x «WITHIN» a SATISFIES TRUE END FROM c", "CK00001", "WITHIN")]
    [InlineData("SELECT ANY x «WITHIN» a SATISFIES TRUE END FROM c", "CK00001", "WITHIN")]
    [InlineData("DELETE FROM c d «UNNEST» d.a t", "CK00001", "UNNEST")]
    [InlineData("UPDATE c d SET a = 1 «UNNEST» d.a t", "CK00001", "UNNEST")]
    [InlineData("UPDATE c «UNNEST» tags t SET a = 1", "CK00001", "UNNEST")]
    [InlineData("DELETE FROM c LEFT OUTER «UNNEST» tags t", "CK00001", "UNNEST")]
    [InlineData("UPDATE c LEFT «UNNEST» tags t SET a = 1", "CK00001", "UNNEST")]
    [InlineData("SELECT ANY x IN a SATISFIES «FIRST»(x) END FROM c", "CK00001", "FIRST")]
    [InlineData("SELECT EVERY x IN a SATISFIES «MERGE» INTO c USING d ON TRUE WHEN MATCHED THEN UPDATE SET a = 1 END FROM c", "CK00001", "MERGE")]
    [InlineData("SELECT ANY x IN [1] SATISFIES CASE WHEN TRUE THEN «MERGE» INTO c USING d ON TRUE WHEN MATCHED THEN UPDATE SET a = 1 END END FROM c", "CK00001", "MERGE")]
    [InlineData("SELECT ANY x IN [1] SATISFIES CASE WHEN FALSE THEN FALSE ELSE «MERGE» INTO c USING d ON TRUE WHEN MATCHED THEN UPDATE SET a = 1 END END FROM c", "CK00001", "MERGE")]
    public void Parse_InvalidArrayForm_ShouldLocateDiagnostic(string marked, string code, string? construct)
    {
        // Arrange
        int start = marked.IndexOf('«');
        int end = marked.IndexOf('»') - 1;
        string query = marked.Replace("«", "", StringComparison.Ordinal).Replace("»", "", StringComparison.Ordinal);
        // Act
        var statement = Parse(query);
        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(end);
        if (construct is not null) { diagnostic.Message.ShouldNotBeNull().ShouldContain(construct, Case.Sensitive); }
    }

    private static OqlQueryStatement Parse(string text) => (OqlQueryStatement)new OqlQueryParser().Parse(text);
}
