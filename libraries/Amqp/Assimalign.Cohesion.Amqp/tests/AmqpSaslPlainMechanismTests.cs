using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpSaslPlainMechanismTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Amqp] - CreateExchange: Should send the RFC 4616 message first and again after an empty challenge")]
    [InlineData(null, "\0user\0pass")]
    [InlineData("admin", "admin\0user\0pass")]
    public async Task CreateExchange_OnClient_ShouldSendPlainMessage(string? authorizationId, string expected)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        AmqpSaslPlainClientMechanism mechanism = AmqpSaslPlainClientMechanism.Create("user", "pass", authorizationId);

        // Act
        IAmqpSaslClientExchange exchange = mechanism.CreateExchange(connections.Client, "broker.test");
        ReadOnlyMemory<byte>? initial = await exchange.GetInitialResponseAsync(timeout.Token);
        ReadOnlyMemory<byte> afterChallenge = await exchange.RespondAsync(ReadOnlyMemory<byte>.Empty, timeout.Token);

        // Assert
        mechanism.Name.ShouldBe(new AmqpSymbol("PLAIN"));
        Encoding.UTF8.GetString(initial.ShouldNotBeNull().Span).ShouldBe(expected);
        Encoding.UTF8.GetString(afterChallenge.Span).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - Create: Should refuse a credential that is missing, empty or holds a NUL")]
    [InlineData("", "pass", null, "userName")]
    [InlineData("us\0er", "pass", null, "userName")]
    [InlineData("user", "", null, "password")]
    [InlineData("user", "pa\0ss", null, "password")]
    [InlineData("user", "pass", "ad\0min", "authorizationId")]
    public void Create_WithInvalidCredential_ShouldThrowArgumentException(string userName, string password, string? authorizationId, string parameter)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpSaslPlainClientMechanism.Create(userName, password, authorizationId));

        // Assert
        exception.ParamName.ShouldBe(parameter);
        Should.Throw<ArgumentNullException>(() => AmqpSaslPlainClientMechanism.Create(null!, "pass")).ParamName.ShouldBe("userName");
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - EvaluateAsync: Should validate the credentials and name the identity after the authorization id, else the user")]
    [InlineData("\0user\0pass", null, "user")]
    [InlineData("admin\0user\0pass", "admin", "admin")]
    public async Task EvaluateAsync_WithValidMessage_ShouldSucceed(string message, string? authorizationId, string identity)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        List<AmqpSaslPlainCredentials> validated = [];
        AmqpSaslPlainServerMechanism mechanism = AmqpSaslPlainServerMechanism.Create((credentials, _) =>
        {
            validated.Add(credentials);
            return ValueTask.FromResult(credentials.UserName == "user" && credentials.Password == "pass");
        });

        // Act
        IAmqpSaslServerExchange exchange = mechanism.CreateExchange(connections.Server, "broker.test");
        AmqpSaslServerStep step = await exchange.EvaluateAsync(Encoding.UTF8.GetBytes(message), timeout.Token);

        // Assert
        step.Code.ShouldBe(AmqpSaslCode.Ok);
        step.Identity.ShouldBe(identity);
        validated.Count.ShouldBe(1);
        validated[0].AuthorizationId.ShouldBe(authorizationId);
        validated[0].UserName.ShouldBe("user");
        validated[0].Password.ShouldBe("pass");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - EvaluateAsync: Should fail authentication when the validator refuses the credentials")]
    public async Task EvaluateAsync_WithRefusedCredentials_ShouldFailAuthentication()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        AmqpSaslPlainServerMechanism mechanism = AmqpSaslPlainServerMechanism.Create((_, _) => ValueTask.FromResult(false));
        IAmqpSaslServerExchange exchange = mechanism.CreateExchange(connections.Server, null);

        // Act
        AmqpSaslServerStep step = await exchange.EvaluateAsync(Encoding.UTF8.GetBytes("\0user\0wrong"), timeout.Token);

        // Assert
        step.Code.ShouldBe(AmqpSaslCode.Auth);
        step.Identity.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - EvaluateAsync: Should fail a malformed message without calling the validator")]
    [InlineData("no separator", "user")]
    [InlineData("one separator", "\0user")]
    [InlineData("an empty user name", "\0\0pass")]
    [InlineData("an empty password", "\0user\0")]
    [InlineData("a NUL in the password", "\0user\0pa\0ss")]
    public async Task EvaluateAsync_WithMalformedMessage_ShouldFailWithoutValidating(string scenario, string message)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        int calls = 0;
        AmqpSaslPlainServerMechanism mechanism = AmqpSaslPlainServerMechanism.Create((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(true);
        });
        IAmqpSaslServerExchange exchange = mechanism.CreateExchange(connections.Server, null);

        // Act
        AmqpSaslServerStep step = await exchange.EvaluateAsync(Encoding.UTF8.GetBytes(message), timeout.Token);

        // Assert
        step.Code.ShouldBe(AmqpSaslCode.Auth, scenario);
        calls.ShouldBe(0, scenario);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - EvaluateAsync: Should fail a user name over 255 bytes or bytes that are not UTF-8")]
    public async Task EvaluateAsync_WithOversizedOrInvalidUtf8Credential_ShouldFail()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        AmqpSaslPlainServerMechanism mechanism = AmqpSaslPlainServerMechanism.Create((_, _) => ValueTask.FromResult(true));
        byte[] oversized = Encoding.UTF8.GetBytes("\0" + new string('a', 256) + "\0pass");
        byte[] invalidUtf8 = [0x00, 0xff, 0xfe, 0x00, (byte)'p'];

        // Act
        AmqpSaslServerStep oversizedStep = await mechanism.CreateExchange(connections.Server, null).EvaluateAsync(oversized, timeout.Token);
        AmqpSaslServerStep invalidStep = await mechanism.CreateExchange(connections.Server, null).EvaluateAsync(invalidUtf8, timeout.Token);

        // Assert
        oversizedStep.Code.ShouldBe(AmqpSaslCode.Auth);
        invalidStep.Code.ShouldBe(AmqpSaslCode.Auth);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - EvaluateAsync: Should answer a missing initial response with one empty challenge, then fail")]
    public async Task EvaluateAsync_WithoutInitialResponse_ShouldChallengeOnceThenFail()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using TestConnectionPair connections = new();
        AmqpSaslPlainServerMechanism mechanism = AmqpSaslPlainServerMechanism.Create((_, _) => ValueTask.FromResult(true));
        IAmqpSaslServerExchange exchange = mechanism.CreateExchange(connections.Server, null);

        // Act
        AmqpSaslServerStep challenge = await exchange.EvaluateAsync(null, timeout.Token);
        AmqpSaslServerStep second = await exchange.EvaluateAsync(null, timeout.Token);

        // Assert
        challenge.IsComplete.ShouldBeFalse();
        challenge.Data.IsEmpty.ShouldBeTrue();
        second.Code.ShouldBe(AmqpSaslCode.Auth);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - CreateExchange: Should refuse a null connection on either side, and a null validator")]
    public void CreateExchange_WithNullConnection_ShouldThrowArgumentNullException()
    {
        // Arrange
        AmqpSaslPlainClientMechanism client = AmqpSaslPlainClientMechanism.Create("user", "pass");
        AmqpSaslPlainServerMechanism server = AmqpSaslPlainServerMechanism.Create((_, _) => ValueTask.FromResult(true));

        // Act
        ArgumentNullException clientException = Should.Throw<ArgumentNullException>(() => client.CreateExchange(null!, null));
        ArgumentNullException serverException = Should.Throw<ArgumentNullException>(() => server.CreateExchange(null!, null));

        // Assert
        clientException.ParamName.ShouldBe("connection");
        serverException.ParamName.ShouldBe("connection");
        Should.Throw<ArgumentNullException>(() => AmqpSaslPlainServerMechanism.Create(null!)).ParamName.ShouldBe("validate");
    }
}
