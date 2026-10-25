using System;
using System.Linq;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class ParameterConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Parameters: unsupported forms have one supported-form diagnostic")]
    [InlineData("?")]
    [InlineData(":id")]
    [InlineData(":count")]
    [InlineData(":union")]
    [InlineData(":like")]
    [InlineData(":select")]
    public void Parse_UnsupportedParameterForm_ShouldReportOneLocatedDiagnostic(string form)
    {
        // Arrange
        string query = "SELECT a FROM t WHERE b = FORM;".Replace("FORM", form, StringComparison.Ordinal);

        // Act
        var statement = new SqlQueryParser().Parse(query);

        // Assert
        var diagnostic = statement.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS00003");
        diagnostic.Start.ShouldBe(query.IndexOf(form, StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + form.Length);
        diagnostic.Message!.ShouldContain("$name");
        diagnostic.Message!.ShouldContain("@name");
        diagnostic.Message!.ShouldContain("$1");
        diagnostic.Message.ShouldBe(QueryDiagnostics.UnsupportedParameterForm(diagnostic.Code!,
            Location.Create(1, 1, diagnostic.Start!.Value, diagnostic.End!.Value)).Message);
        statement.Diagnostics.ShouldNotContain(item => item.Code == "CK00001");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Parameters: a trailing stray question mark keeps the generic message")]
    public void Parse_TrailingQuestionMark_ShouldKeepGenericDiagnostic()
    {
        // Arrange
        const string query = "SELECT a FROM t ?";

        // Act
        var statement = new SqlQueryParser().Parse(query);

        // Assert
        var diagnostic = statement.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS00003");
        diagnostic.Message!.ShouldContain("Unexpected character");
        diagnostic.Message!.ShouldNotContain("$name");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Parameters: identifier and numeric names preserve the ((SqlSelectExpression)statement.SqlExpression).Columns[0].Expression.ShouldBeOfType<SqlParameterExpression>().ParameterName contract")]
    [InlineData("$name")]
    [InlineData("@name")]
    [InlineData("$_x")]
    [InlineData("$1")]
    [InlineData("@1")]
    [InlineData("$01")]
    [InlineData("$0")]
    [InlineData("$9223372036854775808")]
    public void Parse_ValidParameterName_ShouldPreserveAstName(string form)
    {
        // Arrange
        string query = $"SELECT {form} FROM t;";

        // Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(query);

        // Assert
        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        ((SqlSelectExpression)statement.SqlExpression).Columns[0].Expression.ShouldBeOfType<SqlParameterExpression>().ParameterName.ShouldBe(form);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Parameters: malformed names fail once at the parameter span")]
    [InlineData("$")]
    [InlineData("@")]
    [InlineData("$1a")]
    [InlineData("@1a")]
    [InlineData("$\u0661")]
    [InlineData("@\u0661")]
    [InlineData("$1\u0661")]
    public void Parse_InvalidParameterName_ShouldReportOneGrammarDiagnostic(string form)
    {
        // Arrange
        string query = $"SELECT {form} FROM t;";

        // Act
        var statement = new SqlQueryParser().Parse(query);

        // Assert
        var diagnostic = statement.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS00003");
        diagnostic.Start.ShouldBe(7);
        diagnostic.End.ShouldBe(7 + form.Length);
        diagnostic.Message!.ShouldContain("identifier");
        diagnostic.Message!.ShouldContain("digits only");
    }
}
