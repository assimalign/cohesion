using System;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentErrorCodeConventionTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Error codes identify binding and value categories and leave the session usable")]
    [InlineData("SELECT 1 / 0 AS calculated FROM items", "CD02006")]
    [InlineData("SELECT @missing AS calculated FROM items", "CD01005")]
    [InlineData("SELECT SQRT(-1) AS calculated FROM items", "CD02019")]
    public async Task Execute_SemanticFailure_ShouldReportCategorizedCode(string text, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("short-code-contract", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        var items = await session.CreateCollectionAsync("items");
        await items.PutAsync(session, "one", "{}"u8.ToArray());

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(text));

        // Assert
        failure.Diagnostics.Select(diagnostic => diagnostic.Code).ShouldContain(code, failure.Message);
        failure.Message.ShouldNotContain(code + ":", Case.Sensitive);
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT 1 AS calculated FROM items");
        result.Status.ShouldBe(DatabaseResultStatus.Success);
    }
}
