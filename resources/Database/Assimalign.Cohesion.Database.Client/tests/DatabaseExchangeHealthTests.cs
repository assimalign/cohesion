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
        harness.Server.Sessions.ShouldHaveSingleItem();
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

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Exchange health: each run starts without the previous run's completion evidence")]
    public async Task ExecuteAsync_ReusedExchangeWithoutNewEvidence_ShouldDiscardSession()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await ClientTestHarness.StartAsync(configureSettings: settings => settings.MaxPoolSize = 1);
        var exchange = new CompleteOnceExchange();
        var first = await harness.Client.RentAsync(timeout.Token);
        await Should.ThrowAsync<DatabaseClientException>(async () => await first.ExecuteAsync(exchange, timeout.Token));
        first.IsOpen.ShouldBeTrue();

        // Act: the same exchange runs again and this time leaves its response unread.
        await Should.ThrowAsync<DatabaseClientException>(async () => await first.ExecuteAsync(exchange, timeout.Token));
        bool openAfterSecondRun = first.IsOpen;
        await first.DisposeAsync();
        await using var next = await harness.Client.RentAsync(timeout.Token);
        var result = await next.ExecuteAsync("SELECT id FROM users WHERE id = 1", cancellationToken: timeout.Token);

        // Assert
        openAfterSecondRun.ShouldBeFalse();
        next.ShouldNotBeSameAs(first);
        result.Rows.ShouldHaveSingleItem().ShouldBe([1]);
    }

    private sealed class UnreadResponseExchange : DatabaseProtocolExchange<bool>
    {
        private readonly ProtocolErrorCode _code;

        /// <summary>Initializes a new instance of the <see cref="UnreadResponseExchange"/> class.</summary>
        /// <param name="code">The error code the exchange reports after leaving its response unread.</param>
        public UnreadResponseExchange(ProtocolErrorCode code)
            : base(SqlProtocol.Family)
        {
            _code = code;
        }

        protected override async ValueTask<bool> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken)
        {
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            // Pong remains unread. A familiar statement code is no proof of a clean session.
            throw new DatabaseClientException(_code, "Transfer stopped with its response unread.");
        }
    }

    private sealed class CompleteResponseExchange : DatabaseProtocolExchange<bool>
    {
        /// <summary>Initializes a new instance of the <see cref="CompleteResponseExchange"/> class.</summary>
        public CompleteResponseExchange()
            : base(SqlProtocol.Family)
        {
        }

        protected override async ValueTask<bool> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken)
        {
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            (await reader.ReadFrameAsync(cancellationToken))?.Type.ShouldBe(ProtocolMessageType.Pong);
            MarkResponseComplete();
            throw new DatabaseClientException(ProtocolErrorCode.Unavailable, "Completed model response rejected by the caller.");
        }
    }

    // Certifies its complete response on the first run only. The second run leaves Pong unread, so
    // only the base's reset before each run keeps the first run's evidence from reaching it.
    private sealed class CompleteOnceExchange : DatabaseProtocolExchange<bool>
    {
        private int _runs;

        /// <summary>Initializes a new instance of the <see cref="CompleteOnceExchange"/> class.</summary>
        public CompleteOnceExchange()
            : base(SqlProtocol.Family)
        {
        }

        protected override async ValueTask<bool> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken)
        {
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            if (++_runs == 1)
            {
                (await reader.ReadFrameAsync(cancellationToken))?.Type.ShouldBe(ProtocolMessageType.Pong);
                MarkResponseComplete();
            }
            throw new DatabaseClientException(ProtocolErrorCode.Unavailable, $"Run {_runs} rejected by the caller.");
        }
    }
}
