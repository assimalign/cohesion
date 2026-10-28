using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Diagnostics;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Server.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

[Collection(nameof(TelemetryCollection))]
public sealed class WebDefaultEventSourceForwardingTests
{
    private const string serverSource = "Assimalign.Cohesion.Web.Server";
    private const string tcpSource = "Assimalign.Cohesion.Connections.Tcp";
    private const string dependencyInjectionSource = "Assimalign.Cohesion.DependencyInjection";
    private const string accessLogCategory = "Assimalign.Cohesion.Web.Diagnostics.HttpLogging";

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Default forwarding: Entry points forward Web.Server without TCP or dependency-injection events")]
    public async Task Build_WithAnEntryPoint_ShouldForwardWebWithoutTransportOrContainerEvents()
    {
        // Arrange — initialize the real TCP provider so a separate listener can prove it raises events.
        await using (TcpConnectionListener warmup = CreateListener())
        {
            await warmup.BindAsync(CancellationToken.None);
        }
        using EventSourceRecorder tcpEvents = new(GetSource(tcpSource), EventLevel.Verbose);
        RecordingLoggerProvider recorded = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.AddProvider(recorded).SetMinimumLevel(LogLevel.Trace);
        TcpConnectionListener listener = CreateListener();
        builder.Server.UseServer(options => options.UseHttp1(listener));
        await using WebApplication application = builder.Build();
        ILoggerFactory factory = application.Context.ServiceProvider.GetRequiredService<ILoggerFactory>();
        using EventSourceRecorder containerEvents = new(GetSource(dependencyInjectionSource), EventLevel.Verbose);
        using ServiceProviderBuilder containerBuilder = new(new ServiceProviderOptions { EnableDynamicCode = false });

        // Act
        using ServiceProvider container = (ServiceProvider)((IServiceProviderBuilder)containerBuilder).Build();
        try
        {
            await ((IWebApplication)application).StartAsync(CancellationToken.None);
            await ((IWebApplication)application).StopAsync(CancellationToken.None);

            // Assert — each excluded provider actually emitted while forwarding was active.
            int containerId = container.GetHashCode();
            containerEvents.Events.ShouldContain(entry => entry.EventName == "ServiceProviderBuilt" &&
                Equals(entry.Payload![0], containerId));
            tcpEvents.Events.ShouldContain(entry => entry.EventName == "ListenerBound" &&
                Equals(entry.Payload![2], listener.EndPoint.ToString()));
            long serverId = (long)recorded.Entries.Single(entry => entry.Category == serverSource &&
                Equals(entry.Attributes["EventName"], "ServerBound")).Attributes["serverId"]!;
            ServerEntries(recorded, serverId, "ServerBound").ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Information);
            ServerEntries(recorded, serverId, "ServerStopped").ShouldHaveSingleItem();
            recorded.Entries.ShouldNotContain(entry => entry.Category.StartsWith("Assimalign.Cohesion.Connections.", StringComparison.Ordinal));
            recorded.Entries.ShouldNotContain(entry => entry.Category == dependencyInjectionSource);
            factory.Forwarders.ShouldHaveSingleItem().Name.ShouldBe("EventSource");
            factory.Providers.ShouldHaveSingleItem().ShouldBeSameAs(recorded);
            factory.Rules.ShouldBeEmpty();
            tcpEvents.Events.ShouldNotContain(entry => entry.EventId == 0);
            containerEvents.Events.ShouldNotContain(entry => entry.EventId == 0);
        }
        finally
        {
            await ((IWebApplication)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Default forwarding: Nested builders register no process-wide forwarder")]
    public async Task Build_WithANestedBuilder_ShouldNotRegisterAForwarder()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // Act
        await using WebApplication application = builder.Build();
        ILoggerFactory factory = application.Context.ServiceProvider.GetRequiredService<ILoggerFactory>();

        // Assert
        builder.Logging.HasForwarders.ShouldBeFalse();
        factory.Forwarders.ShouldBeEmpty();
        factory.Providers.ShouldBeEmpty();
        factory.Rules.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Default forwarding: An entry point may opt out")]
    public async Task Build_WhenDefaultForwardingIsDisabled_ShouldNotRegisterAForwarder()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.EnableEventSourceForwarding = false;

        // Act
        await using WebApplication application = builder.Build();
        ILoggerFactory factory = application.Context.ServiceProvider.GetRequiredService<ILoggerFactory>();

        // Assert
        builder.Logging.HasForwarders.ShouldBeFalse();
        factory.Forwarders.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Default forwarding: An application's source selection takes precedence")]
    public async Task Build_WithAnApplicationEventSourceForwarder_ShouldKeepItsSourceSelection()
    {
        // Arrange — the application explicitly chooses TCP instead of the Web default.
        RecordingLoggerProvider recorded = new();
        EventSourceForwardingOptions forwarding = new();
        forwarding.Sources.Clear();
        forwarding.Sources.Add(tcpSource);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.AddProvider(recorded).AddEventSourceForwarding(forwarding);
        TcpConnectionListener listener = CreateListener();
        builder.Server.UseServer(options => options.UseHttp1(listener));

        // Act
        await using WebApplication application = builder.Build();
        ILoggerFactory factory = application.Context.ServiceProvider.GetRequiredService<ILoggerFactory>();
        try
        {
            await ((IWebApplication)application).StartAsync(CancellationToken.None);
            await ((IWebApplication)application).StopAsync(CancellationToken.None);

            // Assert — a second EventSource forwarder would fail the factory's duplicate-name guard.
            factory.Forwarders.ShouldHaveSingleItem().Name.ShouldBe("EventSource");
            recorded.Entries.ShouldContain(entry => entry.Category == tcpSource &&
                entry.Attributes.ContainsKey("endPoint") && Equals(entry.Attributes["endPoint"], listener.EndPoint.ToString()));
            recorded.Entries.ShouldNotContain(entry => entry.Category == serverSource);
        }
        finally
        {
            await ((IWebApplication)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Default forwarding: Any application forwarder suppresses the hosting default")]
    public async Task Build_WithAnApplicationForwarder_ShouldPreserveItWithoutAddingTheDefault()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder(Array.Empty<string>());
        ApplicationForwarder forwarder = new();
        builder.Logging.AddForwarder(_ => forwarder);

        // Act
        await using WebApplication application = builder.Build();
        ILoggerFactory factory = application.Context.ServiceProvider.GetRequiredService<ILoggerFactory>();

        // Assert
        factory.Forwarders.ShouldHaveSingleItem().ShouldBeSameAs(forwarder);
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Default forwarding: HTTP access logging remains Information with the application's providers and rules")]
    public async Task Build_WithHttpLogging_ShouldPreserveInformationAccessLogsAndComposition()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        RecordingLoggerProvider recorded = new();
        LoggerFilterRule rule = new(category: "Application", level: LogLevel.Debug);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.AddProvider(recorded).AddRule(rule);
        TcpConnectionListener listener = CreateListener();
        builder.Server.UseServer(options => options.UseHttp1(listener));
        await using WebApplication application = builder.Build();
        ILoggerFactory factory = application.Context.ServiceProvider.GetRequiredService<ILoggerFactory>();
        application.UseHttpLogging(factory);
        application.Use((context, next) =>
        {
            context.Response.StatusCode = Assimalign.Cohesion.Http.HttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        // Act
        try
        {
            await ((IWebApplication)application).StartAsync(timeout.Token);
            using HttpClient client = new();
            using HttpResponseMessage response = await client.GetAsync($"http://{listener.EndPoint}/", timeout.Token);
            await ((IWebApplication)application).StopAsync(timeout.Token);

            // Assert
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            recorded.Entries.Where(entry => entry.Category == accessLogCategory).ShouldHaveSingleItem()
                .Level.ShouldBe(LogLevel.Information);
            factory.Providers.ShouldHaveSingleItem().ShouldBeSameAs(recorded);
            factory.Rules.ShouldHaveSingleItem().ShouldBeSameAs(rule);
            factory.Forwarders.ShouldHaveSingleItem().Name.ShouldBe("EventSource");
        }
        finally
        {
            await ((IWebApplication)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Default forwarding: Disposing the application releases its process-wide listener")]
    public async Task DisposeAsync_WithDefaultForwarding_ShouldDisposeTheFactoryAndStopDelivery()
    {
        // Arrange
        using EventSourceRecorder serverEvents = new(await ServerDiagnosticsSource.GetAsync(), EventLevel.Informational);
        RecordingLoggerProvider recorded = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.AddProvider(recorded);
        await using WebApplication application = builder.Build();
        ILoggerFactory factory = application.Context.ServiceProvider.GetRequiredService<ILoggerFactory>();
        WebApplicationServer first = new(new WebApplicationServerOptions
        {
            Pipeline = new FakePipeline(),
            Listener = new HttpConnectionListenerOptions().UseHttp1(CreateListener()),
        });
        WebApplicationServer later = new(new WebApplicationServerOptions
        {
            Pipeline = new FakePipeline(),
            Listener = new HttpConnectionListenerOptions().UseHttp1(CreateListener()),
        });

        // Act
        try
        {
            await first.StartAsync(CancellationToken.None);
            await first.StopAsync(CancellationToken.None);
            await ((IAsyncDisposable)application).DisposeAsync();
            await later.StartAsync(CancellationToken.None);
            await later.StopAsync(CancellationToken.None);

            // Assert
            EventWrittenEventArgs[] bound = serverEvents.Events.Where(entry => entry.EventId == 1).ToArray();
            bound.Length.ShouldBe(2);
            long firstId = (long)bound[0].Payload![0]!;
            long laterId = (long)bound[1].Payload![0]!;
            firstId.ShouldNotBe(laterId);
            ServerEntries(recorded, firstId, "ServerBound").ShouldHaveSingleItem();
            ServerEntries(recorded, laterId, "ServerBound").ShouldBeEmpty();
            Should.Throw<ObjectDisposedException>(() => factory.Create("after-disposal"));
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
            await later.StopAsync(CancellationToken.None);
        }
    }

    private static TcpConnectionListener CreateListener()
        => new(new TcpConnectionListenerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });

    private static EventSource GetSource(string name)
        => EventSource.GetSources().Single(source => source.Name == name);

    private static ILoggerEntry[] ServerEntries(RecordingLoggerProvider recorded, long serverId, string eventName)
        => recorded.Entries.Where(entry => entry.Category == serverSource &&
            entry.Attributes.TryGetValue("serverId", out object? id) && Equals(id, serverId) &&
            entry.Attributes.TryGetValue("EventName", out object? name) && Equals(name, eventName)).ToArray();

    private sealed class ApplicationForwarder : ILoggerForwarder
    {
        public string Name => "Application";

        public void Dispose() { }
    }
}
