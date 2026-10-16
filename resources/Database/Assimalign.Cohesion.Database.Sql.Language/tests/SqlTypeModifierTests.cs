using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlTypeModifierTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Interval qualifier: invalid ranges remain syntax failures")]
    [InlineData("INTERVAL YEAR TO DAY")]
    [InlineData("INTERVAL MONTH TO YEAR")]
    [InlineData("INTERVAL SECOND TO MINUTE")]
    [InlineData("INTERVAL HOUR TO DAY")]
    [InlineData("INTERVAL DAY TO YEAR(3)")]
    [InlineData("INTERVAL DAY TO")]
    [InlineData("INTERVAL \"SECOND\"")]
    public void Parse_InvalidIntervalRange_ShouldReportSyntax(string type)
    {
        // Arrange
        var parser = new SqlQueryParser();
        foreach (var (sql, _) in SqlTypeModifierCases.Statements(type))
        {
            // Act
            var errors = parser.Parse(sql).Diagnostics.Where(error => error.Severity == DiagnosticSeverity.Error).ToArray();

            // Assert
            errors.ShouldNotBeEmpty();
            errors.ShouldAllBe(error => error.Code == "SQL0003");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Interval qualifier: case and lexical trivia preserve the diagnostic subject")]
    public void Parse_IntervalQualifierWithTrivia_ShouldNameUnsupportedModifier()
    {
        // Arrange
        var parser = new SqlQueryParser();
        foreach (var (sql, subject) in SqlTypeModifierCases.Statements("interval /* field */ day\n to second (3)"))
        {
            // Act
            var errors = parser.Parse(sql).Diagnostics.Where(error => error.Severity == DiagnosticSeverity.Error).ToArray();

            // Assert
            var error = errors.ShouldHaveSingleItem(sql);
            error.Code.ShouldBe("COHDBL001");
            error.Message.ShouldNotBeNull().ShouldContain(subject, Case.Sensitive);
            error.Message.ShouldContain("interval day to second(3)", Case.Sensitive);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - FLOAT precision: invalid boundaries name the object and PostgreSQL bit limit")]
    [InlineData("FLOAT(0)", "at least 1 bit")]
    [InlineData("FLOAT(54)", "less than 54 bits")]
    [InlineData("FLOAT(2147483647)", "less than 54 bits")]
    public void Parse_InvalidFloatPrecision_ShouldReportBitRange(string type, string reason)
    {
        // Arrange
        var parser = new SqlQueryParser();
        foreach (var (sql, subject) in SqlTypeModifierCases.Statements(type).Append(($"DROP FUNCTION parameter_function({type})", "parameter type")))
        {
            // Act
            var errors = parser.Parse(sql).Diagnostics.Where(error => error.Severity == DiagnosticSeverity.Error).ToArray();

            // Assert
            var error = errors.ShouldHaveSingleItem();
            error.Code.ShouldBe("SQL0005");
            error.Message.ShouldNotBeNull().ShouldContain(subject, Case.Sensitive);
            error.Message.ShouldContain(reason, Case.Sensitive);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Temporal modifiers: every declaration names the unsupported modifier")]
    [MemberData(nameof(SqlTypeModifierCases.UnsupportedTemporalStatements), MemberType = typeof(SqlTypeModifierCases))]
    public void Parse_TemporalModifiers_ShouldNameUnsupportedModifier(string type, string sql, string subject)
    {
        // Arrange
        var parser = new SqlQueryParser();

        // Act
        var errors = parser.Parse(sql).Diagnostics.Where(error => error.Severity == DiagnosticSeverity.Error).ToArray();

        // Assert
        var error = errors.ShouldHaveSingleItem(sql);
        error.Code.ShouldBe("COHDBL001", sql);
        error.Message.ShouldNotBeNull().ShouldContain(subject, Case.Sensitive);
        error.Message.ShouldContain(type, Case.Sensitive);
        error.Message.ShouldContain("modifier", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - FLOAT precision: resolution selects PostgreSQL storage without retained modifiers")]
    [MemberData(nameof(SqlTypeModifierCases.Floating), MemberType = typeof(SqlTypeModifierCases))]
    public void Parse_FloatPrecision_ShouldResolveStorage(string type, DatabaseType expected)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse($"SELECT CAST(1 AS {type}) FROM items");

        // Assert
        statement.Diagnostics.Where(error => error.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        var select = statement.Expression.ShouldBeOfType<SqlSelectExpression>();
        var cast = select.Columns[0].Expression.ShouldBeOfType<SqlCastExpression>();
        cast.TargetTypeInfo.ShouldNotBeNull().Type.ShouldBe(expected);
        cast.TargetTypeInfo.MaxLength.ShouldBeNull();
        cast.TargetTypeInfo.Precision.ShouldBeNull();
        cast.TargetTypeInfo.Scale.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Type modifiers: all declaration sites agree with CAST")]
    [MemberData(nameof(SqlTypeModifierCases.Types), MemberType = typeof(SqlTypeModifierCases))]
    public void Parse_ModifierBoundary_ShouldAgreeAtEverySite(string type, bool valid)
    {
        // Arrange
        var parser = new SqlQueryParser();
        foreach (var (sql, subject) in SqlTypeModifierCases.Statements(type))
        {
            // Act
            var errors = parser.Parse(sql).Diagnostics.Where(error => error.Severity == DiagnosticSeverity.Error).ToArray();

            // Assert
            if (valid)
            {
                errors.ShouldBeEmpty(sql);
            }
            else
            {
                var error = errors.ShouldHaveSingleItem(sql);
                error.Code.ShouldBe("SQL0005", sql);
                error.Message.ShouldNotBeNull().ShouldContain(subject, Case.Sensitive);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Type resolution: raw modifier fields cannot bypass validation")]
    [InlineData("NUMERIC", null, 29, 0)]
    [InlineData("NUMERIC", null, null, 1)]
    [InlineData("NUMERIC", 1, 1, 0)]
    [InlineData("NUMERIC", null, 1, -1)]
    [InlineData("VARCHAR", -1, null, null)]
    [InlineData("VARCHAR", null, 1, null)]
    [InlineData("INT", null, null, 0)]
    [InlineData("FLOAT", null, 0, null)]
    [InlineData("FLOAT", null, 54, null)]
    [InlineData("FLOAT", 24, 24, null)]
    [InlineData("FLOAT", null, 24, 0)]
    public void TryResolve_InvalidFields_ShouldRefuse(string name, int? length, int? precision, int? scale)
    {
        // Arrange / Act
        bool resolved = SqlTypeNames.TryResolve(name, length, precision, scale, out var info);

        // Assert
        resolved.ShouldBeFalse();
        info.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Type resolution: decimal precision defaults scale to zero without a length")]
    public void TryResolve_DecimalPrecision_ShouldNormalize()
    {
        // Arrange / Act
        SqlTypeNames.TryResolve("NUMERIC", 28, null, null, out var info).ShouldBeTrue();

        // Assert
        info.Type.ShouldBe(DatabaseType.Decimal);
        info.Precision.ShouldBe(28);
        info.Scale.ShouldBe(0);
        info.MaxLength.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - FLOAT raw resolution: length and precision entry points agree")]
    [InlineData(1, DatabaseType.Float32)]
    [InlineData(24, DatabaseType.Float32)]
    [InlineData(25, DatabaseType.Float64)]
    [InlineData(53, DatabaseType.Float64)]
    public void TryResolve_FloatPrecision_ShouldAgreeForBothArgumentSlots(int precision, DatabaseType expected)
    {
        // Arrange / Act
        SqlTypeNames.TryResolve("float", precision, null, null, out var firstArgument).ShouldBeTrue();
        SqlTypeNames.TryResolve("FLOAT", null, precision, null, out var precisionArgument).ShouldBeTrue();

        // Assert
        firstArgument.Type.ShouldBe(expected);
        precisionArgument.Type.ShouldBe(expected);
        firstArgument.Precision.ShouldBeNull();
        precisionArgument.Precision.ShouldBeNull();
    }
}
