using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Server.Tests;

public class DatabaseServerOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Server] - Server: Options carry safe defaults")]
    public void Options_Defaults_ShouldBeBounded()
    {
        // Arrange
        var options = new DatabaseServerOptions();

        // Assert
        options.MaxSessions.ShouldBeGreaterThan(0);
        options.AuthenticationTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        options.IdleTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        options.ShutdownDrainTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Server: Construction rejects a missing listener and a non-positive session limit")]
    public async System.Threading.Tasks.Task Constructor_WithInvalidOptions_ShouldThrow()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "opt-validate" });

        // Act / Assert: no listener.
        Should.Throw<ArgumentException>(() => new TestDatabaseServer(engine, new DatabaseServerOptions()));

        // Act / Assert: non-positive session limit.
        await using var listener = new InMemoryConnectionListener();
        Should.Throw<ArgumentException>(() => new TestDatabaseServer(engine, new DatabaseServerOptions { Listener = listener, MaxSessions = 0 }));

        // Act / Assert: null engine.
        Should.Throw<ArgumentNullException>(() => new TestDatabaseServer(null!, new DatabaseServerOptions { Listener = listener }));
    }
}
