using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpSaslExternalMechanismTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Amqp] - CreateExchange: Should send the authorization id as the initial response, empty without one")]
    [InlineData(null, "")]
    [InlineData("admin", "admin")]
    public async Task CreateExchange_OnClient_ShouldSendAuthorizationId(string? authorizationId, string expected)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        AmqpSaslExternalClientMechanism mechanism = new() { AuthorizationId = authorizationId };

        // Act
        IAmqpSaslClientExchange exchange = mechanism.CreateExchange(connections.Client, null);
        ReadOnlyMemory<byte>? initial = await exchange.GetInitialResponseAsync(timeout.Token);

        // Assert
        mechanism.Name.ShouldBe(new AmqpSymbol("EXTERNAL"));
        Encoding.UTF8.GetString(initial.ShouldNotBeNull().Span).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - EvaluateAsync: Should pass the connection and authorization id to the callback and succeed with its identity")]
    [InlineData("", null)]
    [InlineData("admin", "admin")]
    public async Task EvaluateAsync_WithIdentity_ShouldSucceed(string response, string? authorizationId)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        IConnection? seenConnection = null;
        string? seenAuthorizationId = "unset";
        AmqpSaslExternalServerMechanism mechanism = AmqpSaslExternalServerMechanism.Create((connection, requested, _) =>
        {
            seenConnection = connection;
            seenAuthorizationId = requested;
            return ValueTask.FromResult<string?>("CN=client.test");
        });
        IAmqpSaslServerExchange exchange = mechanism.CreateExchange(connections.Server, "broker.test");

        // Act
        AmqpSaslServerStep step = await exchange.EvaluateAsync(Encoding.UTF8.GetBytes(response), timeout.Token);

        // Assert
        step.Code.ShouldBe(AmqpSaslCode.Ok);
        step.Identity.ShouldBe("CN=client.test");
        seenConnection.ShouldBeSameAs(connections.Server);
        seenAuthorizationId.ShouldBe(authorizationId);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - EvaluateAsync: Should fail authentication when the callback returns no identity")]
    public async Task EvaluateAsync_WithoutIdentity_ShouldFailAuthentication()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        AmqpSaslExternalServerMechanism mechanism = AmqpSaslExternalServerMechanism.Create((_, _, _) => ValueTask.FromResult<string?>(null));

        // Act
        AmqpSaslServerStep step = await mechanism.CreateExchange(connections.Server, null).EvaluateAsync(ReadOnlyMemory<byte>.Empty, timeout.Token);

        // Assert
        step.Code.ShouldBe(AmqpSaslCode.Auth);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - EvaluateAsync: Should fail an authorization id that is not UTF-8 without calling the callback")]
    public async Task EvaluateAsync_WithInvalidUtf8_ShouldFailWithoutCallback()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        int calls = 0;
        AmqpSaslExternalServerMechanism mechanism = AmqpSaslExternalServerMechanism.Create((_, _, _) =>
        {
            calls++;
            return ValueTask.FromResult<string?>("anyone");
        });

        // Act
        AmqpSaslServerStep step = await mechanism.CreateExchange(connections.Server, null).EvaluateAsync(new byte[] { 0xff }, timeout.Token);

        // Assert
        step.Code.ShouldBe(AmqpSaslCode.Auth);
        calls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - EvaluateAsync: Should answer a missing initial response with one empty challenge, then fail")]
    public async Task EvaluateAsync_WithoutInitialResponse_ShouldChallengeOnceThenFail()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        AmqpSaslExternalServerMechanism mechanism = AmqpSaslExternalServerMechanism.Create((_, _, _) => ValueTask.FromResult<string?>("anyone"));
        IAmqpSaslServerExchange exchange = mechanism.CreateExchange(connections.Server, null);

        // Act
        AmqpSaslServerStep challenge = await exchange.EvaluateAsync(null, timeout.Token);
        AmqpSaslServerStep second = await exchange.EvaluateAsync(null, timeout.Token);

        // Assert
        challenge.IsComplete.ShouldBeFalse();
        challenge.Data.IsEmpty.ShouldBeTrue();
        second.Code.ShouldBe(AmqpSaslCode.Auth);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Create: Should refuse a null callback and a null connection")]
    public void Create_WithNullCallback_ShouldThrowArgumentNullException()
    {
        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => AmqpSaslExternalServerMechanism.Create(null!));

        // Assert
        exception.ParamName.ShouldBe("authenticate");
        Should.Throw<ArgumentNullException>(() => new AmqpSaslExternalClientMechanism().CreateExchange(null!, null)).ParamName.ShouldBe("connection");
    }
}
