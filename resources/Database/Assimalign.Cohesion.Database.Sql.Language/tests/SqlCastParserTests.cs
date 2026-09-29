using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Verifies CAST target resolution and the errors that prevent invalid conversion plans.
/// Engine execution tests separately prove conversion rather than operand pass-through.
/// </summary>
public class SqlCastParserTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: Resolves shared target identities")]
    [InlineData("BOOL", DatabaseType.Boolean)]
    [InlineData("BOOLEAN", DatabaseType.Boolean)]
    [InlineData("TINYINT", DatabaseType.Int8)]
    [InlineData("SMALLINT", DatabaseType.Int16)]
    [InlineData("INT2", DatabaseType.Int16)]
    [InlineData("INT", DatabaseType.Int32)]
    [InlineData("INTEGER", DatabaseType.Int32)]
    [InlineData("INT4", DatabaseType.Int32)]
    [InlineData("BIGINT", DatabaseType.Int64)]
    [InlineData("INT8", DatabaseType.Int64)]
    [InlineData("DECIMAL", DatabaseType.Decimal)]
    [InlineData("NUMERIC", DatabaseType.Decimal)]
    [InlineData("CHAR", DatabaseType.String)]
    [InlineData("CHARACTER", DatabaseType.String)]
    [InlineData("VARCHAR", DatabaseType.String)]
    [InlineData("TEXT", DatabaseType.String)]
    [InlineData("integer", DatabaseType.Int32)]
    public void Parse_CastTarget_ResolvesSharedType(string target, DatabaseType expected)
    {
        var statement = Parse($"SELECT CAST('42' AS {target});");

        var cast = GetCast(statement);

        cast.TargetTypeInfo.ShouldNotBeNull().Type.ShouldBe(expected);
        cast.TargetTypeInfo.MaxLength.ShouldBeNull();
        cast.TargetTypeInfo.Precision.ShouldBeNull();
        cast.TargetTypeInfo.Scale.ShouldBeNull();
        statement.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: Retains decimal precision and scale")]
    [InlineData("DECIMAL(5)", 5, 0)]
    [InlineData("DECIMAL(5,2)", 5, 2)]
    [InlineData("numeric(28,28)", 28, 28)]
    [InlineData("NUMERIC(1,0)", 1, 0)]
    public void Parse_DecimalTarget_RetainsPrecisionAndScale(string target, int precision, int scale)
    {
        var statement = Parse($"SELECT CAST('12.34' AS {target});");

        var type = GetCast(statement).TargetTypeInfo.ShouldNotBeNull();

        type.Type.ShouldBe(DatabaseType.Decimal);
        type.Precision.ShouldBe(precision);
        type.Scale.ShouldBe(scale);
        type.MaxLength.ShouldBeNull();
        statement.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: Retains string length")]
    [InlineData("CHAR(1)", 1)]
    [InlineData("CHARACTER(5)", 5)]
    [InlineData("VARCHAR(100)", 100)]
    [InlineData("TEXT(2147483647)", int.MaxValue)]
    public void Parse_StringTarget_RetainsLength(string target, int length)
    {
        var statement = Parse($"SELECT CAST(42 AS {target});");

        var type = GetCast(statement).TargetTypeInfo.ShouldNotBeNull();

        type.Type.ShouldBe(DatabaseType.String);
        type.MaxLength.ShouldBe(length);
        type.Precision.ShouldBeNull();
        type.Scale.ShouldBeNull();
        statement.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: Rejects unknown targets at the target span")]
    [InlineData("NOTATYPE")]
    [InlineData("NOTATYPE(4)")]
    public void Parse_UnknownTarget_ReportsPreciseDiagnostic(string target)
    {
        string sql = $"SELECT CAST(x AS {target});";
        var statement = Parse(sql);

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "SQL0004");

        diagnostic.Message.ShouldBe("Unknown CAST target type 'NOTATYPE'.");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe(target);
        GetCast(statement).TargetTypeInfo.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: Rejects unsupported shared types")]
    [InlineData("REAL")]
    [InlineData("DOUBLE")]
    [InlineData("BINARY")]
    [InlineData("DATE")]
    [InlineData("TIME")]
    [InlineData("TIMESTAMP")]
    [InlineData("TIMESTAMPTZ")]
    [InlineData("INTERVAL")]
    [InlineData("UUID")]
    [InlineData("JSON")]
    [InlineData("JSONB")]
    public void Parse_UnsupportedTarget_ReportsConversionBoundary(string target)
    {
        var statement = Parse($"SELECT CAST(NULL AS {target});");

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "SQL0005");

        diagnostic.Message.ShouldNotBeNull().ShouldContain("not supported for explicit conversion", Case.Sensitive);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        GetCast(statement).TargetTypeInfo.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: Rejects invalid type arguments")]
    [InlineData("INT(4)", "does not accept type arguments")]
    [InlineData("BOOLEAN(1)", "does not accept type arguments")]
    [InlineData("DECIMAL(0)", "precision must be between 1 and 28")]
    [InlineData("DECIMAL(29,0)", "precision must be between 1 and 28")]
    [InlineData("DECIMAL(3,4)", "scale must be between 0 and precision")]
    [InlineData("DECIMAL(4,-1)", "unsigned integer literals")]
    [InlineData("DECIMAL(4,2,1)", "unsigned integer literals")]
    [InlineData("DECIMAL(4,)", "unsigned integer literals")]
    [InlineData("VARCHAR(0)", "one positive length argument")]
    [InlineData("VARCHAR(3,1)", "one positive length argument")]
    [InlineData("VARCHAR(-1)", "unsigned integer literals")]
    [InlineData("VARCHAR(2147483648)", "unsigned integer literals")]
    [InlineData("VARCHAR(1.5)", "unsigned integer literals")]
    [InlineData("VARCHAR(x)", "unsigned integer literals")]
    [InlineData("VARCHAR()", "unsigned integer literals")]
    public void Parse_InvalidTargetArguments_ReportsDiagnostic(string target, string message)
    {
        var statement = Parse($"SELECT CAST('123' AS {target});");

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "SQL0005");

        diagnostic.Message.ShouldNotBeNull().ShouldContain(message, Case.Sensitive);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        GetCast(statement).TargetTypeInfo.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: Requires complete syntax")]
    [InlineData("SELECT CAST '42' AS INT);", "Expected '(' after CAST.")]
    [InlineData("SELECT CAST('42' INT);", "Expected AS before the CAST target type.")]
    [InlineData("SELECT CAST('42' AS );", "Expected a CAST target type name.")]
    [InlineData("SELECT CAST('42' AS INT;", "Expected ')' after the CAST target type.")]
    public void Parse_IncompleteCast_ReportsSyntaxDiagnostic(string sql, string message)
    {
        var statement = Parse(sql);

        statement.Diagnostics.ShouldContain(item => item.Code == "SQL0003" && item.Message == message);
    }

    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);

    private static SqlCastExpression GetCast(SqlQueryStatement statement) =>
        statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression.ShouldBeOfType<SqlCastExpression>();
}
