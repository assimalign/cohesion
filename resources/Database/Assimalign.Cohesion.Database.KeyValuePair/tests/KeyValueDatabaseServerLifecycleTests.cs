using System;
using System.Net;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public class KeyValueDatabaseServerLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - StartAsync: Should wait for listener bind")]
    public async Task StartAsync_WhileListenerBindIsPending_ShouldWaitForBind()
    {
        // Arrange
        await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "server-bind-gate" });
        var listener = new ControlledConnectionListener();
        await using var server = KeyValueDatabaseServer.Create(engine, new KeyValueDatabaseServerOptions { Listener = listener });

        // Act
        Task startTask = server.StartAsync(TestTimeout.Token());
        await listener.BindStarted.WaitAsync(TestTimeout.Token());

        try
        {
            // Assert
            startTask.IsCompleted.ShouldBeFalse();
            listener.AcceptCount.ShouldBe(0);
        }
        finally
        {
            listener.CompleteBind();
        }

        await startTask.WaitAsync(TestTimeout.Token());
        listener.AcceptCount.ShouldBeGreaterThan(0);
        await server.StopAsync(TestTimeout.Token());
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - StartAsync: Bind failure should propagate and release listener")]
    public async Task StartAsync_WhenListenerBindFails_ShouldPropagateAndReleaseListener()
    {
        // Arrange
        await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "server-bind-failure" });
        var listener = new ControlledConnectionListener();
        await using var server = KeyValueDatabaseServer.Create(engine, new KeyValueDatabaseServerOptions { Listener = listener });
        var expected = new InvalidOperationException("Bind failed.");

        // Act
        Task startTask = server.StartAsync(TestTimeout.Token());
        await listener.BindStarted.WaitAsync(TestTimeout.Token());
        listener.CompleteBind(expected);
        InvalidOperationException actual = await Should.ThrowAsync<InvalidOperationException>(async () => await startTask);

        // Assert
        actual.ShouldBeSameAs(expected);
        listener.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - StopAsync: Fresh server should bind the stopped server's TCP port")]
    public async Task StopAsync_WithBoundTcpListener_ShouldAllowFreshServerToBindSamePort()
    {
        // Arrange
        await using var firstEngine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "server-port-first" });
        TcpConnectionListener firstListener = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var firstServer = KeyValueDatabaseServer.Create(
            firstEngine,
            new KeyValueDatabaseServerOptions { Listener = firstListener });

        await firstServer.StartAsync(TestTimeout.Token());
        IPEndPoint fixedEndPoint = firstListener.EndPoint.ShouldBeOfType<IPEndPoint>();

        // Act
        await firstServer.StopAsync(TestTimeout.Token());

        await using var secondEngine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "server-port-second" });
        TcpConnectionListener secondListener = TcpConnectionListener.Create(
            options => options.EndPoint = fixedEndPoint);
        await using var secondServer = KeyValueDatabaseServer.Create(
            secondEngine,
            new KeyValueDatabaseServerOptions { Listener = secondListener });
        await secondServer.StartAsync(TestTimeout.Token());

        // Assert
        secondListener.EndPoint.ShouldBe(fixedEndPoint);
        await secondServer.StopAsync(TestTimeout.Token());
    }
}
