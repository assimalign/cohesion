using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpSaslAnonymousMechanismTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Amqp] - CreateExchange: Should send the trace as the initial response, empty without one")]
    [InlineData(null, "")]
    [InlineData("tester@example.test", "tester@example.test")]
    public async Task CreateExchange_OnClient_ShouldSendTrace(string? trace, string expected)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        AmqpSaslAnonymousClientMechanism mechanism = new() { Trace = trace };

        // Act
        IAmqpSaslClientExchange exchange = mechanism.CreateExchange(connections.Client, null);
        ReadOnlyMemory<byte>? initial = await exchange.GetInitialResponseAsync(timeout.Token);

        // Assert
        mechanism.Name.ShouldBe(new AmqpSymbol("ANONYMOUS"));
        Encoding.UTF8.GetString(initial.ShouldNotBeNull().Span).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - EvaluateAsync: Should accept any response, or none, with no identity")]
    public async Task EvaluateAsync_OnServer_ShouldSucceedWithoutIdentity()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        AmqpSaslAnonymousServerMechanism mechanism = new();
        IAmqpSaslServerExchange exchange = mechanism.CreateExchange(connections.Server, null);

        // Act
        AmqpSaslServerStep withTrace = await exchange.EvaluateAsync(Encoding.UTF8.GetBytes("trace"), timeout.Token);
        AmqpSaslServerStep withoutResponse = await exchange.EvaluateAsync(null, timeout.Token);

        // Assert
        mechanism.Name.ShouldBe(new AmqpSymbol("ANONYMOUS"));
        withTrace.Code.ShouldBe(AmqpSaslCode.Ok);
        withTrace.Identity.ShouldBeNull();
        withoutResponse.Code.ShouldBe(AmqpSaslCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - EvaluateAsync: Should observe a canceled token")]
    public async Task EvaluateAsync_WithCanceledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        await using TestConnectionPair connections = new();
        IAmqpSaslServerExchange exchange = new AmqpSaslAnonymousServerMechanism().CreateExchange(connections.Server, null);

        // Act
        Func<Task> evaluate = async () => await exchange.EvaluateAsync(null, new CancellationToken(canceled: true));

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(evaluate);
    }
}
