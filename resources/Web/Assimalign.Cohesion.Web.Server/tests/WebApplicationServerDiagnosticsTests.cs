using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Internal;
using Assimalign.Cohesion.Web.Server.Tests.TestObjects;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using HttpVersion = Assimalign.Cohesion.Http.HttpVersion;
using HttpMethod = System.Net.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Server.Tests;

/// <summary>
/// The default server reports its failures through its event source with server and connection
/// identities, deliberate levels, and no request or response content.
/// </summary>
[Collection(nameof(TelemetryCollection))]
public class WebApplicationServerDiagnosticsTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web Server] - Server/Diagnostics: Each completion callback failure is reported without skipping later callbacks")]
    public async Task ServeExchange_WhenCompletionCallbacksThrow_ShouldReportEveryFailure()
    {
        // Arrange
        using EventSourceRecorder recorded = new(WebServerEventSource.Log, EventLevel.Verbose);
        InvalidOperationException first = new("first callback");
        AggregateException second = new("second callback", new InvalidOperationException("nested"));
        bool lastRan = false;
        string? requestId = null;
        FakeHttpConnection connection = new(new FakeHttpConnectionContext(new[]
        {
            new FakeHttpContext(request: new FakeHttpRequest("/", string.Empty)),
        }));
        FakePipeline pipeline = new((context, _) =>
        {
            requestId = context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();
            IWebResponseCompletionFeature completion = context.Features.Get<IWebResponseCompletionFeature>()!;
            completion.Register(() => throw first);
            completion.Register(() => ValueTask.FromException(second));
            completion.Register(() => { lastRan = true; return ValueTask.CompletedTask; });
            return Task.CompletedTask;
        });
        WebApplicationServer server = CreateServer(pipeline, new FakeHttpConnectionListener(connection));

        // Act
        try
        {
            await server.StartAsync(CancellationToken.None);
            await connection.Disposed.Task.WaitAsync(_timeout);

            // Assert
            lastRan.ShouldBeTrue();
            EventWrittenEventArgs[] events = ServerEvents(recorded, server.ServerId, 11);
            events.Length.ShouldBe(2);
            events.ShouldAllBe(entry => entry.EventName == "CompletionCallbackFailed" && entry.Level == EventLevel.Error);
            events[0].PayloadNames.ShouldBe(["serverId", "requestId", "routeTemplate", "exceptionType", "exceptionMessage"]);
            events[0].Payload.ShouldBe(new object?[] { server.ServerId, requestId, string.Empty, first.GetType().FullName, first.Message });
            events[1].Payload.ShouldBe(new object?[] { server.ServerId, requestId, string.Empty, second.GetType().FullName, second.Message });
            connection.AbortCount.ShouldBe(0);
            recorded.Events.ShouldNotContain(entry => entry.EventId == 0);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web Server] - Server/Diagnostics: A bind failure is Critical with its cause before startup fails")]
    public async Task StartAsync_WhenBindFails_ShouldReportCriticalWithTheCause()
    {
        // Arrange
        using EventSourceRecorder recorded = new(WebServerEventSource.Log, EventLevel.Verbose);
        InvalidOperationException failure = new("endpoint unavailable");
        FakeHttpConnectionListener listener = new()
        {
            BindHandler = _ => ValueTask.FromException(failure),
        };
        WebApplicationServer server = CreateServer(new FakePipeline(), listener);

        // Act
        try
        {
            await Should.ThrowAsync<InvalidOperationException>(() => server.StartAsync(CancellationToken.None));

            // Assert
            EventWrittenEventArgs entry = ServerEvents(recorded, server.ServerId, 2).ShouldHaveSingleItem();
            entry.EventName.ShouldBe("BindFailed");
            entry.Level.ShouldBe(EventLevel.Critical);
            entry.PayloadNames.ShouldBe(["serverId", "protocols", "exceptionType", "exceptionMessage"]);
            entry.Payload.ShouldBe(new object?[] { server.ServerId, "HTTP/1.1", failure.GetType().FullName, failure.Message });
            ServerEvents(recorded, server.ServerId, 1).ShouldBeEmpty();
            recorded.Events.ShouldNotContain(candidate => candidate.EventId == 0);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web Server] - Server/Diagnostics: An accept-loop fault is Error")]
    public async Task AcceptLoop_WhenTheListenerFaults_ShouldReportError()
    {
        // Arrange
        using EventSourceRecorder recorded = new(WebServerEventSource.Log, EventLevel.Verbose);
        InvalidOperationException failure = new("listener faulted");
        FakeHttpConnectionListener listener = new()
        {
            AcceptHandler = _ => Task.FromException<IHttpConnection>(failure),
        };
        WebApplicationServer server = CreateServer(new FakePipeline(), listener);

        // Act
        try
        {
            await server.StartAsync(CancellationToken.None);
            EventWrittenEventArgs entry = await WaitForEventAsync(recorded, server.ServerId, 3);

            // Assert
            entry.EventName.ShouldBe("AcceptLoopFaulted");
            entry.Level.ShouldBe(EventLevel.Error);
            entry.PayloadNames.ShouldBe(["serverId", "exceptionType", "exceptionMessage"]);
            entry.Payload.ShouldBe(new object?[] { server.ServerId, failure.GetType().FullName, failure.Message });
            ServerEvents(recorded, server.ServerId, 3).ShouldHaveSingleItem();
            recorded.Events.ShouldNotContain(candidate => candidate.EventId == 0);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web Server] - Server/Diagnostics: A cancellation the server did not request is an accept-loop fault")]
    public async Task AcceptLoop_WhenTheListenerFaultsWithACancellation_ShouldReportError()
    {
        // Arrange — the listener's own cancellation is a transport fault, not a requested drain (#1310).
        using EventSourceRecorder recorded = new(WebServerEventSource.Log, EventLevel.Verbose);
        OperationCanceledException failure = new("The transport canceled its own accept.");
        FakeHttpConnectionListener listener = new()
        {
            AcceptHandler = _ => Task.FromException<IHttpConnection>(failure),
        };
        WebApplicationServer server = CreateServer(new FakePipeline(), listener);

        // Act
        try
        {
            await server.StartAsync(CancellationToken.None);
            EventWrittenEventArgs entry = await WaitForEventAsync(recorded, server.ServerId, 3);

            // Assert
            entry.Level.ShouldBe(EventLevel.Error);
            entry.Payload.ShouldBe(new object?[] { server.ServerId, failure.GetType().FullName, failure.Message });
            ServerEvents(recorded, server.ServerId, 3).ShouldHaveSingleItem();
            recorded.Events.ShouldNotContain(candidate => candidate.EventId == 0);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web Server] - Server/Diagnostics: A connection fault is Error with its identity, endpoints and protocol")]
    public async Task ServeConnection_WhenTheResponseCannotBeSent_ShouldReportErrorWithTheConnectionIdentity()
    {
        // Arrange — the HTTP/1.1 response cannot be written because of an application defect.
        using EventSourceRecorder recorded = new(WebServerEventSource.Log, EventLevel.Verbose);
        long faults = WebServerEventSource.Log.ConnectionFaults;
        InvalidOperationException failure = new("response framing failed");
        FakeHttpConnectionContext connectionContext = new(new[] { new FakeHttpContext() })
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 8080),
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("10.0.0.7"), 51000),
            SendHandler = (_, _) => ValueTask.FromException(failure),
        };
        FakeHttpConnection connection = new(connectionContext);
        WebApplicationServer server = CreateServer(new FakePipeline(), new FakeHttpConnectionListener(connection));

        // Act
        try
        {
            await server.StartAsync(CancellationToken.None);
            await connection.Disposed.Task.WaitAsync(_timeout);

            // Assert
            EventWrittenEventArgs entry = ServerEvents(recorded, server.ServerId, 4).ShouldHaveSingleItem();
            entry.EventName.ShouldBe("ConnectionFaulted");
            entry.Level.ShouldBe(EventLevel.Error);
            entry.PayloadNames.ShouldBe(["serverId", "connectionId", "localEndPoint", "remoteEndPoint", "protocolVersion", "exceptionType", "exceptionMessage"]);
            entry.Payload.ShouldBe(new object?[]
            {
                server.ServerId, connection.Id.ToString(), "127.0.0.1:8080", "10.0.0.7:51000", "1.1",
                failure.GetType().FullName, failure.Message,
            });
            (WebServerEventSource.Log.ConnectionFaults - faults).ShouldBe(1);
            connection.AbortCount.ShouldBe(1);
            recorded.Events.ShouldNotContain(candidate => candidate.EventId == 0);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web Server] - Server/Diagnostics: Send and disposal faults count one connection")]
    public async Task ServeConnection_WhenSendAndDisposalFault_ShouldCountTheConnectionOnce()
    {
        // Arrange
        using EventSourceRecorder recorded = new(WebServerEventSource.Log, EventLevel.Verbose);
        long faults = WebServerEventSource.Log.ConnectionFaults;
        InvalidOperationException sendFailure = new("response framing failed");
        InvalidOperationException disposeFailure = new("connection disposal failed");
        FakeHttpConnection connection = new(new FakeHttpConnectionContext(new[] { new FakeHttpContext() })
        {
            SendHandler = (_, _) => ValueTask.FromException(sendFailure),
        });
        DisposeFailureConnection failing = new(connection, disposeFailure);
        WebApplicationServer server = CreateServer(new FakePipeline(), new FakeHttpConnectionListener(failing));

        // Act
        try
        {
            await server.StartAsync(CancellationToken.None);
            await connection.Disposed.Task.WaitAsync(_timeout);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }

        // Assert — both sites report their cause, but the total measures connections rather than exceptions.
        EventWrittenEventArgs[] events = ServerEvents(recorded, server.ServerId, 4);
        events.Length.ShouldBe(2);
        events.ShouldAllBe(entry => Equals(Payload(entry, "connectionId"), connection.Id.ToString()));
        events.Select(entry => Payload(entry, "exceptionMessage")).ShouldBe(new[] { sendFailure.Message, disposeFailure.Message });
        (WebServerEventSource.Log.ConnectionFaults - faults).ShouldBe(1);
        recorded.Events.ShouldNotContain(candidate => candidate.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web Server] - Server/Diagnostics: A peer-ended connection is Verbose and does not count as a connection fault")]
    public async Task ServeConnection_WhenThePeerIsGone_ShouldReportVerboseWithoutCountingAFault()
    {
        // Arrange — the response write fails because the peer went away.
        using EventSourceRecorder recorded = new(WebServerEventSource.Log, EventLevel.Verbose);
        long faults = WebServerEventSource.Log.ConnectionFaults;
        IOException failure = new("connection reset by peer");
        FakeHttpConnectionContext connectionContext = new(new[] { new FakeHttpContext() })
        {
            SendHandler = (_, _) => ValueTask.FromException(failure),
        };
        FakeHttpConnection connection = new(connectionContext);
        WebApplicationServer server = CreateServer(new FakePipeline(), new FakeHttpConnectionListener(connection));

        // Act
        try
        {
            await server.StartAsync(CancellationToken.None);
            await connection.Disposed.Task.WaitAsync(_timeout);

            // Assert
            EventWrittenEventArgs entry = ServerEvents(recorded, server.ServerId, 5).ShouldHaveSingleItem();
            entry.EventName.ShouldBe("ConnectionEndedByPeer");
            entry.Level.ShouldBe(EventLevel.Verbose);
            Payload(entry, "connectionId").ShouldBe(connection.Id.ToString());
            Payload(entry, "exceptionType").ShouldBe(typeof(IOException).FullName);
            Payload(entry, "exceptionMessage").ShouldBe(failure.Message);
            ServerEvents(recorded, server.ServerId, 4).ShouldBeEmpty();
            (WebServerEventSource.Log.ConnectionFaults - faults).ShouldBe(0);
            recorded.Events.ShouldNotContain(candidate => candidate.EventId == 0);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web Server] - Server/Diagnostics: A drain the budget cut short is Warning with the work still in flight")]
    public async Task StopAsync_WhenBudgetRunsOut_ShouldReportWarningWithWhatWasInFlight()
    {
        // Arrange — one HTTP/1.1 exchange and two HTTP/2 streams run until they are cancelled.
        using EventSourceRecorder recorded = new(WebServerEventSource.Log, EventLevel.Verbose);
        int entered = 0;
        TaskCompletionSource allEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHttpConnection sequential = new(new FakeHttpConnectionContext(new[] { new FakeHttpContext() }, parkAfterExchanges: true));
        FakeHttpConnection multiplexed = new(new FakeHttpConnectionContext(
            new[] { new FakeHttpContext(HttpVersion.Http20), new FakeHttpContext(HttpVersion.Http20) },
            parkAfterExchanges: true));
        FakePipeline pipeline = new(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref entered) == 3)
            {
                allEntered.TrySetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        FakeHttpConnectionListener listener = new(sequential, multiplexed);
        WebApplicationServer server = CreateServer(pipeline, listener);
        using CancellationTokenSource budget = new();
        using CancellationTokenSource timeout = new(_timeout);

        // Act
        try
        {
            await server.StartAsync(CancellationToken.None);
            await allEntered.Task.WaitAsync(_timeout);
            // Dispatch enters the pipeline before the accept loop records the task. Its next accept
            // proves both connections have been registered before the stop snapshots them.
            while (listener.AcceptCount != 3)
            {
                await Task.Delay(10, timeout.Token);
            }

            Task stop = server.StopAsync(budget.Token);
            budget.Cancel();
            await stop.WaitAsync(_timeout);

            // Assert
            EventWrittenEventArgs entry = ServerEvents(recorded, server.ServerId, 7).ShouldHaveSingleItem();
            entry.EventName.ShouldBe("DrainTimedOut");
            entry.Level.ShouldBe(EventLevel.Warning);
            entry.PayloadNames.ShouldBe(["serverId", "connections", "exchanges", "elapsedMilliseconds"]);
            Payload(entry, "connections").ShouldBe(2);
            Payload(entry, "exchanges").ShouldBe(3);
            Payload(entry, "elapsedMilliseconds").ShouldBeOfType<double>().ShouldBeGreaterThanOrEqualTo(0);
            ServerEvents(recorded, server.ServerId).ShouldNotContain(candidate => (candidate.Level == EventLevel.Error || candidate.Level == EventLevel.Critical));
            recorded.Events.ShouldNotContain(candidate => candidate.EventId == 0);
        }
        finally
        {
            budget.Cancel();
            await server.StopAsync(budget.Token);
        }
    }


    private static WebApplicationServer CreateServer(IWebApplicationPipeline pipeline, FakeHttpConnectionListener listener)
    {
        return new WebApplicationServer(new WebApplicationServerOptions
        {
            Pipeline = pipeline,

        }, listener);
    }

    private static EventWrittenEventArgs[] ServerEvents(EventSourceRecorder recorded, long serverId, int? eventId = null)
        => recorded.Events.Where(entry =>
            (!eventId.HasValue || entry.EventId == eventId.Value) &&
            entry.PayloadNames is { Count: > 0 } && entry.PayloadNames[0] == "serverId" &&
            entry.Payload is { Count: > 0 } && Equals(entry.Payload[0], serverId)).ToArray();

    private static object? Payload(EventWrittenEventArgs entry, string name)
        => entry.Payload![entry.PayloadNames!.IndexOf(name)];

    private static async Task<EventWrittenEventArgs> WaitForEventAsync(EventSourceRecorder recorded, long serverId, int eventId)
    {
        using CancellationTokenSource timeout = new(_timeout);

        while (true)
        {
            EventWrittenEventArgs? entry = ServerEvents(recorded, serverId, eventId).FirstOrDefault();

            if (entry is not null)
            {
                return entry;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class DisposeFailureConnection : IHttpConnection
    {
        private readonly FakeHttpConnection _connection;
        private readonly Exception _failure;

        public DisposeFailureConnection(FakeHttpConnection connection, Exception failure)
        {
            _connection = connection;
            _failure = failure;
        }

        public ConnectionId Id => _connection.Id;

        public HttpProtocol Protocol => _connection.Protocol;

        public EndPoint? LocalEndPoint => _connection.LocalEndPoint;

        public EndPoint? RemoteEndPoint => _connection.RemoteEndPoint;

        public ConnectionCapabilities Capabilities => _connection.Capabilities;

        public IConnectionFeatureCollection Features => _connection.Features;

        public ConnectionState State => _connection.State;

        public CancellationToken ConnectionClosed => _connection.ConnectionClosed;

        public void Abort(Exception? reason = null) => _connection.Abort(reason);

        public IHttpConnectionContext Open() => _connection.Open();

        public ValueTask<IHttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
            => _connection.OpenAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            throw _failure;
        }
    }
}
