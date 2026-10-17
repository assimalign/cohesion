using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class QuotedFunctionDiagnosticTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Quoted functions: lowercase unsupported names fail during parsing")]
    [InlineData("sqrt")]
    [InlineData("nullif")]
    [InlineData("rank")]
    [InlineData("current_date")]
    public void Parse_QuotedUnsupportedCall_ShouldCarryLocatedDiagnostic(string name)
    {
        // Arrange
        string sql = $"SELECT \"{name}\"(id) FROM t;";

        // Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBL001", diagnostic.Message);
        diagnostic.Start.ShouldBe(7);
        diagnostic.End.ShouldBe(9 + name.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Quoted functions: preserve case and column references")]
    [InlineData("SELECT \"SQRT\"(id) FROM t;")]
    [InlineData("SELECT \"Sqrt\"(id) FROM t;")]
    [InlineData("SELECT \"sqrt\" FROM t;")]
    [InlineData("SELECT \"current_date\" FROM t;")]
    [InlineData("SELECT \"abs\"(id) FROM t;")]
    public void Parse_QuotedNameOutsideUnsupportedVocabulary_ShouldParse(string sql)
    {
        // Arrange
        var parser = new SqlQueryParser();

        // Act
        var statement = parser.Parse(sql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }
}
