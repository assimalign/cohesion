using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DROP classifies native ownership by the requested normalized signature")]
    [InlineData("native_echo(TEXT)", "COHSQLD001")]
    [InlineData("NATIVE_ECHO(INT8)", "COHSQLD003")]
    [InlineData("native_echo(BIGINT)", "COHSQLD003")]
    public async Task ExecuteAsync_DropNativeOverload_ShouldClassifyRequestedSignature(string signature, string code)
    {
        // Arrange
        await using var engine = await BuildAsync(functions =>
            functions.Add(SqlScalarFunction.Create("native_echo", static (long value) => value)));
        await using var session = await SessionAsync(engine);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP FUNCTION " + signature));

        // Assert
        if (code == "COHSQLD001") { failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code); }
        else { failure.ShouldStartWithMessage(code + ":", Case.Sensitive); }
    }
}
