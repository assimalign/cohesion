using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>Pool reuse follows the exchange's completion evidence rather than its error code.</summary>
public sealed class DatabaseExchangeHealthTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Client] - Exchange health: complete statement failures preserve the next pool rental")]
    [InlineData("TRUNCATE TABLE users;", ProtocolErrorCode.ParseFailure)]
    [InlineData("SELECT id FROM missing_table", ProtocolErrorCode.ExecutionFailure)]
    public async Task ExecuteAsync_CompleteStatementFailure_ShouldReuseSameSession(string statement, ProtocolErrorCode code)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await ClientTestHarness.StartAsync(configureSettings: settings => settings.MaxPoolSize = 1);
        var first = await harness.Client.RentAsync(timeout.Token);

        // Act
        var exception = await Should.ThrowAsync<DatabaseClientException>(async () =>
            await first.ExecuteAsync(statement, cancellationToken: timeout.Token));
        await first.DisposeAsync();
        await using var next = await harness.Client.RentAsync(timeout.Token);
        var result = await next.ExecuteAsync("SELECT id FROM users WHERE id = 1", cancellationToken: timeout.Token);

        // Assert
        exception.Code.ShouldBe(code);
        next.ShouldBeSameAs(first);
        result.Rows.ShouldHaveSingleItem().ShouldBe([1]);
        harness.Server.Context.Sessions.ShouldHaveSingleItem();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Exchange health: statement-like codes cannot make an unread response reusable")]
    [InlineData(ProtocolErrorCode.ParseFailure)]
    [InlineData(ProtocolErrorCode.ExecutionFailure)]
    public async Task ExecuteAsync_UnreadResponseWithStatementCode_ShouldDiscardSession(ProtocolErrorCode code)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await ClientTestHarness.StartAsync(configureSettings: settings => settings.MaxPoolSize = 1);
        var first = await harness.Client.RentAsync(timeout.Token);

        // Act
        var exception = await Should.ThrowAsync<DatabaseClientException>(async () =>
            await first.ExecuteAsync(new UnreadResponseExchange(code), timeout.Token));
        await first.DisposeAsync();
        await using var next = await harness.Client.RentAsync(timeout.Token);
        var result = await next.ExecuteAsync("SELECT id FROM users WHERE id = 2", cancellationToken: timeout.Token);

        // Assert
        exception.Code.ShouldBe(code);
        next.ShouldNotBeSameAs(first);
        result.Rows.ShouldHaveSingleItem().ShouldBe([2]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Exchange health: completion evidence preserves a session independently of the diagnostic code")]
    public async Task ExecuteAsync_CompleteNonStatementFailure_ShouldReuseSameSession()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await ClientTestHarness.StartAsync(configureSettings: settings => settings.MaxPoolSize = 1);
        var first = await harness.Client.RentAsync(timeout.Token);

        // Act
        var exception = await Should.ThrowAsync<DatabaseClientException>(async () =>
            await first.ExecuteAsync(new CompleteResponseExchange(), timeout.Token));
        await first.DisposeAsync();
        await using var next = await harness.Client.RentAsync(timeout.Token);
        var result = await next.ExecuteAsync("SELECT id FROM users WHERE id = 1", cancellationToken: timeout.Token);

        // Assert
        exception.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        next.ShouldBeSameAs(first);
        result.Rows.ShouldHaveSingleItem();
    }

    private sealed class UnreadResponseExchange : IDatabaseProtocolExchange<bool>
    {
        private readonly ProtocolErrorCode _code;

        /// <summary>Initializes a new instance of the <see cref="UnreadResponseExchange"/> class.</summary>
        /// <param name="code">The error code the exchange reports after leaving its response unread.</param>
        public UnreadResponseExchange(ProtocolErrorCode code)
        {
            _code = code;
        }

        public ProtocolMessageFamily Family => SqlProtocol.Family;

        public async ValueTask<bool> ExecuteAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer, CancellationToken cancellationToken = default)
        {
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            // Pong remains unread. A familiar statement code is no proof of a clean session.
            throw new DatabaseClientException(_code, "Transfer stopped with its response unread.");
        }
    }

    private sealed class CompleteResponseExchange : IDatabaseProtocolExchange<bool>
    {
        public ProtocolMessageFamily Family => SqlProtocol.Family;
        public bool IsResponseComplete { get; private set; }

        public async ValueTask<bool> ExecuteAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer, CancellationToken cancellationToken = default)
        {
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            (await reader.ReadFrameAsync(cancellationToken))?.Type.ShouldBe(ProtocolMessageType.Pong);
            IsResponseComplete = true;
            throw new DatabaseClientException(ProtocolErrorCode.Unavailable, "Completed model response rejected by the caller.");
        }
    }
}
