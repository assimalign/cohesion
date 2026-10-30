using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlBitwiseParserTests
{
    public static IEnumerable<object[]> BinaryPairs()
    {
        string[] operators = ["&", "|", "#", "<<", ">>", "||"];
        foreach (string first in operators)
        {
            foreach (string second in operators) { yield return [first, second]; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Bitwise: every generic binary pair associates left (#1721)")]
    [MemberData(nameof(BinaryPairs))]
    public void Parse_GenericOperatorPairs_ShouldAssociateLeft(string first, string second)
    {
        // Arrange / Act: PostgreSQL gram.y:904,15368-15371, all generic operators use %left Op.
        var actual = Parse($"a {first} b {second} c");
        var expected = Parse($"(a {first} b) {second} c");
        // Assert
        SqlTreeDump.Dump(actual).ShouldBe(SqlTreeDump.Dump(expected));
        SqlTreeDump.Dump(Parse(SqlExpressionRenderer.Render(actual))).ShouldBe(SqlTreeDump.Dump(actual));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Bitwise: PostgreSQL precedence and rendering round trip (#1721)")]
    [InlineData("~a + b * c", "~(a + (b * c))", "~a + b * c")]
    [InlineData("~a & b", "(~a) & b", "~a & b")]
    [InlineData("~(a & b)", "~(a & b)", "~(a & b)")]
    [InlineData("a & ~b + c", "a & (~(b + c))", "a & (~b + c)")]
    [InlineData("-a::INT << b + c", "(-CAST(a AS INT)) << (b + c)", "-CAST(a AS INT) << b + c")]
    [InlineData("~a::INT", "~CAST(a AS INT)", "~CAST(a AS INT)")]
    [InlineData("~ ~a", "~(~a)", "~(~a)")]
    [InlineData("a << b = c & d", "(a << b) = (c & d)", "a << b = c & d")]
    [InlineData("a + b & c * d", "(a + b) & (c * d)", "a + b & c * d")]
    [InlineData("a IN (1) & b", "(a IN (1)) & b", "(a IN (1)) & b")]
    public void Render_PrecedenceCorpus_ShouldPreserveTree(string text, string grouped, string canonical)
    {
        // Arrange / Act
        var actual = Parse(text);
        string rendered = SqlExpressionRenderer.Render(actual);
        // Assert
        SqlTreeDump.Dump(actual).ShouldBe(SqlTreeDump.Dump(Parse(grouped)));
        rendered.ShouldBe(canonical);
        SqlTreeDump.Dump(Parse(rendered)).ShouldBe(SqlTreeDump.Dump(actual));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Bitwise: incomplete operands and separated shift tokens stay syntax errors (#1721)")]
    [InlineData("1 #")]
    [InlineData("# 1")]
    [InlineData("1 &")]
    [InlineData("~")]
    [InlineData("1 < < 2")]
    [InlineData("1 > /* gap */ > 2")]
    public void Parse_MalformedBitwise_ShouldRefuse(string text)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("SELECT " + text + ";");
        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS00003");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Bitwise: a leading XOR token cannot be skipped during command recovery (#1721)")]
    [InlineData("# SELECT 1;")]
    [InlineData("# DELETE FROM t;")]
    [InlineData("#")]
    public void Parse_LeadingHash_ShouldRejectWithoutReturningExecutableTree(string text)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS00003" && diagnostic.Start == 0);
        statement.SqlExpression.GetType().ShouldBe(typeof(SqlQueryExpression));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Bitwise: contiguous regex spellings stay deferred in prefix positions (#1721)")]
    [InlineData("~~1")]
    [InlineData("~*'x'")]
    [InlineData("~~*'x'")]
    [InlineData("!~'x'")]
    [InlineData("!~*'x'")]
    [InlineData("!~~'x'")]
    [InlineData("!~~*'x'")]
    public void Parse_PrefixRegexSpelling_ShouldReportOneUnsupportedOperator(string text)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("SELECT " + text + ";");
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    private static SqlExpression Parse(string text)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("SELECT " + text + ";");
        statement.Diagnostics.ShouldBeEmpty();
        return statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression;
    }
}
