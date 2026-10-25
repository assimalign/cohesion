using System;
using System.Linq;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class ParameterConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Parameters: unsupported forms have one supported-form diagnostic")]
    [InlineData("?")]
    [InlineData(":id")]
    [InlineData(":count")]
    [InlineData(":union")]
    [InlineData(":like")]
    [InlineData(":select")]
    public void Parse_UnsupportedParameterForm_ShouldReportOneLocatedDiagnostic(string form)
    {
        // Arrange
        string query = "MATCH (a) WHERE a.x = FORM RETURN a".Replace("FORM", form, StringComparison.Ordinal);

        // Act
        var statement = new GqlQueryParser().Parse(query);

        // Assert
        var diagnostic = statement.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CG00002");
        diagnostic.Start.ShouldBe(query.IndexOf(form, StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + form.Length);
        diagnostic.Message!.ShouldContain("$name");
        diagnostic.Message!.ShouldContain("@name");
        diagnostic.Message!.ShouldContain("$1");
        diagnostic.Message.ShouldBe(QueryDiagnostics.UnsupportedParameterForm(diagnostic.Code!,
            Location.Create(1, 1, diagnostic.Start!.Value, diagnostic.End!.Value)).Message);
        statement.Diagnostics.ShouldNotContain(item => item.Code == "CK00001");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Parameters: a trailing stray question mark keeps the generic message")]
    public void Parse_TrailingQuestionMark_ShouldKeepGenericDiagnostic()
    {
        // Arrange
        const string query = "MATCH (a) RETURN a ?";

        // Act
        var statement = new GqlQueryParser().Parse(query);

        // Assert
        var diagnostic = statement.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CG00002");
        diagnostic.Message!.ShouldContain("Unexpected character");
        diagnostic.Message!.ShouldNotContain("$name");
    }
}
