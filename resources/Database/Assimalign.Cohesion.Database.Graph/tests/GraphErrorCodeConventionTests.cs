using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphErrorCodeConventionTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Error categories retain structured diagnostics and session usability")]
    [InlineData("RETURN 1 / 0 AS value", "CG02013")]
    [InlineData("COMMIT", "CG04023")]
    public async Task Execute_SemanticFailure_ShouldReportCategorizedCode(string text, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("short-code-contract", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(text));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        failure.Message.ShouldNotContain(code + ":", Case.Sensitive);
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("RETURN 1 AS value");
        result.Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Unknown-label diagnostic keeps warning severity with its binding-category code")]
    public void UnknownLabel_MissingToken_ShouldRetainWarningSeverity()
    {
        // Arrange / Act
        var warning = GraphTokenResolver.UnknownLabel("missing", null);

        // Assert
        warning.Code.ShouldBe("CG01010");
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.Message.ShouldNotBeNull().ShouldNotContain("CG01010:", Case.Sensitive);
    }
}
