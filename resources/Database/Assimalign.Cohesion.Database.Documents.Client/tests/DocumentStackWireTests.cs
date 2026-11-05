using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentStackWireTests
{
    [Fact(DisplayName = "Cohesion Test [Documents.Client] - TCP stack refusal reports CD05021 and preserves the exact session (#1803)")]
    public async Task QueryAsync_RuntimeStackFailure_ShouldKeepTcpSessionAsync()
    {
        // Arrange: inject the runtime backstop at a real evaluation boundary.
        int calls = 0;
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp: true, configureEngine: builder =>
            builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("stack_refusal", _ =>
            {
                calls++;
                throw new InsufficientExecutionStackException();
            })));
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;

        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () =>
            await connection.QueryAsync("SELECT RAW stack_refusal(1) FROM [0] AS z", cancellationToken: harness.Token));

        // Assert
        calls.ShouldBe(1);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05021");
        (await connection.QueryAsync("SELECT RAW 7 FROM [0] AS z", cancellationToken: harness.Token))
            .Documents.ShouldHaveSingleItem().GetInt32().ShouldBe(7);
        connection.IsOpen.ShouldBeTrue();
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
