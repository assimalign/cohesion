using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client.Internal;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>
/// Serializes the tests that observe the client event source: the source and its counters are
/// process-wide.
/// </summary>
[CollectionDefinition(nameof(DatabaseClientEventSourceCollection), DisableParallelization = true)]
public class DatabaseClientEventSourceCollection
{
}

/// <summary>
/// The shared client core's event source against the repository's EventSource convention: a
/// loopback client against a real SQL server reports each connection and pool transition once,
/// a dead endpoint, a refused authentication, a protocol violation and a download whose broken
/// rental cannot be returned each report their failure once, and the connection gauges return to
/// where they started.
/// </summary>
[Collection(nameof(DatabaseClientEventSourceCollection))]
public sealed class DatabaseClientEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Client] - DatabaseClientEventSource: Should be named for its assembly")]
    public void GetName_DatabaseClientEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(DatabaseClientEventSource));

        // Assert
        name.ShouldBe(typeof(DatabaseClientEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Client");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - DatabaseClientEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(DatabaseClientEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Client", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - DatabaseClientEventSource: Should report open, rent, return and close once each and restore the gauges")]
    public async Task ConnectionLifecycle_LoopbackServer_ShouldReportEachTransitionOnceAndRestoreGauges()
    {
        // Arrange
        string database = UniqueDatabase();
        await using var server = await LoopbackServer.StartAsync(database);
        using var recorder = new EventSourceRecorder(DatabaseClientEventSource.Log, EventLevel.Verbose);
        long connectionsBefore = DatabaseClientEventSource.Log.CurrentConnections;
        long rentedBefore = DatabaseClientEventSource.Log.CurrentRentedConnections;
        long openedBefore = DatabaseClientEventSource.Log.TotalConnectionsOpened;
        var client = server.CreateClient();
        long connectionsWhileOpen;
        long rentedWhileRented;
        DatabaseClientException failure;

        // Act: open and rent, run a statement, return; rent the pooled session again, run a failing
        // statement the connection survives, return; dispose the client, which closes the session.
        await using (DatabaseConnection first = await client.RentAsync(Timeout()))
        {
            await first.ExecuteAsync("SELECT id FROM users", cancellationToken: Timeout());
            connectionsWhileOpen = DatabaseClientEventSource.Log.CurrentConnections;
            rentedWhileRented = DatabaseClientEventSource.Log.CurrentRentedConnections;
        }

        long rentedAfterReturn = DatabaseClientEventSource.Log.CurrentRentedConnections;
        await using (DatabaseConnection second = await client.RentAsync(Timeout()))
        {
            failure = await Should.ThrowAsync<DatabaseClientException>(async () =>
                await second.ExecuteAsync("SELECT id FROM missing_table", cancellationToken: Timeout()));
            second.IsOpen.ShouldBeTrue();
        }

        await client.DisposeAsync();

        // Assert: the gauges
        connectionsWhileOpen.ShouldBe(connectionsBefore + 1);
        rentedWhileRented.ShouldBe(rentedBefore + 1);
        rentedAfterReturn.ShouldBe(rentedBefore);
        DatabaseClientEventSource.Log.CurrentConnections.ShouldBe(connectionsBefore);
        DatabaseClientEventSource.Log.CurrentRentedConnections.ShouldBe(rentedBefore);
        DatabaseClientEventSource.Log.TotalConnectionsOpened.ShouldBe(openedBefore + 1);

        // Assert: the events, in order
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], database)).ToArray();
        events.Select(e => e.EventName).ShouldBe(
        [
            "ConnectionOpened",
            "ConnectionRented",
            "ConnectionReturned",
            "ConnectionRented",
            "ExchangeFailed",
            "ConnectionReturned",
            "ConnectionClosed",
        ]);

        var opened = events[0];
        opened.EventId.ShouldBe(1);
        opened.Level.ShouldBe(EventLevel.Informational);
        opened.PayloadNames.ShouldBe(["database", "endPoint", "serverVersion", "durationMilliseconds"]);
        opened.Payload![1].ShouldBe(server.Listener.EndPoint.ToString());
        opened.Payload[2].ShouldBe(ProtocolVersion.Current.ToString());
        ((double)opened.Payload[3]!).ShouldBeGreaterThan(0d);

        var rentedNew = events[1];
        rentedNew.EventId.ShouldBe(5);
        rentedNew.Level.ShouldBe(EventLevel.Verbose);
        (rentedNew.Keywords & DatabaseClientEventSource.Keywords.Pool).ShouldBe(DatabaseClientEventSource.Keywords.Pool);
        rentedNew.PayloadNames.ShouldBe(["database", "reused", "waitedMilliseconds"]);
        rentedNew.Payload![1].ShouldBe(false);
        events[3].Payload![1].ShouldBe(true);

        var returned = events[2];
        returned.EventId.ShouldBe(6);
        returned.PayloadNames.ShouldBe(["database", "pooled"]);
        returned.Payload![1].ShouldBe(true);

        var exchangeFailed = events[4];
        exchangeFailed.EventId.ShouldBe(7);
        exchangeFailed.Level.ShouldBe(EventLevel.Verbose);
        exchangeFailed.PayloadNames.ShouldBe(["database", "code", "exceptionMessage"]);
        failure.Code.ShouldNotBe(ProtocolErrorCode.Internal);
        failure.Code.ShouldNotBe(ProtocolErrorCode.ProtocolViolation);
        failure.Message.ShouldContain("missing_table", Case.Sensitive);
        exchangeFailed.Payload.ShouldBe([database, failure.Code.ToString(), string.Empty],
            "The server's statement-level message can quote the statement; the core does not write it.");

        var closed = events[6];
        closed.EventId.ShouldBe(3);
        closed.Level.ShouldBe(EventLevel.Informational);
        closed.PayloadNames.ShouldBe(["database", "endPoint"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - DatabaseClientEventSource: Should report a dial to a dead endpoint once as a failed open")]
    public async Task RentAsync_DeadEndPoint_ShouldReportConnectionOpenFailedOnce()
    {
        // Arrange
        string database = UniqueDatabase();
        using var unreachable = new UnreachableEndPoint();
        await using var client = DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = database, Principal = "tester", EndPoint = unreachable.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = new TcpConnectionFactory(),
            Family = SqlProtocol.Family,
        });
        using var recorder = new EventSourceRecorder(DatabaseClientEventSource.Log, EventLevel.Verbose);
        long failuresBefore = DatabaseClientEventSource.Log.TotalConnectionFailures;
        long connectionsBefore = DatabaseClientEventSource.Log.CurrentConnections;

        // Act
        var exception = await Should.ThrowAsync<DatabaseClientException>(async () => await client.RentAsync(Timeout()));

        // Assert
        exception.Code.ShouldBe(ProtocolErrorCode.ConnectionFailure);
        DatabaseClientEventSource.Log.TotalConnectionFailures.ShouldBe(failuresBefore + 1);
        DatabaseClientEventSource.Log.CurrentConnections.ShouldBe(connectionsBefore);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var failed = recorder.Events.Where(e => Equals(e.Payload?[0], database)).ShouldHaveSingleItem();
        failed.EventName.ShouldBe("ConnectionOpenFailed");
        failed.EventId.ShouldBe(2);
        failed.Level.ShouldBe(EventLevel.Error);
        failed.PayloadNames.ShouldBe(["database", "endPoint", "code", "exceptionMessage", "durationMilliseconds"]);
        failed.Payload![1].ShouldBe(unreachable.EndPoint.ToString());
        failed.Payload[2].ShouldBe(nameof(ProtocolErrorCode.ConnectionFailure));
        failed.Payload[3].ShouldBe(exception.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - DatabaseClientEventSource: Should report a refused authentication once as a failed open, not a broken connection")]
    public async Task RentAsync_AuthenticationRejected_ShouldReportConnectionOpenFailedOnce()
    {
        // Arrange
        string database = UniqueDatabase();
        await using var server = await LoopbackServer.StartAsync(database, new RejectingAuthenticator());
        await using var client = server.CreateClient();
        using var recorder = new EventSourceRecorder(DatabaseClientEventSource.Log, EventLevel.Verbose);
        long connectionsBefore = DatabaseClientEventSource.Log.CurrentConnections;

        // Act
        var exception = await Should.ThrowAsync<DatabaseClientException>(async () => await client.RentAsync(Timeout()));

        // Assert
        exception.Code.ShouldBe(ProtocolErrorCode.AuthenticationFailed);
        DatabaseClientEventSource.Log.CurrentConnections.ShouldBe(connectionsBefore);
        var failed = recorder.Events.Where(e => Equals(e.Payload?[0], database)).ShouldHaveSingleItem();
        failed.EventName.ShouldBe("ConnectionOpenFailed");
        failed.Payload![2].ShouldBe(nameof(ProtocolErrorCode.AuthenticationFailed));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - DatabaseClientEventSource: Should report a protocol violation once as a broken connection, then its close")]
    public async Task ExecuteAsync_ProtocolViolation_ShouldReportConnectionBrokenOnce()
    {
        // Arrange: a peer that completes the handshake, then answers the statement with the
        // client-local ConnectionFailure code no conforming server sends.
        string database = UniqueDatabase();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = database, Principal = "tester", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = listener.CreateFactory(),
            Family = SqlProtocol.Family,
        });
        Task peer = AnswerStatementWithConnectionFailureAsync(listener, timeout.Token);
        using var recorder = new EventSourceRecorder(DatabaseClientEventSource.Log, EventLevel.Verbose);
        long connectionsBefore = DatabaseClientEventSource.Log.CurrentConnections;
        DatabaseClientException exception;

        // Act
        await using (DatabaseConnection connection = await client.RentAsync(timeout.Token))
        {
            exception = await Should.ThrowAsync<DatabaseClientException>(async () =>
                await connection.ExecuteAsync("SELECT 1", cancellationToken: timeout.Token));
        }

        await client.DisposeAsync();
        await peer;

        // Assert
        exception.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        DatabaseClientEventSource.Log.CurrentConnections.ShouldBe(connectionsBefore);
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], database)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["ConnectionOpened", "ConnectionRented", "ConnectionBroken", "ConnectionReturned", "ConnectionClosed"]);

        var broken = events[2];
        broken.EventId.ShouldBe(4);
        broken.Level.ShouldBe(EventLevel.Warning);
        broken.PayloadNames.ShouldBe(["database", "code", "exceptionMessage"]);
        broken.Payload.ShouldBe([database, nameof(ProtocolErrorCode.ProtocolViolation), exception.Message]);
        events[3].Payload![1].ShouldBe(false, "A broken connection closes instead of returning to the pool.");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - DatabaseClientEventSource: Should report a failed download's release failure once, from the download stream")]
    public async Task ExecuteStreamingAsync_ReleaseFails_ShouldReportDownloadReleaseFailedOnce()
    {
        // Arrange: the scripted peer refuses the transfer before its metadata, and the transport's
        // own disposal throws, so returning the broken rental fails inside the catch of the download
        // stream that swallows it.
        string database = UniqueDatabase();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness("header-error",
            factory => new DisposeFailingConnectionFactory(factory), database);
        using var recorder = new EventSourceRecorder(DatabaseClientEventSource.Log, EventLevel.Verbose);
        long connectionsBefore = DatabaseClientEventSource.Log.CurrentConnections;
        long rentedBefore = DatabaseClientEventSource.Log.CurrentRentedConnections;

        // Act
        var exception = await Should.ThrowAsync<DatabaseClientException>(async () =>
            await harness.Client.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token));

        // Assert: the caller sees the exchange's failure, and the gauges are back where they started.
        exception.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        exception.Message.ShouldNotBeNullOrEmpty();
        DatabaseClientEventSource.Log.CurrentConnections.ShouldBe(connectionsBefore);
        DatabaseClientEventSource.Log.CurrentRentedConnections.ShouldBe(rentedBefore);

        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], database)).ToArray();
        events.Select(e => e.EventName).ShouldBe(
        [
            "ConnectionOpened",
            "ConnectionRented",
            "ExchangeFailed",
            "ConnectionReturned",
            "ConnectionClosed",
            "DownloadReleaseFailed",
        ]);

        // A statement-level server error's message is the model client's to write, not the core's.
        events[2].Payload.ShouldBe([database, nameof(ProtocolErrorCode.ExecutionFailure), string.Empty]);

        var released = events[5];
        released.EventId.ShouldBe(8);
        released.Level.ShouldBe(EventLevel.Warning);
        released.PayloadNames.ShouldBe(["database", "exceptionType", "exceptionMessage"]);
        released.Payload.ShouldBe([database, typeof(IOException).FullName, DisposeFailingConnection.FailureMessage]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - DatabaseClientEventSource: Should publish its connection counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishConnectionCounters()
    {
        // Arrange
        string[] counters = ["current-connections", "current-rented-connections", "connections-opened-per-second", "total-connection-failures"];

        // Act
        using var recorder = new EventSourceRecorder(DatabaseClientEventSource.Log, EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(counters, Timeout());
    }

    private static string UniqueDatabase() => "evt" + Guid.NewGuid().ToString("N");

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static async Task AnswerStatementWithConnectionFailureAsync(InMemoryConnectionListener listener, CancellationToken cancellationToken)
    {
        await using Connection transport = await listener.AcceptAsync(cancellationToken);
        await using var channel = new ProtocolChannel(transport.AsStream(), SqlProtocol.Family);
        (await ReadAsync(channel.Reader, cancellationToken)).Type.ShouldBe(ProtocolMessageType.Startup);
        await WriteAsync(channel.Writer, ProtocolMessageType.Authenticate, ReadOnlyMemory<byte>.Empty, cancellationToken);
        (await ReadAsync(channel.Reader, cancellationToken)).Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
        await WriteAsync(channel.Writer, ProtocolMessageType.Ready, ReadOnlyMemory<byte>.Empty, cancellationToken);
        (await ReadAsync(channel.Reader, cancellationToken)).Type.ShouldBe((ProtocolMessageType)SqlProtocolMessageType.Execute);
        await WriteAsync(channel.Writer, ProtocolMessageType.Error,
            new ProtocolErrorMessage(ProtocolErrorCode.ConnectionFailure, "Spoofed dial failure.").Encode(), cancellationToken);
        try
        {
            while (await channel.Reader.ReadFrameAsync(cancellationToken) is not null)
            {
            }
        }
        catch (Exception exception) when (exception is IOException or ConnectionAbortedException or ConnectionResetException)
        {
            // The client closed the connection it found broken.
        }
    }

    private static async ValueTask<ProtocolFrame> ReadAsync(ProtocolFrameReader reader, CancellationToken cancellationToken)
        => await reader.ReadFrameAsync(cancellationToken) ?? throw new ProtocolException("The scripted peer's client closed unexpectedly.");

    private static async ValueTask WriteAsync(ProtocolFrameWriter writer, ProtocolMessageType type,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await writer.WriteFrameAsync(new ProtocolFrame(type, payload), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Dials through another factory and returns connections whose disposal throws.
    /// </summary>
    private sealed class DisposeFailingConnectionFactory : IConnectionFactory
    {
        private readonly IConnectionFactory _inner;

        public DisposeFailingConnectionFactory(IConnectionFactory inner)
        {
            _inner = inner;
        }

        public ConnectionCapabilities Capabilities => _inner.Capabilities;

        public async ValueTask<IConnection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
            => new DisposeFailingConnection(await _inner.ConnectAsync(endPoint, cancellationToken));
    }

    /// <summary>
    /// A connection that closes its transport on disposal and then throws, the way a transport whose
    /// teardown fails does.
    /// </summary>
    private sealed class DisposeFailingConnection : IConnection
    {
        public const string FailureMessage = "The transport could not close.";

        private readonly IConnection _inner;

        public DisposeFailingConnection(IConnection inner)
        {
            _inner = inner;
        }

        public PipeReader Input => _inner.Input;
        public PipeWriter Output => _inner.Output;
        public ConnectionId Id => _inner.Id;
        public EndPoint? LocalEndPoint => _inner.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
        public ConnectionDirection Direction => _inner.Direction;
        public ConnectionCapabilities Capabilities => _inner.Capabilities;
        public ConnectionState State => _inner.State;
        public CancellationToken ConnectionClosed => _inner.ConnectionClosed;

        public void Abort(Exception? reason = null) => _inner.Abort(reason);

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            throw new IOException(FailureMessage);
        }
    }

    /// <summary>
    /// An authenticator that rejects every principal.
    /// </summary>
    private sealed class RejectingAuthenticator : DatabaseAuthenticator
    {
        protected override ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
            => ValueTask.FromResult(false);
    }

    /// <summary>
    /// A live SQL engine with one database and a running server on an in-memory listener.
    /// </summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly SqlDatabaseEngine _engine;
        private readonly SqlDatabaseServer _server;
        private readonly string _database;

        private LoopbackServer(SqlDatabaseEngine engine, InMemoryConnectionListener listener, SqlDatabaseServer server, string database)
        {
            _engine = engine;
            Listener = listener;
            _server = server;
            _database = database;
        }

        public InMemoryConnectionListener Listener { get; }

        public static async Task<LoopbackServer> StartAsync(string database, DatabaseAuthenticator? authenticator = null)
        {
            var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "client-event-source" });
            var instance = await engine.CreateDatabaseAsync(database);
            await using (var session = await instance.CreateSessionAsync())
            {
                await session.ExecuteAsync("CREATE TABLE users (id INT NOT NULL)");
                await session.ExecuteAsync("INSERT INTO users (id) VALUES (1)");
            }

            var listener = new InMemoryConnectionListener();
            var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener, Authenticator = authenticator });
            await server.StartAsync();
            return new LoopbackServer(engine, listener, server, database);
        }

        public DatabaseClient CreateClient()
            => DatabaseClient.Create(new DatabaseClientOptions
            {
                Settings = new DatabaseConnectionSettings { Database = _database, Principal = "tester", EndPoint = Listener.EndPoint, MaxPoolSize = 1 },
                ConnectionFactory = Listener.CreateFactory(),
                Family = SqlProtocol.Family,
            });

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            await Listener.DisposeAsync();
            await _engine.DisposeAsync();
        }
    }
}
