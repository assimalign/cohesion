using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlServerRejectionSetupTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Server refuses invalid terminal drain timers at construction")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5184000000d)]
    [InlineData(4294967295d)]
    public async Task Create_InvalidDrainTimeout_ShouldRefuseAtConstruction(double milliseconds)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("drain-options", new());
        await using var listener = new BlockingOutputListener(int.MaxValue);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseServer.Create(engine, new()
        {
            Listener = listener, ShutdownDrainTimeout = TimeSpan.FromMilliseconds(milliseconds),
        })).ParamName.ShouldBe("ShutdownDrainTimeout");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Failed rejection setup disposes the connection and completes rejection")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectAsync_SetupFailure_ShouldDisposeAndComplete(bool beforeDeadline)
    {
        // Arrange: fail before the linked deadline can be created.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("rejection-setup", new());
        await using var listener = new BlockingOutputListener(int.MaxValue);
        await listener.BindAsync(timeout.Token);
        await using var peer = await listener.Factory.ConnectAsync(listener.EndPoint, timeout.Token);
        var connection = (BlockingOutputListener.BlockingConnection)await listener.AcceptAsync(timeout.Token);
        connection.FailSetup = beforeDeadline;
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        // Internal fault injection also exercises CancelAfter failing after registration.
        if (!beforeDeadline) { server.ServerOptions.ShutdownDrainTimeout = TimeSpan.FromDays(60); }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        await server.RejectAsync(connection, Guid.NewGuid(), completion, timeout.Token);

        // Assert
        completion.Task.IsCompletedSuccessfully.ShouldBeTrue();
        connection.Disposals.ShouldBe(1);
        connection.PendingOperations.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Peer-close linger rejects non-positive, infinite and oversized timers")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5184000000d)]
    [InlineData(4294967295d)]
    public async Task Create_InvalidTerminalLinger_ShouldRefuseAtConstruction(double milliseconds)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("linger-options", new());
        await using var listener = new BlockingOutputListener(int.MaxValue);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseServer.Create(engine, new()
        {
            Listener = listener, TerminalDrainLinger = TimeSpan.FromMilliseconds(milliseconds),
        })).ParamName.ShouldBe("TerminalDrainLinger");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Drain and linger validation includes the exact runtime timer boundaries")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_TimerBoundary_ShouldAcceptOnlyWithinRange(bool linger)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("timer-boundary", new());
        await using var listener = new BlockingOutputListener(int.MaxValue);
        var options = new SqlDatabaseServerOptions { Listener = listener };
        TimeSpan maximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        if (linger) { options.TerminalDrainLinger = maximum + TimeSpan.FromTicks(1); }
        else { options.ShutdownDrainTimeout = maximum + TimeSpan.FromTicks(1); }

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseServer.Create(engine, options));
        options.ShutdownDrainTimeout = linger ? TimeSpan.FromTicks(1) : maximum;
        options.TerminalDrainLinger = linger ? maximum : TimeSpan.FromTicks(1);
        await using var server = SqlDatabaseServer.Create(engine, options);
        server.ServerOptions.ShutdownDrainTimeout.ShouldBe(linger ? TimeSpan.FromTicks(1) : maximum);
        server.ServerOptions.TerminalDrainLinger.ShouldBe(linger ? maximum : TimeSpan.FromTicks(1));
        options.TerminalDrainLinger = TimeSpan.Zero;
        server.ServerOptions.TerminalDrainLinger.ShouldBe(linger ? maximum : TimeSpan.FromTicks(1));
    }
}
