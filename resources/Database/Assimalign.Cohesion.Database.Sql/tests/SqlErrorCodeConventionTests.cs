using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlErrorCodeConventionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Error codes identify binding and value categories without prefixing the carrier message")]
    [InlineData("SELECT 1 / 0", "CS02001")]
    [InlineData("SELECT no_such_code_test_function(1)", "CS01016")]
    [InlineData("SELECT CAST('not-an-int' AS INTEGER)", "CS02022")]
    public async Task Execute_SemanticFailure_ShouldReportCategorizedCode(string text, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("short-code-contract", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(text));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        failure.Message.ShouldNotContain(code + ":", Case.Sensitive);
        await using var result = (Assimalign.Cohesion.Database.Execution.DatabaseResultSet)await session.ExecuteAsync("SELECT 1");
        result.Status.ShouldBe(Assimalign.Cohesion.Database.Execution.DatabaseResultStatus.Success);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Diagnostic fixtures reject short-code prefixes without a structured carrier")]
    public void DiagnosticAssertion_MissingCarrier_ShouldRejectPrefixOnlyFailure()
    {
        // Arrange
        var failure = new DatabaseException("CS02001: detail");

        // Act / Assert
        Should.Throw<ShouldAssertException>(() => failure.ShouldHaveMessage("CS02001: detail"));
    }
}
