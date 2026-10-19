using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlReturningProtocolTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - RETURNING: failure emits exactly one Error and no partial result frames")]
    [InlineData("INSERT INTO users VALUES (3, 'new') RETURNING 1 / 0", ProtocolErrorCode.ExecutionFailure)]
    [InlineData("DELETE FROM users RETURNING OLD.id", ProtocolErrorCode.ParseFailure)]
    public async Task Execute_FailedReturning_ShouldEmitOnlyErrorAndKeepSession(string sql, ProtocolErrorCode code)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());

        // Assert: Error is the first response. Any extra Error or partial result is observed
        // before the next command's header, so the same stream proves exactly one failure.
        var error = await client.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(error.Payload.Span).Code.ShouldBe(code);
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT 1 FROM users").Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        await client.ExpectAsync(ProtocolMessageType.ResultRow);
        await client.ExpectAsync(ProtocolMessageType.ResultRow);
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }
}
