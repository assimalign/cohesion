using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class SavepointConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Savepoints: spellings and decoded names round trip (#1726)")]
    [InlineData("SAVEPOINT scope")]
    [InlineData("RELEASE scope")]
    [InlineData("RELEASE SAVEPOINT scope")]
    [InlineData("ROLLBACK TO scope")]
    [InlineData("ROLLBACK TO SAVEPOINT scope")]
    [InlineData("SAVEPOINT \"a b\"")]
    [InlineData("ROLLBACK TO SAVEPOINT \"a\"\"b\"")]
    public void Parse_NamedControl_ShouldRoundTrip(string source)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(source).ShouldBeOfType<GqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        var control = statement.GqlExpression.ShouldBeOfType<GqlQueryExpression>();
        control.SavepointName.ShouldNotBeNullOrEmpty();
        string rendered = control.ToString();
        var reparsed = new GqlQueryParser().Parse(rendered).ShouldBeOfType<GqlQueryStatement>();
        reparsed.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        reparsed.GqlExpression.ShouldBeOfType<GqlQueryExpression>().SavepointName.ShouldBe(control.SavepointName);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Savepoints: missing names and unconsumed suffixes refuse (#1726)")]
    [InlineData("SAVEPOINT")]
    [InlineData("SAVEPOINT \"\"")]
    [InlineData("RELEASE")]
    [InlineData("RELEASE SAVEPOINT")]
    [InlineData("ROLLBACK TO")]
    [InlineData("ROLLBACK TO SAVEPOINT")]
    [InlineData("ROLLBACK TO SAVEPOINT scope extra")]
    [InlineData("SAVEPOINT scope extra")]
    [InlineData("RELEASE scope extra")]
    public void Parse_MalformedControl_ShouldRefuse(string source)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(source);

        // Assert
        statement.Diagnostics.ShouldContain(item => item.Severity == DiagnosticSeverity.Error);
    }
}
