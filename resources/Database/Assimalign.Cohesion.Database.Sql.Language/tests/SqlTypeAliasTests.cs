using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlTypeAliasTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Type aliases: every declaration and CAST resolves the same built-in")]
    [InlineData("DOUBLE PRECISION", DatabaseType.Float64, null)]
    [InlineData("double /*x*/ precision", DatabaseType.Float64, null)]
    [InlineData("CHARACTER VARYING(20)", DatabaseType.String, 20)]
    [InlineData("char\n varying /*n*/ (20)", DatabaseType.String, 20)]
    [InlineData("NATIONAL CHARACTER VARYING(20)", DatabaseType.String, 20)]
    [InlineData("BINARY VARYING(20)", DatabaseType.Binary, 20)]
    [InlineData("TIME WITHOUT TIME ZONE", DatabaseType.Time, null)]
    [InlineData("TIMESTAMP WITHOUT TIME ZONE", DatabaseType.DateTime, null)]
    [InlineData("timestamp /*x*/ with\n time zone", DatabaseType.DateTimeOffset, null)]
    [InlineData("NVARCHAR", DatabaseType.String, null)]
    [InlineData("NVARCHAR(20)", DatabaseType.String, 20)]
    [InlineData("NVARCHAR(MAX)", DatabaseType.String, null)]
    [InlineData("varchar ( /*x*/ max )", DatabaseType.String, null)]
    [InlineData("VARBINARY(MAX)", DatabaseType.Binary, null)]
    [InlineData("NCHAR", DatabaseType.Character, 1)]
    [InlineData("NCHAR(10)", DatabaseType.Character, 10)]
    [InlineData("NATIONAL CHARACTER", DatabaseType.Character, 1)]
    [InlineData("NATIONAL CHARACTER(10)", DatabaseType.Character, 10)]
    [InlineData("DATETIME", DatabaseType.DateTime, null)]
    [InlineData("DATETIME2", DatabaseType.DateTime, null)]
    [InlineData("DATETIMEOFFSET", DatabaseType.DateTimeOffset, null)]
    [InlineData("UNIQUEIDENTIFIER", DatabaseType.Guid, null)]
    [InlineData("FLOAT(24)", DatabaseType.Float32, null)]
    [InlineData("FLOAT(25)", DatabaseType.Float64, null)]
    public void Parse_AliasAtEverySite_ShouldResolve(string type, DatabaseType expected, int? length)
    {
        // Arrange
        var parser = new SqlQueryParser();
        foreach (var (sql, _) in SqlTypeModifierCases.Statements(type))
        {
            // Act
            var statement = parser.Parse(sql + ";");

            // Assert
            statement.Diagnostics.ShouldBeEmpty(sql);
            DatabaseTypeInfo? info = statement.Expression switch
            {
                SqlCreateTableExpression create => create.Columns[0].ResolvedType,
                SqlAlterTableExpression { Action: SqlAlterAddColumnAction add } => add.Column.ResolvedType,
                SqlAlterTableExpression { Action: SqlAlterColumnAction alter } => alter.Column.ResolvedType,
                SqlSelectExpression select => select.Columns[0].Expression.ShouldBeOfType<SqlCastExpression>().TargetTypeInfo,
                _ => null,
            };
            if (statement.Expression is SqlCreateFunctionExpression or SqlCreateDomainExpression) { continue; }
            info.ShouldNotBeNull(sql);
            info.Type.ShouldBe(expected, sql);
            info.MaxLength.ShouldBe(length, sql);
            info.Precision.ShouldBeNull();
            info.Scale.ShouldBeNull();
            if (statement.Expression is SqlSelectExpression selected)
            {
                string rendered = SqlExpressionRenderer.Render(selected);
                var reparsed = parser.Parse(rendered + ";");
                reparsed.Diagnostics.ShouldBeEmpty(rendered);
                var target = reparsed.Expression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression.ShouldBeOfType<SqlCastExpression>().TargetTypeInfo.ShouldNotBeNull();
                target.Type.ShouldBe(expected);
                target.MaxLength.ShouldBe(length);
                SqlExpressionRenderer.Render(reparsed.Expression.ShouldBeOfType<SqlSelectExpression>()).ShouldBe(rendered);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Type aliases: rejected spellings name a reason and replacement")]
    [InlineData("TIME WITH TIME ZONE", "SQL0005", "no shared storage type", "TIME WITHOUT TIME ZONE")]
    [InlineData("BIT", "SQL0005", "bit strings", "BOOLEAN")]
    [InlineData("BIT(3)", "SQL0005", "bit strings", "BOOLEAN")]
    [InlineData("SMALLDATETIME", "SQL0005", "minute precision", "TIMESTAMP")]
    [InlineData("INT[]", "COHDBL001", "Array types", "scalar")]
    [InlineData("INTEGER ARRAY", "COHDBL001", "Array types", "scalar")]
    [InlineData("DATETIME2(7)", "COHDBL001", "Temporal precision", "modifier")]
    [InlineData("DATETIMEOFFSET(7)", "COHDBL001", "Temporal precision", "modifier")]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", "COHDBL001", "Temporal precision", "modifier")]
    [InlineData("TIME(7) WITHOUT TIME ZONE", "COHDBL001", "Temporal precision", "modifier")]
    [InlineData("UNIQUEIDENTIFIER(16)", "SQL0005", "does not accept", "arguments")]
    [InlineData("NCHAR(MAX)", "SQL0005", "unsigned integer", "arguments")]
    [InlineData("NVARCHAR(MAX,1)", "SQL0005", "unsigned integer", "arguments")]
    public void Parse_RejectedAlias_ShouldExplainAtEverySite(string type, string code, string reason, string replacement)
    {
        // Arrange
        var parser = new SqlQueryParser();
        foreach (var (sql, _) in SqlTypeModifierCases.Statements(type))
        {
            // Act
            var errors = parser.Parse(sql + ";").Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();

            // Assert
            var error = errors.ShouldHaveSingleItem(sql);
            error.Code.ShouldBe(code, sql);
            error.Message.ShouldNotBeNull().ShouldContain(reason, Case.Sensitive);
            error.Message.ShouldContain(replacement, Case.Sensitive);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Domain names: unknown names remain engine-resolved at every site")]
    [InlineData("mood")]
    [InlineData("NOTATYPE")]
    public void Parse_DomainName_ShouldLeaveResolutionToEngine(string type)
    {
        // Arrange
        var parser = new SqlQueryParser();
        foreach (var (sql, _) in SqlTypeModifierCases.Statements(type))
        {
            // Act / Assert
            parser.Parse(sql + ";").Diagnostics.ShouldBeEmpty();
        }
        parser.Parse($"CREATE TABLE t (c {type});").Expression.ShouldBeOfType<SqlCreateTableExpression>()
            .Columns[0].ResolvedType.ShouldBeNull();
    }
}
