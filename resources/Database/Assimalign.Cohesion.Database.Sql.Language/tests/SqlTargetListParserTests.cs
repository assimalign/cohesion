using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlTargetListParserTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Target-list abandonment skips hostile tails without diagnostic amplification (#1792)")]
    public void Parse_OverLimitWithInvalidTail_ShouldSkipPreflightAndDiscardTree()
    {
        // Arrange
        string sql = "SELECT " + string.Join(",", Enumerable.Repeat("1", 1664)) + ",1," + new string('?', 16384) + ";";
        // Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05097");
        statement.SqlExpression.ShouldBeOfType<SqlQueryExpression>();
        statement.SqlExpression.CommandType.ShouldBe(SqlQueryCommandType.Select);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Target-list corpus: every query and RETURNING path abandons entry 1665 (#1792)")]
    [InlineData("SELECT {0};")]
    [InlineData("CREATE VIEW v AS SELECT {0};")]
    [InlineData("CREATE TABLE t AS SELECT {0};")]
    [InlineData("INSERT INTO t SELECT {0};")]
    [InlineData("WITH c AS (SELECT {0}) SELECT * FROM c;")]
    [InlineData("SELECT * FROM (SELECT {0}) d;")]
    [InlineData("SELECT 1 UNION ALL SELECT {0};")]
    [InlineData("SELECT (SELECT {0});")]
    [InlineData("EXPLAIN SELECT {0};")]
    [InlineData("INSERT INTO t VALUES(1) RETURNING {0};")]
    [InlineData("UPDATE t SET a=1 RETURNING {0};")]
    [InlineData("DELETE FROM t RETURNING {0};")]
    [InlineData("MERGE INTO t USING s ON t.a=s.a WHEN MATCHED THEN DELETE RETURNING {0};")]
    public void Parse_TargetListBoundary_ShouldKeepOnlyLocatedRefusal(string template)
    {
        // Arrange: reuse the parser to pin reset after an abandoned nested statement too.
        var parser = new SqlQueryParser();
        string accepted = string.Format(template, string.Join(",", Enumerable.Repeat("1", 1664)));
        string refused = string.Format(template, string.Join(",", Enumerable.Repeat("1", 1665)));
        // Act
        var boundary = parser.Parse(accepted);
        var failure = parser.Parse(refused);
        var usable = parser.Parse("SELECT 1;");
        // Assert
        boundary.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        failure.Diagnostics.Count().ShouldBe(1);
        var diagnostic = failure.Diagnostics.Single();
        diagnostic.Code.ShouldBe("CS05097");
        diagnostic.Start.ShouldBe(template.IndexOf("{0}", System.StringComparison.Ordinal) + 1664 * 2);
        diagnostic.End.ShouldBe(diagnostic.Start + 1);
        diagnostic.Message.ShouldNotBeNull().ShouldContain("1665");
        parser.Parse(refused.TrimEnd(';')).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05097");
        usable.Diagnostics.ShouldBeEmpty();
    }
}
