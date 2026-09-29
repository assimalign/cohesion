using Assimalign.Cohesion.Database.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>Verifies collation syntax, precedence, and explicit scope diagnostics.</summary>
public sealed class SqlCollationParserTests
{
    /// <summary>Column declarations preserve the override without conflating inheritance with Binary.</summary>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - COLLATE: column names and constraints parse")]
    [InlineData("binary")]
    [InlineData("case_insensitive")]
    [InlineData("case_accent_insensitive")]
    [InlineData("invariant")]
    public void Parse_ColumnCollation_ShouldPreserveNameAndConstraints(string name)
    {
        // Arrange / Act
        var statement = Parse($"CREATE TABLE t (name TEXT COLLATE {name} NOT NULL UNIQUE, other TEXT);");

        // Assert
        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        var create = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>();
        create.Columns[0].CollationName.ShouldBe(name);
        create.Columns[0].IsNullable.ShouldBeFalse();
        create.Columns[0].Constraints.ShouldHaveSingleItem().Kind.ShouldBe(SqlConstraintKind.Unique);
        create.Columns[1].CollationName.ShouldBeNull();
    }

    /// <summary>COLLATE binds to its operand before comparison and records nested overrides.</summary>
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - COLLATE: expression precedence preserves operand overrides")]
    public void Parse_NestedExpressionCollation_ShouldRetainInnermostAndOuterNames()
    {
        // Arrange / Act
        var statement = Parse("SELECT name FROM t WHERE name = ('Alice' COLLATE binary) COLLATE case_insensitive;");

        // Assert
        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        var comparison = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Where.ShouldBeOfType<SqlBinaryExpression>();
        comparison.Left.ShouldBeOfType<SqlColumnReferenceExpression>();
        var outer = comparison.Right.ShouldBeOfType<SqlCollateExpression>();
        outer.CollationName.ShouldBe("case_insensitive");
        var inner = outer.Operand.ShouldBeOfType<SqlCollateExpression>();
        inner.CollationName.ShouldBe("binary");
        inner.Operand.ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe("Alice");
    }

    /// <summary>The syntax is available at every executable expression comparison site.</summary>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - COLLATE: expressions parse across comparison sites")]
    [InlineData("SELECT DISTINCT name COLLATE binary FROM t;")]
    [InlineData("SELECT name FROM t WHERE name LIKE 'A%' COLLATE case_insensitive;")]
    [InlineData("SELECT name COLLATE case_insensitive FROM t GROUP BY name COLLATE case_insensitive;")]
    [InlineData("SELECT name FROM t ORDER BY name COLLATE case_accent_insensitive;")]
    [InlineData("ALTER TABLE t ADD COLUMN name TEXT COLLATE case_insensitive;")]
    [InlineData("SELECT 'Alice' COLLATE CASE_INSENSITIVE = 'alice';")]
    public void Parse_CollatedExpressionSite_ShouldSucceed(string sql)
    {
        Parse(sql).Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>Out-of-scope collation requests fail with the model's explicit surface diagnostic.</summary>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - COLLATE: deferred collation features reject clearly")]
    [InlineData("CREATE TABLE t (name TEXT COLLATE turkish_ci);")]
    [InlineData("SELECT name COLLATE swedish FROM t;")]
    [InlineData("CREATE COLLATION custom;")]
    [InlineData("SET COLLATION case_insensitive;")]
    [InlineData("SET SESSION COLLATION case_insensitive;")]
    [InlineData("CREATE FULLTEXT INDEX text_idx ON t (name COLLATE case_insensitive);")]
    public void Parse_DeferredCollationFeature_ShouldReportModelDiagnostic(string sql)
    {
        Parse(sql).Diagnostics.ShouldContain(item => item.Code == "COHDBL001" && item.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>Incomplete or duplicate collation clauses do not silently fall back.</summary>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - COLLATE: malformed syntax is rejected")]
    [InlineData("SELECT name COLLATE FROM t;")]
    [InlineData("SELECT name COLLATE;")]
    [InlineData("CREATE TABLE t (name TEXT COLLATE);")]
    [InlineData("CREATE TABLE t (name TEXT COLLATE binary COLLATE case_insensitive);")]
    public void Parse_MalformedCollation_ShouldReportError(string sql)
    {
        Parse(sql).Diagnostics.ShouldContain(item => item.Severity == DiagnosticSeverity.Error);
    }

    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);
}
