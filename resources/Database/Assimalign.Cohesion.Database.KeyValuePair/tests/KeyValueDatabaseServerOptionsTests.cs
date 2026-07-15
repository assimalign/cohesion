using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public class KeyValueDatabaseServerOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Server: Options carry safe defaults")]
    public void Options_Defaults_ShouldBeBounded()
    {
        // Arrange
        var options = new KeyValueDatabaseServerOptions();

        // Assert
        options.MaxSessions.ShouldBeGreaterThan(0);
        options.AuthenticationTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        options.IdleTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        options.ShutdownDrainTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Server: Creation rejects a missing listener and a non-positive session limit")]
    public async System.Threading.Tasks.Task Create_WithInvalidOptions_ShouldThrow()
    {
        // Arrange
        await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "kv-opt-validate" });

        // Act / Assert: no listener.
        Should.Throw<ArgumentException>(() => KeyValueDatabaseServer.Create(engine, new KeyValueDatabaseServerOptions()));

        // Act / Assert: non-positive session limit.
        await using var listener = new InMemoryConnectionListener();
        Should.Throw<ArgumentException>(() => KeyValueDatabaseServer.Create(engine, new KeyValueDatabaseServerOptions { Listener = listener, MaxSessions = 0 }));

        // Act / Assert: null engine.
        Should.Throw<ArgumentNullException>(() => KeyValueDatabaseServer.Create(null!, new KeyValueDatabaseServerOptions { Listener = listener }));
    }
}
