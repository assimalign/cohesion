using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SavepointConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Savepoints: spellings and decoded names round trip (#1726)")]
    [InlineData("SAVEPOINT scope")]
    [InlineData("RELEASE scope")]
    [InlineData("RELEASE SAVEPOINT scope")]
    [InlineData("ROLLBACK TO scope")]
    [InlineData("ROLLBACK TO SAVEPOINT scope")]
    [InlineData("SAVEPOINT \"a b\"")]
    [InlineData("ROLLBACK TO SAVEPOINT \"a\"\"b\"")]
    [InlineData("ROLLBACK WORK TO SAVEPOINT scope")]
    [InlineData("ROLLBACK WORK TO scope")]
    [InlineData("ROLLBACK TRANSACTION TO scope")]
    [InlineData("ROLLBACK TRANSACTION TO SAVEPOINT scope")]
    public void Parse_NamedControl_ShouldRoundTrip(string source)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(source).ShouldBeOfType<SqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        var control = statement.SqlExpression.ShouldBeOfType<SqlTransactionExpression>();
        control.SavepointName.ShouldNotBeNullOrEmpty();
        string rendered = SqlExpressionRenderer.Render(control);
        var reparsed = new SqlQueryParser().Parse(rendered).ShouldBeOfType<SqlQueryStatement>();
        reparsed.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        reparsed.SqlExpression.ShouldBeOfType<SqlTransactionExpression>().SavepointName.ShouldBe(control.SavepointName);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Savepoints: missing names and unconsumed suffixes refuse (#1726)")]
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
        var statement = new SqlQueryParser().Parse(source);

        // Assert
        statement.Diagnostics.ShouldContain(item => item.Severity == DiagnosticSeverity.Error);
    }
}
