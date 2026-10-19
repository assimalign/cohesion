using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlSchemaParsingTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - CREATE SCHEMA retains contextual names and existence guards")]
    [InlineData("CREATE SCHEMA sales", "sales", false)]
    [InlineData("CREATE SCHEMA IF NOT EXISTS sales;", "sales", true)]
    [InlineData("create /* namespace */ schema \"a.b\"", "a.b", false)]
    [InlineData("CREATE SCHEMA schema", "schema", false)]
    [InlineData("CREATE SCHEMA \"AUTHORIZATION\"", "AUTHORIZATION", false)]
    public void Parse_CreateSchema_ShouldRetainNamespace(string sql, string name, bool guarded)
    {
        // Arrange / Act
        var result = Parse(sql);

        // Assert
        result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var schema = result.SqlExpression.ShouldBeOfType<SqlCreateSchemaExpression>();
        schema.Name.ShouldBe(name);
        schema.IfNotExists.ShouldBe(guarded);
        schema.CommandType.ShouldBe(SqlQueryCommandType.Create);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DROP SCHEMA defaults to RESTRICT and retains CASCADE")]
    [InlineData("DROP SCHEMA sales", false, false)]
    [InlineData("DROP SCHEMA sales RESTRICT", false, false)]
    [InlineData("DROP SCHEMA IF EXISTS sales CASCADE;", true, true)]
    public void Parse_DropSchema_ShouldRetainBehavior(string sql, bool guarded, bool cascade)
    {
        // Arrange / Act
        var result = Parse(sql);

        // Assert
        result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var schema = result.SqlExpression.ShouldBeOfType<SqlDropSchemaExpression>();
        schema.Name.ShouldBe("sales");
        schema.IfExists.ShouldBe(guarded);
        schema.Cascade.ShouldBe(cascade);
        schema.CommandType.ShouldBe(SqlQueryCommandType.Drop);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Schema authorization and nested elements are precisely unsupported")]
    [InlineData("CREATE SCHEMA sales AUTHORIZATION owner", "AUTHORIZATION")]
    [InlineData("CREATE SCHEMA AUTHORIZATION owner", "AUTHORIZATION")]
    [InlineData("CREATE SCHEMA IF NOT EXISTS sales AUTHORIZATION owner", "AUTHORIZATION")]
    [InlineData("CREATE SCHEMA sales CREATE TABLE t(id INT)", "elements")]
    [InlineData("CREATE SCHEMA sales CREATE VIEW v AS SELECT * FROM t", "elements")]
    [InlineData("CREATE SCHEMA IF NOT EXISTS sales CREATE TABLE t(id INT)", "elements")]
    [InlineData("CREATE SCHEMA sales GRANT ALL ON t TO owner", "elements")]
    public void Parse_ExcludedSchemaForm_ShouldNameConstruct(string sql, string construct)
    {
        // Arrange / Act
        var result = Parse(sql);

        // Assert
        var diagnostic = result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Message.ShouldNotBeNull().ShouldContain(construct, Case.Sensitive);
        diagnostic.Start.ShouldNotBeNull();
        diagnostic.Start.GetValueOrDefault().ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Malformed schema DDL never becomes table DDL")]
    [InlineData("CREATE SCHEMA")]
    [InlineData("CREATE SCHEMA (a INT)")]
    [InlineData("CREATE SCHEMA IF EXISTS s")]
    [InlineData("CREATE SCHEMA s.extra")]
    [InlineData("DROP SCHEMA;")]
    [InlineData("DROP SCHEMA IF NOT EXISTS s")]
    [InlineData("DROP SCHEMA s CASCADE RESTRICT")]
    [InlineData("DROP SCHEMA s, other")]
    [InlineData("CREATE SCHEMA s trailing")]
    public void Parse_MalformedSchema_ShouldReportSyntaxError(string sql)
    {
        // Arrange / Act
        var result = Parse(sql);

        // Assert
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "SQL0003");
        result.SqlExpression.ShouldNotBeOfType<SqlCreateTableExpression>();
        result.SqlExpression.ShouldNotBeOfType<SqlDropTableExpression>();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Schema and authorization remain ordinary unquoted identifiers")]
    public void Parse_ContextualWords_ShouldPreserveIdentifierVocabulary()
    {
        // Arrange / Act
        var result = Parse("CREATE TABLE schema(authorization INT)");

        // Assert
        result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        result.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>().Table.TableName.ShouldBe("schema");
        SqlLanguageProfile.Instance.Keywords.ToArray().ShouldNotContain(word => word.Equals("SCHEMA", System.StringComparison.OrdinalIgnoreCase));
        SqlLanguageProfile.Instance.Keywords.ToArray().ShouldNotContain(word => word.Equals("AUTHORIZATION", System.StringComparison.OrdinalIgnoreCase));
    }

    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);
}
