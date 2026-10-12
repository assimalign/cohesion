using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlDefinitionParserTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - CREATE FUNCTION preserves the single body and PostgreSQL defaults")]
    public void Parse_FunctionDefaults_ShouldBeVolatileAndCalledOnNull()
    {
        // Arrange
        var parser = new SqlQueryParser();
        // Act
        var statement = (SqlQueryStatement)parser.Parse("CREATE FUNCTION f(p NUMERIC(10,2), q TEXT) RETURNS INT RETURN LENGTH(q);");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var definition = statement.SqlExpression.ShouldBeOfType<SqlCreateFunctionExpression>();
        definition.Parameters.Count.ShouldBe(2);
        definition.Parameters[0].DataType.ShouldBe("NUMERIC(10,2)");
        definition.Volatility.ShouldBe("VOLATILE");
        definition.IsStrict.ShouldBeFalse();
        definition.Body.ShouldBeOfType<SqlFunctionCallExpression>();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - SQL function and domain DDL parses in the shared grammar")]
    [InlineData("CREATE FUNCTION f() RETURNS INT STABLE CALLED ON NULL INPUT RETURN 1")]
    [InlineData("CREATE FUNCTION f(p INT) RETURNS INT STRICT IMMUTABLE RETURN p + 1")]
    [InlineData("DROP FUNCTION f(INT, TEXT)")]
    [InlineData("CREATE DOMAIN positive AS INT DEFAULT 1 NOT NULL CONSTRAINT positive_check CHECK (VALUE > 0)")]
    [InlineData("DROP DOMAIN positive")]
    public void Parse_DefinitionDdl_ShouldHaveNoDiagnostics(string sql)
    {
        // Arrange
        var parser = new SqlQueryParser();
        // Act
        var statement = parser.Parse(sql + ";");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Duplicate function clauses and malformed signatures are refused")]
    [InlineData("CREATE FUNCTION f(p) RETURNS INT RETURN 1")]
    [InlineData("CREATE FUNCTION f() RETURNS INT IMMUTABLE VOLATILE RETURN 1")]
    [InlineData("CREATE FUNCTION f() RETURNS INT STRICT CALLED ON NULL INPUT RETURN 1")]
    [InlineData("CREATE DOMAIN d AS INT CONSTRAINT ck")]
    public void Parse_InvalidDefinition_ShouldReportDiagnostics(string sql)
    {
        // Arrange
        var parser = new SqlQueryParser();
        // Act
        var statement = parser.Parse(sql + ";");
        // Assert
        statement.Diagnostics.Any().ShouldBeTrue();
    }
}
