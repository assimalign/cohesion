using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class QuotedFunctionDiagnosticTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Quoted functions: window functions require OVER during binding")]
    [InlineData("lead")]
    [InlineData("lag")]
    [InlineData("rank")]
    [InlineData("ntile")]
    [InlineData("first_value")]
    [InlineData("last_value")]
    [InlineData("nth_value")]
    public void Parse_QuotedWindowCall_ShouldRespectExecutionBoundary(string name)
    {
        // Arrange
        string sql = $"SELECT \"{name}\"(id) FROM t;";

        // Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty(); // Required OVER is a binding diagnostic.
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
