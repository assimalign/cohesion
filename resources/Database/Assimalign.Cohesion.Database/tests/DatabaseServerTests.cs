using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The root <see cref="DatabaseServer"/> and <see cref="DatabaseServerSession"/> bases
/// (concrete-types plan rows 9 and 11, phase 3, #1259): the server's one lifecycle state machine
/// the four model servers each carried, and the server session's identity, handshake values and
/// disposal.
/// </summary>
public class DatabaseServerTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Server: the engine is the constructor's and is required")]
    public void Constructor_Engine_ShouldBeFixedAndRequired()
    {
        // Arrange
        var engine = new TestEngine();

        // Act
        var server = new TestServer(engine);
        IDatabaseServer bridged = server;

        // Assert
        server.Engine.ShouldBeSameAs(engine);
        bridged.Context.Engine.ShouldBeSameAs(engine);
        server.Running.ShouldBeFalse();
        Should.Throw<ArgumentNullException>(() => new TestServer(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Server: a start starts once, and a second start while running returns")]
    public async Task StartAsync_Twice_ShouldStartOnce()
    {
        // Arrange
        var server = new TestServer(new TestEngine());

        // Act
        await server.StartAsync();
        await server.StartAsync();

        // Assert
        server.Starts.ShouldBe(1);
        server.Running.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Server: a stop is terminal and idempotent, and a start after it is refused")]
    public async Task StopAsync_AfterStart_ShouldBeTerminal()
    {
        // Arrange
        var server = new TestServer(new TestEngine());
        await server.StartAsync();

        // Act
        await server.StopAsync();
        await server.StopAsync();
        await server.DisposeAsync();

        // Assert
        server.Stops.ShouldBe(1);
        server.Running.ShouldBeFalse();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await server.StartAsync());
        server.Starts.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Server: a server that never started still stops, so the leaf releases what it owns")]
    public async Task StopAsync_NeverStarted_ShouldCallTheStopCore()
    {
        // Arrange
        var server = new TestServer(new TestEngine());

        // Act
        await server.DisposeAsync();

        // Assert
        server.Stops.ShouldBe(1);
        server.Starts.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Server: a failed start leaves the server stopped for good")]
    public async Task StartAsync_Fails_ShouldBeTerminal()
    {
        // Arrange
        var failure = new InvalidOperationException("bind failed");
        var server = new TestServer(new TestEngine()) { StartFailure = failure };

        // Act
        var error = await Should.ThrowAsync<InvalidOperationException>(async () => await server.StartAsync());
        server.StartFailure = null;
        await Should.ThrowAsync<ObjectDisposedException>(async () => await server.StartAsync());
        await server.StopAsync();

        // Assert
        error.ShouldBeSameAs(failure);
        server.Starts.ShouldBe(1);
        server.Stops.ShouldBe(0);
        server.Running.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Server: a canceled start never reaches the core and leaves the server startable")]
    public async Task StartAsync_Canceled_ShouldNotCallTheCore()
    {
        // Arrange
        var server = new TestServer(new TestEngine());
        using var source = new CancellationTokenSource();
        source.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await server.StartAsync(source.Token));
        await server.StartAsync();

        // Assert
        server.Starts.ShouldBe(1);
        server.Running.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Server session: the identity is fixed and the handshake values are set once")]
    public void ServerSession_HandshakeValues_ShouldBeSetOnce()
    {
        // Arrange
        var session = new TestServerSession();
        var other = new TestServerSession();
        IDatabaseServerSession bridged = session;
        var id = session.Id;

        // Act
        var versionBefore = session.ProtocolVersion;
        var principalBefore = session.Principal;
        session.Negotiate(new ProtocolVersion(1, 0));
        session.Authenticate("svc-user");

        // Assert
        id.ShouldNotBe(Guid.Empty);
        session.Id.ShouldBe(id);
        other.Id.ShouldNotBe(id);
        versionBefore.ShouldBe(default(ProtocolVersion));
        principalBefore.ShouldBeNull();
        session.ProtocolVersion.ShouldBe(new ProtocolVersion(1, 0));
        session.Principal.ShouldBe("svc-user");
        bridged.Principal.ShouldBe("svc-user");
        bridged.ProtocolVersion.ShouldBe(new ProtocolVersion(1, 0));
        Should.Throw<InvalidOperationException>(() => session.Negotiate(new ProtocolVersion(1, 0)));
        Should.Throw<InvalidOperationException>(() => session.Authenticate("other-user"));
        Should.Throw<ArgumentNullException>(() => other.Authenticate(null!));
        session.Principal.ShouldBe("svc-user");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Server session: the engine session is the leaf's, through the interface too, and disposal reaches the core")]
    public async Task ServerSession_EngineSessionAndDisposal_ShouldBeTheLeafs()
    {
        // Arrange
        var engineSession = new TestSession(new TestDatabase("appdb", new TestEngine()));
        var session = new TestServerSession();
        IDatabaseServerSession bridged = session;
        var before = bridged.DatabaseSession;

        // Act
        session.Session = engineSession;
        await session.DisposeAsync();

        // Assert
        before.ShouldBeNull();
        session.DatabaseSession.ShouldBeSameAs(engineSession);
        bridged.DatabaseSession.ShouldBeSameAs(engineSession);
        session.DisposeCores.ShouldBe(1);
    }
}
