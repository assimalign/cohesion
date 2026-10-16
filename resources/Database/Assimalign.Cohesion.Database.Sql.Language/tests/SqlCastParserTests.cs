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
    [InlineData("CHAR", DatabaseType.Character)]
    [InlineData("CHARACTER", DatabaseType.Character)]
    [InlineData("VARCHAR", DatabaseType.String)]
    [InlineData("TEXT", DatabaseType.String)]
    [InlineData("integer", DatabaseType.Int32)]
    public void Parse_CastTarget_ResolvesSharedType(string target, DatabaseType expected)
    {
        var statement = Parse($"SELECT CAST('42' AS {target});");

        var cast = GetCast(statement);

        cast.TargetTypeInfo.ShouldNotBeNull().Type.ShouldBe(expected);
        cast.TargetTypeInfo.MaxLength.ShouldBe(expected == DatabaseType.Character ? 1 : (int?)null);
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
    [InlineData("CHAR(1)", 1, DatabaseType.Character)]
    [InlineData("CHARACTER(5)", 5, DatabaseType.Character)]
    [InlineData("VARCHAR(100)", 100, DatabaseType.String)]
    [InlineData("TEXT(10485760)", 10485760, DatabaseType.String)]
    public void Parse_StringTarget_RetainsLength(string target, int length, DatabaseType expected)
    {
        var statement = Parse($"SELECT CAST(42 AS {target});");

        var type = GetCast(statement).TargetTypeInfo.ShouldNotBeNull();

        type.Type.ShouldBe(expected);
        type.MaxLength.ShouldBe(length);
        type.Precision.ShouldBeNull();
        type.Scale.ShouldBeNull();
        statement.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: Leaves a name that is not a built-in type to the engine's type catalog (E3)")]
    [InlineData("NOTATYPE")]
    [InlineData("email")]
    public void Parse_NameThatIsNotBuiltIn_LeavesItToTheEngine(string target)
    {
        string sql = $"SELECT CAST(x AS {target});";
        var statement = Parse(sql);

        // The parser does not know the engine's domains: no SQL0004, and the name as written.
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var cast = GetCast(statement);
        cast.TargetTypeInfo.ShouldBeNull();
        cast.TargetType.ShouldBe(target);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: Rejects malformed arguments of a name that is not a built-in type")]
    [InlineData("NOTATYPE(-1)")]
    [InlineData("email(1.5)")]
    [InlineData("email(1, 2, 3)")]
    public void Parse_NameThatIsNotBuiltInWithMalformedArguments_ReportsDiagnostic(string target)
    {
        var statement = Parse($"SELECT CAST(x AS {target});");

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "SQL0005");

        diagnostic.Message.ShouldNotBeNull().ShouldContain("unsigned integer literals", Case.Sensitive);
        GetCast(statement).TargetTypeInfo.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: Resolves every built-in type as a target; the engine's cast registry decides the conversion (E3)")]
    [InlineData("REAL", DatabaseType.Float32)]
    [InlineData("DOUBLE", DatabaseType.Float64)]
    [InlineData("BINARY", DatabaseType.Binary)]
    [InlineData("DATE", DatabaseType.Date)]
    [InlineData("TIME", DatabaseType.Time)]
    [InlineData("TIMESTAMP", DatabaseType.DateTime)]
    [InlineData("TIMESTAMPTZ", DatabaseType.DateTimeOffset)]
    [InlineData("INTERVAL", DatabaseType.TimeSpan)]
    [InlineData("UUID", DatabaseType.Guid)]
    [InlineData("JSON", DatabaseType.Json)]
    [InlineData("JSONB", DatabaseType.JsonBinary)]
    public void Parse_EveryBuiltInTarget_Resolves(string target, DatabaseType expected)
    {
        var statement = Parse($"SELECT CAST(NULL AS {target});");

        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        GetCast(statement).TargetTypeInfo.ShouldNotBeNull().Type.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: A built-in type without arguments refuses them, and a binary length must be positive")]
    [InlineData("DATE(3)", "does not accept type arguments")]
    [InlineData("UUID(16)", "does not accept type arguments")]
    [InlineData("DOUBLE(8)", "does not accept type arguments")]
    [InlineData("BINARY(0)", "one positive length argument")]
    public void Parse_BuiltInTargetWithInvalidArguments_ReportsDiagnostic(string target, string message)
    {
        var statement = Parse($"SELECT CAST(NULL AS {target});");

        statement.Diagnostics.Single(item => item.Code == "SQL0005").Message.ShouldNotBeNull().ShouldContain(message, Case.Sensitive);
        GetCast(statement).TargetTypeInfo.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: Rejects invalid type arguments")]
    [InlineData("NOTATYPE(4)", "does not accept type arguments")]
    [InlineData("TEXT(2147483647)", "one positive length argument")]
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
