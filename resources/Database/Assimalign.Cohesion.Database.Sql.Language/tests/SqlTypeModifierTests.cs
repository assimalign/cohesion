using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlTypeModifierTests
{
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
}
