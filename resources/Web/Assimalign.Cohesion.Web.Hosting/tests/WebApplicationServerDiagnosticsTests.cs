using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using HttpVersion = Assimalign.Cohesion.Http.HttpVersion;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// The default server's own diagnostics (#147): a bind failure, an accept-loop fault, a connection
/// fault, and a drain the stop's budget cut short are each logged once, at a deliberate level, under
/// the server's category, and never with request or response content.
/// </summary>
public class WebApplicationServerDiagnosticsTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server/Diagnostics: A bind failure is logged as Critical with its cause before startup fails")]
    public async Task StartAsync_WhenBindFails_ShouldLogCriticalWithTheCause()
    {
        // Arrange
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = CreateLoggerFactory(recorded);
        InvalidOperationException failure = new("endpoint unavailable");
        FakeHttpConnectionListener listener = new()
        {
            BindHandler = _ => ValueTask.FromException(failure),
        };
        WebApplicationServer server = CreateServer(new FakePipeline(), listener, loggerFactory);

        // Act
        await Should.ThrowAsync<HostStartupException>(() => server.StartAsync());

        // Assert
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Critical);
        entry.Category.ShouldBe(WebApplicationServerLog.Category);
        entry.Exception.ShouldBeSameAs(failure);
        entry.Attributes[WebApplicationServerLog.ListenerProtocolsAttribute].ShouldBe("HTTP/1.1");

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server/Diagnostics: An accept-loop fault is logged as Critical")]
    public async Task AcceptLoop_WhenTheListenerFaults_ShouldLogCritical()
    {
        // Arrange
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = CreateLoggerFactory(recorded);
        InvalidOperationException failure = new("listener faulted");
        FakeHttpConnectionListener listener = new()
        {
            AcceptHandler = _ => Task.FromException<IHttpConnection>(failure),
        };
        WebApplicationServer server = CreateServer(new FakePipeline(), listener, loggerFactory);

        // Act
        await server.StartAsync();
        ILoggerEntry entry = await WaitForEntryAsync(recorded, LogLevel.Critical);

        // Assert
        entry.Category.ShouldBe(WebApplicationServerLog.Category);
        entry.Exception.ShouldBeSameAs(failure);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server/Diagnostics: A cancellation the server did not request is logged as an accept-loop fault")]
    public async Task AcceptLoop_WhenTheListenerFaultsWithACancellation_ShouldLogCritical()
    {
        // Arrange — the listener reports a transport fault whose exception is a cancellation the server never
        // requested (#1310). Before the fix the accept loop read it as the drain and ended without a log.
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = CreateLoggerFactory(recorded);
        OperationCanceledException failure = new("The transport canceled its own accept.");
        FakeHttpConnectionListener listener = new()
        {
            AcceptHandler = _ => Task.FromException<IHttpConnection>(failure),
        };
        WebApplicationServer server = CreateServer(new FakePipeline(), listener, loggerFactory);

        // Act
        await server.StartAsync();
        ILoggerEntry entry = await WaitForEntryAsync(recorded, LogLevel.Critical);

        // Assert
        entry.Category.ShouldBe(WebApplicationServerLog.Category);
        entry.Exception.ShouldBeSameAs(failure);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server/Diagnostics: A connection fault is logged as Error with the connection's id, endpoints and protocol only")]
    public async Task ServeConnection_WhenTheResponseCannotBeSent_ShouldLogErrorWithTheConnectionIdentity()
    {
        // Arrange — the HTTP/1.1 response cannot be written, an unexpected failure.
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = CreateLoggerFactory(recorded);
        InvalidOperationException failure = new("response framing failed");
        FakeHttpConnectionContext connectionContext = new(new[] { new FakeHttpContext() })
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 8080),
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("10.0.0.7"), 51000),
            SendHandler = (_, _) => ValueTask.FromException(failure),
        };
        FakeHttpConnection connection = new(connectionContext);
        WebApplicationServer server = CreateServer(new FakePipeline(), new FakeHttpConnectionListener(connection), loggerFactory);

        // Act
        await server.StartAsync();
        await connection.Disposed.Task.WaitAsync(_timeout);

        // Assert — one Error entry, identifying the connection by nothing but id, endpoints, and protocol.
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Category.ShouldBe(WebApplicationServerLog.Category);
        entry.Exception.ShouldBeSameAs(failure);
        entry.Attributes.Keys.OrderBy(key => key, StringComparer.Ordinal).ShouldBe(new[]
        {
            WebApplicationServerLog.ConnectionIdAttribute,
            WebApplicationServerLog.LocalAddressAttribute,
            WebApplicationServerLog.LocalPortAttribute,
            WebApplicationServerLog.PeerAddressAttribute,
            WebApplicationServerLog.PeerPortAttribute,
            WebApplicationServerLog.ProtocolVersionAttribute,
        }.OrderBy(key => key, StringComparer.Ordinal));
        entry.Attributes[WebApplicationServerLog.ConnectionIdAttribute].ShouldBe(connection.Id.ToString());
        entry.Attributes[WebApplicationServerLog.LocalAddressAttribute].ShouldBe("127.0.0.1");
        entry.Attributes[WebApplicationServerLog.LocalPortAttribute].ShouldBe(8080);
        entry.Attributes[WebApplicationServerLog.PeerAddressAttribute].ShouldBe("10.0.0.7");
        entry.Attributes[WebApplicationServerLog.PeerPortAttribute].ShouldBe(51000);
        entry.Attributes[WebApplicationServerLog.ProtocolVersionAttribute].ShouldBe("1.1");
        connection.AbortCount.ShouldBe(1);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server/Diagnostics: A connection the peer or the network ended is logged at Debug only")]
    public async Task ServeConnection_WhenThePeerIsGone_ShouldLogAtDebug()
    {
        // Arrange — the response write fails because the peer went away.
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = CreateLoggerFactory(recorded);
        FakeHttpConnectionContext connectionContext = new(new[] { new FakeHttpContext() })
        {
            SendHandler = (_, _) => ValueTask.FromException(new IOException("connection reset by peer")),
        };
        FakeHttpConnection connection = new(connectionContext);
        WebApplicationServer server = CreateServer(new FakePipeline(), new FakeHttpConnectionListener(connection), loggerFactory);

        // Act
        await server.StartAsync();
        await connection.Disposed.Task.WaitAsync(_timeout);

        // Assert
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Debug);
        entry.Exception.ShouldBeOfType<IOException>();

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server/Diagnostics: A drain the budget cut short is logged as Warning with the connections and exchanges still in flight")]
    public async Task StopAsync_WhenBudgetRunsOut_ShouldLogWarningWithWhatWasInFlight()
    {
        // Arrange — one HTTP/1.1 exchange and two HTTP/2 streams on a second connection, all running
        // until they are cancelled.
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = CreateLoggerFactory(recorded);
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
        WebApplicationServer server = CreateServer(pipeline, new FakeHttpConnectionListener(sequential, multiplexed), loggerFactory);

        await server.StartAsync();
        await allEntered.Task.WaitAsync(_timeout);

        using CancellationTokenSource budget = new();
        Task stop = server.StopAsync(budget.Token);

        // Act
        budget.Cancel();
        await stop.WaitAsync(_timeout);

        // Assert
        ILoggerEntry entry = recorded.Entries.Where(entry => entry.Level == LogLevel.Warning).ShouldHaveSingleItem();
        entry.Category.ShouldBe(WebApplicationServerLog.Category);
        entry.Attributes[WebApplicationServerLog.DrainConnectionsAttribute].ShouldBe(2);
        entry.Attributes[WebApplicationServerLog.DrainExchangesAttribute].ShouldBe(3);
        entry.Attributes[WebApplicationServerLog.DrainDurationAttribute].ShouldBeOfType<TimeSpan>();
        recorded.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Error);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server/Diagnostics: A connection fault on a real exchange is logged without its header values or bodies")]
    public async Task ServeConnection_WhenARealResponseFails_ShouldNotLogHeaderValuesOrBodies()
    {
        // Arrange — the handler hands the server a response body that fails when it is read, so the
        // HTTP/1.1 response cannot be framed and the connection faults.
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        RecordingLoggerProvider recorded = new();

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Logging.AddProvider(recorded);
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            context.Response.Body = new FailingReadStream();
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/secret")
        {
            Content = new StringContent("request-body-secret", Encoding.UTF8),
        };
        request.Headers.Add("X-Api-Key", "header-value-secret");

        // Act
        await Should.ThrowAsync<Exception>(() => client.SendAsync(request, cancellationToken));
        ILoggerEntry entry = await WaitForEntryAsync(recorded, LogLevel.Error);

        // Assert
        entry.Category.ShouldBe(WebApplicationServerLog.Category);
        entry.Attributes[WebApplicationServerLog.ProtocolVersionAttribute].ShouldBe("1.1");
        string logged = entry.Message + " " + string.Join(" ", entry.Attributes.Select(attribute => $"{attribute.Key}={attribute.Value}"));
        logged.ShouldNotContain("header-value-secret");
        logged.ShouldNotContain("request-body-secret");
        logged.ShouldNotContain("/secret");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server/Diagnostics: The default server logs through the application's logger factory")]
    public async Task Build_DefaultServer_ShouldLogBindFailuresThroughTheApplicationLoggerFactory()
    {
        // Arrange — a first application holds a loopback port; a second one is configured to bind it.
        TcpConnectionListener holder = new(new TcpConnectionListenerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
        WebApplicationBuilder holderBuilder = WebApplication.CreateBuilder();
        holderBuilder.Server.UseServer(options => options.UseHttp1(holder));
        await using WebApplication holderApplication = holderBuilder.Build();
        await ((IWebApplication)holderApplication).StartAsync();

        RecordingLoggerProvider recorded = new();
        TcpConnectionListener competitor = new(new TcpConnectionListenerOptions { EndPoint = (IPEndPoint)holder.EndPoint });
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.AddProvider(recorded);
        builder.Server.UseServer(options => options.UseHttp1(competitor));
        await using WebApplication application = builder.Build();

        // Act
        await Should.ThrowAsync<HostStartupException>(() => ((IWebApplication)application).StartAsync());

        // Assert
        ILoggerEntry entry = recorded.Entries.Where(entry => entry.Category == WebApplicationServerLog.Category).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Critical);
        entry.Exception.ShouldNotBeNull();

        await ((IWebApplication)holderApplication).StopAsync();
    }

    private static ILoggerFactory CreateLoggerFactory(RecordingLoggerProvider recorded)
    {
        return new LoggerFactoryBuilder()
            .AddProvider(recorded)
            .SetMinimumLevel(LogLevel.Trace)
            .Build();
    }

    private static WebApplicationServer CreateServer(IWebApplicationPipeline pipeline, FakeHttpConnectionListener listener, ILoggerFactory loggerFactory)
    {
        return new WebApplicationServer(new WebApplicationServerOptions
        {
            Pipeline = pipeline,
            Listener = listener,
            Logger = loggerFactory.Create(WebApplicationServerLog.Category),
        });
    }

    private static async Task<ILoggerEntry> WaitForEntryAsync(RecordingLoggerProvider recorded, LogLevel level)
    {
        using CancellationTokenSource timeout = new(_timeout);

        while (true)
        {
            ILoggerEntry? entry = recorded.Entries.FirstOrDefault(entry => entry.Level == level && entry.Category == WebApplicationServerLog.Category);

            if (entry is not null)
            {
                return entry;
            }

            await Task.Delay(10, timeout.Token);
        }
    }
}
