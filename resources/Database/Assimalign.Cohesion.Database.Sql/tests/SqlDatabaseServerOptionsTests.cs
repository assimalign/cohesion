using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public class SqlDatabaseServerOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Server: Options carry safe defaults")]
    public void Options_Defaults_ShouldBeBounded()
    {
        // Arrange
        var options = new SqlDatabaseServerOptions();

        // Assert
        options.MaxSessions.ShouldBeGreaterThan(0);
        options.AuthenticationTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        options.IdleTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        options.ShutdownDrainTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Server: Creation rejects a missing listener and a non-positive session limit")]
    public async System.Threading.Tasks.Task Create_WithInvalidOptions_ShouldThrow()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "opt-validate" });

        // Act / Assert: no listener.
        Should.Throw<ArgumentException>(() => SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions()));

        // Act / Assert: non-positive session limit.
        await using var listener = new InMemoryConnectionListener();
        Should.Throw<ArgumentException>(() => SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener, MaxSessions = 0 }));

        // Act / Assert: null engine.
        Should.Throw<ArgumentNullException>(() => SqlDatabaseServer.Create(null!, new SqlDatabaseServerOptions { Listener = listener }));
    }
}
