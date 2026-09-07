using System;
using System.Net;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Core;

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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Server: Resource endpoint binding configures a TCP listener")]
    public async System.Threading.Tasks.Task Listen_WithResourceEndpoint_ShouldConfigureTcpListener()
    {
        var options = new SqlDatabaseServerOptions();
        var endpoint = new EndpointAddress("cohesion-db", "127.0.0.1", 5740);

        SqlDatabaseServerOptions result = options.Listen(endpoint);

        result.ShouldBeSameAs(options);
        result.Listener.ShouldNotBeNull();
        result.Listener.EndPoint.ShouldBe(new IPEndPoint(IPAddress.Loopback, 5740));
        await result.Listener.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Server: Resource endpoint binding rejects a default address")]
    public void Listen_WithDefaultResourceEndpoint_ShouldRejectAddress()
    {
        var options = new SqlDatabaseServerOptions();

        Should.Throw<ArgumentException>(() => options.Listen(default));
    }
}
