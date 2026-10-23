using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DROP classifies native ownership by the requested normalized signature")]
    [InlineData("native_echo(TEXT)", "CS01009")]
    [InlineData("NATIVE_ECHO(INT8)", "CS03303")]
    [InlineData("native_echo(BIGINT)", "CS03303")]
    public async Task ExecuteAsync_DropNativeOverload_ShouldClassifyRequestedSignature(string signature, string code)
    {
        // Arrange
        await using var engine = await BuildAsync(functions =>
            functions.Add(SqlScalarFunction.Create("native_echo", static (long value) => value)));
        await using var session = await SessionAsync(engine);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP FUNCTION " + signature));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        failure.Message.ShouldNotContain(code + ":", Case.Sensitive);
    }
}
