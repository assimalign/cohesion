using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlJsonParserTests
{
    public static IEnumerable<object[]> OperatorPairs()
    {
        string[] operators = ["->", "->>", "||", "&", "|", "#", "<<", ">>"];
        foreach (string first in operators)
        {
            foreach (string second in operators) { yield return [first, second]; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - JSON: generic operators associate left and render without changing their trees (#1722)")]
    [MemberData(nameof(OperatorPairs))]
    public void Parse_OperatorPairs_ShouldAssociateLeft(string first, string second)
    {
        // Arrange / Act: PostgreSQL gram.y:904 and 15368-15371 (%left Op).
        var actual = Expression($"a {first} b {second} c");
        // Assert
        SqlTreeDump.Dump(actual).ShouldBe(SqlTreeDump.Dump(Expression($"(a {first} b) {second} c")));
        SqlTreeDump.Dump(Expression(SqlExpressionRenderer.Render(actual))).ShouldBe(SqlTreeDump.Dump(actual));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - JSON: PostgreSQL precedence and canonical kinds round trip (#1722)")]
    [InlineData("doc->'a'->>'b'", "(doc -> 'a') ->> 'b'", "doc -> 'a' ->> 'b'")]
    [InlineData("doc -> 1 + 2 * 3", "doc -> (1 + (2 * 3))", "doc -> 1 + 2 * 3")]
    [InlineData("doc ->> 'a' = 'x'", "(doc ->> 'a') = 'x'", "doc ->> 'a' = 'x'")]
    [InlineData("doc IS JSON", "doc IS JSON VALUE", "doc IS JSON VALUE")]
    [InlineData("doc IS NOT JSON SCALAR", "doc IS NOT JSON SCALAR", "doc IS NOT JSON SCALAR")]
    [InlineData("NOT doc IS JSON OBJECT", "NOT (doc IS JSON OBJECT)", "NOT doc IS JSON OBJECT")]
    [InlineData("doc->'a' IS JSON ARRAY", "(doc -> 'a') IS JSON ARRAY", "doc -> 'a' IS JSON ARRAY")]
    [InlineData("(doc->>'a')::JSON IS JSON", "CAST((doc ->> 'a') AS JSON) IS JSON VALUE", "CAST(doc ->> 'a' AS JSON) IS JSON VALUE")]
    [InlineData("doc IS JSON ARRAY AND doc IS NOT NULL", "(doc IS JSON ARRAY) AND (doc IS NOT NULL)", "doc IS JSON ARRAY AND doc IS NOT NULL")]
    public void Render_PrecedenceAndKinds_ShouldPreserveTree(string source, string grouped, string canonical)
    {
        // Arrange / Act
        var expression = Expression(source);
        // Assert
        SqlTreeDump.Dump(expression).ShouldBe(SqlTreeDump.Dump(Expression(grouped)));
        SqlExpressionRenderer.Render(expression).ShouldBe(canonical);
        SqlTreeDump.Dump(Expression(canonical)).ShouldBe(SqlTreeDump.Dump(expression));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - JSON: incomplete and separated arrows are syntax errors (#1722)")]
    [InlineData("doc ->")]
    [InlineData("doc ->>")]
    [InlineData("doc - > 'a'")]
    [InlineData("doc -> > 'a'")]
    [InlineData("doc -> /* gap */ > 'a'")]
    [InlineData("doc ->>> 'a'")]
    public void Parse_MalformedAccess_ShouldRefuse(string source)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("SELECT " + source);
        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private static SqlExpression Expression(string source)
    {
        var statement = new SqlQueryParser().Parse("SELECT " + source + ";").ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.ShouldBeEmpty();
        return statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression;
    }
}
