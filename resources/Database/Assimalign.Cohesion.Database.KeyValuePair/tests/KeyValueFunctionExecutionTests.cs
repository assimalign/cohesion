using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public sealed class KeyValueFunctionExecutionTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Functions: CALL resolves an unregistered function and keeps the session usable")]
    public async Task ExecuteCommandTextAsync_UnregisteredCall_ShouldReportUnknownFunctionAsync()
    {
        // Arrange: only existing engine/session APIs, so this reproduces CALL's absent
        // dispatch rather than failing to compile against a not-yet-added function API.
        await using var engine = KeyValueDatabaseEngine.Create("function-resolution", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        DatabaseException error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            await session.ExecuteCommandTextAsync("CALL missing_function", cancellationToken: CancellationToken.None);
        });

        // Assert: the command is recognized; resolution owns this failure, not verb parsing.
        Diagnostic diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CV01018");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Message.ShouldBe(error.Message);
        error.ShouldNotBeOfType<DatabaseParseException>();
        error.Message.ShouldContain("missing_function", Case.Sensitive);
        byte[] missingKey = [42];
        bool exists = await database.ExistsAsync(session, missingKey, CancellationToken.None);
        exists.ShouldBeFalse();
    }
}
