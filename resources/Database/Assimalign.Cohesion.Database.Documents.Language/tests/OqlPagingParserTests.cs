using System;
using System.Linq;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlPagingParserTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Paging: every preceding operand and both clause orders parse")]
    [InlineData("SELECT * FROM c LIMIT 10")]
    [InlineData("SELECT * FROM c d LIMIT 1")]
    [InlineData("SELECT * FROM c returning LIMIT 1")]
    [InlineData("SELECT * FROM c returning OFFSET 1")]
    [InlineData("SELECT * FROM c WHERE a = 1 LIMIT 5")]
    [InlineData("SELECT * FROM c WHERE a = 'x' LIMIT 1")]
    [InlineData("SELECT * FROM c WHERE a IS NULL LIMIT 1")]
    [InlineData("SELECT * FROM c WHERE a = TRUE LIMIT 1")]
    [InlineData("SELECT * FROM c WHERE a = @p LIMIT 1")]
    [InlineData("SELECT * FROM c WHERE c.items[0] = 1 LIMIT 1")]
    [InlineData("SELECT * FROM c WHERE (a = 1) LIMIT 1")]
    [InlineData("SELECT a FROM c GROUP BY a LIMIT 1")]
    [InlineData("SELECT a FROM c GROUP BY a HAVING COUNT(*) > 1 LIMIT 1")]
    [InlineData("SELECT * FROM c ORDER BY c.select LIMIT 1")]
    [InlineData("SELECT * FROM c ORDER BY a DESC OFFSET 2")]
    [InlineData("SELECT * FROM c ORDER BY a ASC LIMIT 2")]
    [InlineData("SELECT * FROM c LIMIT $n OFFSET @m")]
    [InlineData("SELECT * FROM c OFFSET 2")]
    [InlineData("SELECT * FROM c OFFSET 2 LIMIT 1")]
    [InlineData("SELECT d.limit, \"offset\" FROM c d LIMIT 1")]
    [InlineData("SELECT a AS limit FROM c LIMIT 1")]
    [InlineData("SELECT * FROM c LIMIT +1 OFFSET -0")]
    [InlineData("SELECT * FROM c LIMIT + /* signed */ 1 OFFSET - 0")]
    [InlineData("SELECT * FROM c LIMIT 2147483648")]
    [InlineData("SELECT * FROM c LIMIT 9223372036854775807 OFFSET 9223372036854775807")]
    public void Parse_Paging_ShouldAccept(string text)
    {
        // Act
        var statement = new OqlQueryParser().Parse(text).ShouldBeOfType<OqlQueryStatement>();
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var select = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        (select.Limit is not null || select.Offset is not null).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Paging: either order retains the same count operands")]
    public void Parse_ClauseOrders_ShouldRetainBothOperands()
    {
        // Arrange / Act
        var parser = new OqlQueryParser();
        var first = parser.Parse("SELECT * FROM c LIMIT 1 OFFSET 2").ShouldBeOfType<OqlQueryStatement>();
        var second = parser.Parse("SELECT * FROM c OFFSET 2 LIMIT 1").ShouldBeOfType<OqlQueryStatement>();
        // Assert
        first.Diagnostics.ShouldBeEmpty();
        second.Diagnostics.ShouldBeEmpty();
        var a = first.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        var b = second.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        a.Limit.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeOfType<JsonElement>().GetInt64()
            .ShouldBe(b.Limit.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeOfType<JsonElement>().GetInt64());
        a.Offset.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeOfType<JsonElement>().GetInt64()
            .ShouldBe(b.Offset.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeOfType<JsonElement>().GetInt64());
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Paging: invalid counts have one OQL0007 at the complete operand")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2.0")]
    [InlineData("1e2")]
    [InlineData("'a'")]
    [InlineData("NULL")]
    [InlineData("NIL")]
    [InlineData("TRUE")]
    [InlineData("FALSE")]
    [InlineData("MISSING")]
    [InlineData("ALL")]
    [InlineData("9223372036854775808")]
    [InlineData("9999999999999999999999999999999999999999")]
    [InlineData("1e999")]
    [InlineData("(1)")]
    [InlineData("[1]")]
    [InlineData("a")]
    [InlineData("+@n")]
    public void Parse_InvalidCount_ShouldReportLocatedCountDiagnostic(string operand)
    {
        // Arrange / Act / Assert
        foreach (string clause in new[] { "LIMIT", "OFFSET" })
        {
            string text = "SELECT * FROM c " + clause + " " + operand;
            var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("OQL0007");
            diagnostic.Message.ShouldBe($"{clause} requires a non-negative integer in the signed 64-bit range.");
            diagnostic.Start.ShouldBe(text.Length - operand.Length);
            diagnostic.End.ShouldBe(text.Length);
            diagnostic.Line.ShouldBe(1);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Paging: a signed multiline invalid operand retains both source lines")]
    public void Parse_MultilineInvalidCount_ShouldRetainFullLocation()
    {
        // Arrange
        const string text = "SELECT * FROM c LIMIT -\n1";
        // Act
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("OQL0007");
        diagnostic.Line.ShouldBe(1);
        diagnostic.Start.ShouldBe(text.IndexOf('-'));
        diagnostic.End.ShouldBe(text.Length);
        var count = new OqlQueryParser().Parse("SELECT * FROM c LIMIT -\n0").ShouldBeOfType<OqlQueryStatement>()
            .OqlExpression.ShouldBeOfType<OqlSelectExpression>().Limit.ShouldNotBeNull();
        count.Location.ShouldNotBeNull().EndLine.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Paging: malformed syntax retains one exact syntax span")]
    [InlineData("SELECT * FROM c LIMIT «»")]
    [InlineData("SELECT * FROM c OFFSET «»")]
    [InlineData("SELECT * FROM c LIMIT 1 «LIMIT» 2")]
    [InlineData("SELECT * FROM c OFFSET 1 «OFFSET» 2")]
    [InlineData("SELECT * FROM c LIMIT 1 «ORDER» BY a")]
    [InlineData("SELECT * FROM c LIMIT 1 «+» 1")]
    [InlineData("SELECT «limit», merge FROM c")]
    [InlineData("SELECT «offset» FROM c")]
    [InlineData("SELECT * FROM c WHERE «limit» = 1")]
    [InlineData("SELECT * FROM c ORDER BY «limit» DESC")]
    public void Parse_MalformedPaging_ShouldReportSyntaxSpan(string marked)
    {
        // Arrange
        int start = marked.IndexOf('«');
        int end = marked.IndexOf('»') - 1;
        string text = marked.Replace("«", "").Replace("»", "");
        // Act
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("OQL0002");
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(end);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Paging: misplaced LIMIT keeps the same syntax diagnostic after any operand")]
    [InlineData("a")]
    [InlineData("1")]
    [InlineData("'x'")]
    [InlineData("TRUE")]
    [InlineData("NULL")]
    [InlineData("@p")]
    [InlineData("c.items[0]")]
    [InlineData("(a)")]
    public void Parse_MisplacedLimit_ShouldReportOneSyntaxDiagnostic(string preceding)
    {
        // Arrange
        string text = $"SELECT * FROM c WHERE {preceding} LIMIT 1 ORDER BY a";
        // Act
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("OQL0002");
        diagnostic.Start.ShouldBe(text.IndexOf("ORDER", StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + 5);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Paging: deferred spellings report one capability diagnostic")]
    [InlineData("SELECT * FROM c LIMIT 5, 10", "LIMIT", "LIMIT m, n (use LIMIT n OFFSET m)")]
    [InlineData("SELECT * FROM c LIMIT ALL, 10", "LIMIT", "LIMIT m, n (use LIMIT n OFFSET m)")]
    [InlineData("SELECT * FROM c LIMIT -5, 10", "LIMIT", "LIMIT m, n (use LIMIT n OFFSET m)")]
    [InlineData("SELECT * FROM c LIMIT 'a', 10", "LIMIT", "LIMIT m, n (use LIMIT n OFFSET m)")]
    [InlineData("SELECT * FROM c LIMIT 1 + 1, 10", "LIMIT", "LIMIT m, n (use LIMIT n OFFSET m)")]
    [InlineData("SELECT * FROM c LIMIT (1 + 1), 10", "LIMIT", "LIMIT m, n (use LIMIT n OFFSET m)")]
    [InlineData("SELECT * FROM c LIMIT @n, 10", "LIMIT", "LIMIT m, n (use LIMIT n OFFSET m)")]
    [InlineData("SELECT * FROM c LIMIT [1, 2], 10", "LIMIT", "LIMIT m, n (use LIMIT n OFFSET m)")]
    [InlineData("SELECT * FROM c ORDER BY a FETCH FIRST 5 ROWS ONLY", "FETCH", "FETCH")]
    [InlineData("UPDATE c SET a = 1 LIMIT 2", "LIMIT", "UPDATE LIMIT")]
    [InlineData("UPDATE c SET a = 1 OFFSET 2", "OFFSET", "UPDATE OFFSET")]
    [InlineData("DELETE FROM c OFFSET 2", "OFFSET", "DELETE OFFSET")]
    [InlineData("INSERT INTO c (KEY, VALUE) VALUES ('a', 1) LIMIT 2", "LIMIT", "INSERT VALUES LIMIT")]
    [InlineData("INSERT INTO c (KEY, VALUE) VALUES ('a', 1) OFFSET 2", "OFFSET", "INSERT VALUES OFFSET")]
    [InlineData("UPSERT INTO c (KEY, VALUE) VALUES ('a', 1) LIMIT 2", "LIMIT", "UPSERT VALUES LIMIT")]
    [InlineData("UPSERT INTO c (KEY, VALUE) VALUES ('a', 1) OFFSET 2", "OFFSET", "UPSERT VALUES OFFSET")]
    [InlineData("EXPLAIN INSERT INTO c (KEY, VALUE) VALUES ('a', 1) LIMIT 2", "LIMIT", "INSERT VALUES LIMIT")]
    [InlineData("EXPLAIN INSERT INTO c (KEY, VALUE) VALUES ('a', 1) OFFSET 2", "OFFSET", "INSERT VALUES OFFSET")]
    [InlineData("EXPLAIN UPSERT INTO c (KEY, VALUE) VALUES ('a', 1) LIMIT 2", "LIMIT", "UPSERT VALUES LIMIT")]
    [InlineData("EXPLAIN UPSERT INTO c (KEY, VALUE) VALUES ('a', 1) OFFSET 2", "OFFSET", "UPSERT VALUES OFFSET")]
    public void Parse_DeferredPaging_ShouldReportOneCapabilityDiagnostic(string text, string word, string construct)
    {
        // Act
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message.ShouldBe($"The {construct} clause is not supported by the OQL surface of this database model.");
        diagnostic.Start.ShouldBe(text.IndexOf(word, StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + word.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Paging: unsupported parameter forms use the shared diagnostic")]
    [InlineData("?")]
    [InlineData(":name")]
    [InlineData("$1a")]
    public void Parse_InvalidParameterForm_ShouldRetainSharedDiagnostic(string operand)
    {
        // Arrange / Act
        string text = "SELECT * FROM c LIMIT " + operand;
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("OQL0002");
        diagnostic.Start.ShouldBe(text.Length - operand.Length);
        diagnostic.End.ShouldBe(text.Length);
    }
}
