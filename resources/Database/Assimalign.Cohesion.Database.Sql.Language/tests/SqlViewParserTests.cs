using System.Linq;

using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlViewParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - CREATE modifiers: excluded constructs retain their own names")]
    [InlineData("CREATE TEMP TABLE t (id INT)", "CREATE TEMP TABLE")]
    [InlineData("CREATE TEMPORARY TABLE t (id INT)", "CREATE TEMPORARY TABLE")]
    [InlineData("CREATE OR REPLACE FUNCTION f(x INT) RETURNS INT RETURN x", "CREATE OR REPLACE FUNCTION")]
    public void Parse_ExcludedCreateConstruct_ShouldNameActualObject(string sql, string construct)
    {
        // Arrange
        var parser = new SqlQueryParser();
        // Act
        var statement = (SqlQueryStatement)parser.Parse(sql);
        // Assert
        var error = statement.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        error.Code.ShouldBe("CK00001");
        error.Message!.ShouldContain(construct, Case.Sensitive);
        error.Message!.ShouldNotContain("VIEW", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - View grammar: typed complete statements")]
    [InlineData("CREATE VIEW v AS SELECT a FROM t")]
    [InlineData("CREATE VIEW dbo.v (a, b) AS SELECT a, b FROM t")]
    [InlineData("CREATE OR REPLACE VIEW \"Sales\".\"My View\" AS SELECT a FROM t")]
    [InlineData("CREATE VIEW v AS WITH x AS (SELECT a FROM t) SELECT a FROM x")]
    [InlineData("CREATE VIEW v AS SELECT a FROM t UNION ALL SELECT a FROM u")]
    public void Parse_CreateView_ShouldReturnTypedCompleteStatement(string sql)
    {
        // Arrange
        var parser = new SqlQueryParser();
        // Act
        var statement = (SqlQueryStatement)parser.Parse(sql);
        // Assert
        statement.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlCreateViewExpression>();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - View grammar: contextual exclusions are capability diagnostics")]
    [InlineData("CREATE MATERIALIZED VIEW v AS SELECT a FROM t", "MATERIALIZED")]
    [InlineData("CREATE TEMP VIEW v AS SELECT a FROM t", "TEMP")]
    [InlineData("CREATE TEMPORARY VIEW v AS SELECT a FROM t", "TEMPORARY")]
    [InlineData("CREATE RECURSIVE VIEW v AS SELECT a FROM t", "RECURSIVE")]
    [InlineData("CREATE VIEW IF NOT EXISTS v AS SELECT a FROM t", "IF NOT EXISTS")]
    [InlineData("CREATE VIEW v WITH (security_barrier = TRUE) AS SELECT a FROM t", "options")]
    [InlineData("CREATE VIEW v AS SELECT a FROM t WITH CHECK OPTION", "CHECK OPTION")]
    [InlineData("CREATE VIEW v AS SELECT a FROM t WITH LOCAL CHECK OPTION", "CHECK OPTION")]
    [InlineData("CREATE VIEW v AS SELECT a FROM t WITH CASCADED CHECK OPTION", "CHECK OPTION")]
    [InlineData("CREATE VIEW v AS SELECT a FROM t WINDOW w AS (ROWS CURRENT ROW)", "ROWS")]
    public void Parse_ExcludedViewForm_ShouldRejectAtDeclaration(string sql, string form)
    {
        // Arrange
        var parser = new SqlQueryParser();
        // Act
        var statement = (SqlQueryStatement)parser.Parse(sql);
        // Assert
        var error = statement.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        error.Code.ShouldBe("CK00001");
        error.Message!.ShouldContain(form);
    }
}
