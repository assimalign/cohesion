using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class QuotedFunctionDiagnosticTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Quoted functions: value functions remain deferred and ranking requires binding")]
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
        if (name is "first_value" or "last_value" or "nth_value")
        {
            var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CK00001", diagnostic.Message);
            diagnostic.Start.ShouldBe(7);
            diagnostic.End.ShouldBe(9 + name.Length);
        }
        else { statement.Diagnostics.ShouldBeEmpty(); } // Required OVER is a binding diagnostic.
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
