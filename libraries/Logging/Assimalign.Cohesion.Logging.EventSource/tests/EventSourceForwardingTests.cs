using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Logging.Tests;

public class EventSourceForwardingTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should write a Cohesion event as an entry in the source's category")]
    public void ForwardEventSources_CohesionInformationalEvent_ShouldWriteEntryInSourceCategory()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using WidgetEventSource source = WidgetEventSource.Create();
        using IDisposable forwarding = factory.ForwardEventSources();
        DateTimeOffset before = DateTimeOffset.UtcNow;

        // Act
        source.WidgetStarted("w1", 3);

        // Assert
        ILoggerEntry entry = provider.EntriesFor(source.Name).ShouldHaveSingleItem();
        entry.Category.ShouldBe(source.Name);
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldBe("Widget w1 started with 3 parts");
        entry.Exception.ShouldBeNull();
        entry.Timestamp.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
        entry.Timestamp.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(1));
        entry.Attributes["widgetId"].ShouldBe("w1");
        entry.Attributes["partCount"].ShouldBe(3);
        entry.Attributes["EventId"].ShouldBe(1);
        entry.Attributes["EventName"].ShouldBe("WidgetStarted");
    }

    [Theory(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should map each event level to its log level")]
    [InlineData(2, LogLevel.Debug)]
    [InlineData(1, LogLevel.Information)]
    [InlineData(3, LogLevel.Warning)]
    [InlineData(4, LogLevel.Error)]
    [InlineData(5, LogLevel.Critical)]
    public void ForwardEventSources_EventAtEachLevel_ShouldMapToLogLevel(int eventId, LogLevel expected)
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Trace);
        using WidgetEventSource source = WidgetEventSource.Create();
        using IDisposable forwarding = factory.ForwardEventSources();

        // Act
        WriteWidgetEvent(source, eventId);

        // Assert
        ILoggerEntry entry = provider.EntriesFor(source.Name).ShouldHaveSingleItem();
        entry.Level.ShouldBe(expected);
        entry.Attributes["EventId"].ShouldBe(eventId);
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should enable a source no more verbosely than its logger accepts")]
    public void ForwardEventSources_WarningMinimumLevel_ShouldNotEnableInformationalEvents()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Warning);
        using WidgetEventSource source = WidgetEventSource.Create();

        // Act
        using IDisposable forwarding = factory.ForwardEventSources();
        source.WidgetStarted("w1", 1);
        source.WidgetDegraded("w1");

        // Assert
        source.IsEnabled(EventLevel.Informational, EventKeywords.All).ShouldBeFalse();
        source.IsEnabled(EventLevel.Warning, EventKeywords.All).ShouldBeTrue();
        provider.EntriesFor(source.Name).ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should enable verbose events when a category rule accepts debug")]
    public void ForwardEventSources_DebugRuleForSourcePrefix_ShouldForwardVerboseEventsAsDebug()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using WidgetEventSource source = WidgetEventSource.Create();
        using ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Information)
            .AddRule(EventSourceForwardingOptions.CohesionSourcePrefix + "Logging.EventSource.Tests", LogLevel.Debug)
            .Build();

        // Act
        using IDisposable forwarding = factory.ForwardEventSources();
        source.WidgetTicked("w1");

        // Assert
        source.IsEnabled(EventLevel.Verbose, EventKeywords.All).ShouldBeTrue();
        ILoggerEntry entry = provider.EntriesFor(source.Name).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Debug);
        entry.Message.ShouldBe("Widget w1 ticked");
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should leave a source disabled when its logger accepts nothing")]
    public void ForwardEventSources_FactoryWithoutProviders_ShouldNotEnableSource()
    {
        // Arrange
        using ILoggerFactory factory = new LoggerFactoryBuilder().Build();
        using WidgetEventSource source = WidgetEventSource.Create();

        // Act
        using IDisposable forwarding = factory.ForwardEventSources();

        // Assert
        source.IsEnabled().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should forward a source created before forwarding started")]
    public void ForwardEventSources_SourceCreatedBeforeForwarding_ShouldForward()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using WidgetEventSource source = WidgetEventSource.Create();
        source.WidgetStarted("before", 1);

        // Act
        using IDisposable forwarding = factory.ForwardEventSources();
        source.WidgetStarted("after", 2);

        // Assert
        provider.EntriesFor(source.Name).ShouldHaveSingleItem().Attributes["widgetId"].ShouldBe("after");
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should forward a source created after forwarding started")]
    public void ForwardEventSources_SourceCreatedAfterForwarding_ShouldForward()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using IDisposable forwarding = factory.ForwardEventSources();

        // Act
        using WidgetEventSource source = WidgetEventSource.Create();
        source.WidgetStarted("w1", 1);

        // Assert
        provider.EntriesFor(source.Name).ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should match source name prefixes case-insensitively")]
    public void ForwardEventSources_SourceNameInDifferentCase_ShouldForward()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using WidgetEventSource source = WidgetEventSource.Create("assimalign.cohesion.");
        using IDisposable forwarding = factory.ForwardEventSources();

        // Act
        source.WidgetStarted("w1", 1);

        // Assert
        provider.EntriesFor(source.Name).ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should ignore sources outside the Cohesion prefix by default")]
    public void ForwardEventSources_NonCohesionSourceWithDefaultOptions_ShouldNotForward()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using WidgetEventSource source = WidgetEventSource.Create("Contoso.");
        using IDisposable forwarding = factory.ForwardEventSources();

        // Act
        source.WidgetStarted("w1", 1);

        // Assert
        provider.EntriesFor(source.Name).ShouldBeEmpty();
        source.IsEnabled().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should forward an added prefix alongside the Cohesion default")]
    public void ForwardEventSources_AddedSourcePrefix_ShouldForwardBothPrefixes()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using WidgetEventSource contoso = WidgetEventSource.Create("Contoso.");
        using WidgetEventSource cohesion = WidgetEventSource.Create();
        var options = new EventSourceForwardingOptions { Sources = { "Contoso." } };

        // Act
        using IDisposable forwarding = factory.ForwardEventSources(options);
        contoso.WidgetStarted("c", 1);
        cohesion.WidgetStarted("a", 1);

        // Assert
        provider.EntriesFor(contoso.Name).ShouldHaveSingleItem();
        provider.EntriesFor(cohesion.Name).ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should forward only the named prefixes after the default is cleared")]
    public void ForwardEventSources_ClearedDefaultPrefix_ShouldForwardOnlyNamedPrefix()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using WidgetEventSource contoso = WidgetEventSource.Create("Contoso.");
        using WidgetEventSource cohesion = WidgetEventSource.Create();
        var options = new EventSourceForwardingOptions();
        options.Sources.Clear();
        options.Sources.Add("Contoso.");

        // Act
        using IDisposable forwarding = factory.ForwardEventSources(options);
        contoso.WidgetStarted("c", 1);
        cohesion.WidgetStarted("a", 1);

        // Assert
        provider.EntriesFor(contoso.Name).ShouldHaveSingleItem();
        provider.EntriesFor(cohesion.Name).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should use the event name when an event has no message template")]
    public void ForwardEventSources_EventWithoutMessageTemplate_ShouldUseEventName()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using WidgetEventSource source = WidgetEventSource.Create();
        using IDisposable forwarding = factory.ForwardEventSources();

        // Act
        source.WidgetDegraded("w1");

        // Assert
        ILoggerEntry entry = provider.EntriesFor(source.Name).ShouldHaveSingleItem();
        entry.Message.ShouldBe("WidgetDegraded");
        entry.Attributes["widgetId"].ShouldBe("w1");
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should fall back to the event name when a template does not match its payload")]
    public void ForwardEventSources_TemplateWithMissingArgument_ShouldUseEventName()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using WidgetEventSource source = WidgetEventSource.Create();
        using IDisposable forwarding = factory.ForwardEventSources();

        // Act
        source.WidgetMeasured("w1", 7);

        // Assert
        ILoggerEntry entry = provider.EntriesFor(source.Name).ShouldHaveSingleItem();
        entry.Message.ShouldBe("WidgetMeasured");
        entry.Attributes["value"].ShouldBe(7);
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should surface EventSource's own error report as a warning")]
    public void ForwardEventSources_MiswrittenEvent_ShouldForwardEventSourceMessageAsWarning()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using WidgetEventSource source = WidgetEventSource.Create();
        using IDisposable forwarding = factory.ForwardEventSources();

        // Act
        source.WidgetMiswritten("w1", "never written");

        // Assert
        ILoggerEntry report = provider.EntriesFor(source.Name)
            .Single(entry => Equals(entry.Attributes["EventId"], 0));
        report.Level.ShouldBe(LogLevel.Warning);
        report.Attributes["EventName"].ShouldBe("EventSourceMessage");
        report.Message.ShouldNotBeNullOrWhiteSpace();
        report.Message.ShouldNotBe("EventSourceMessage");
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should not forward periodic counter payloads")]
    public async Task ForwardEventSources_CountersEnabledByAnotherListener_ShouldNotForwardCounterPayloads()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Trace);
        using WidgetEventSource source = WidgetEventSource.Create();
        using IDisposable forwarding = factory.ForwardEventSources();
        using var counters = new CounterPayloadListener();

        // Act
        counters.EnableCounters(source);
        await counters.FirstPayload.WaitAsync(_testTimeout);

        // Assert
        provider.EntriesFor(source.Name)
            .ShouldNotContain(entry => Equals(entry.Attributes["EventName"], "EventCounters"));
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should drop an event raised while its thread is forwarding")]
    public void ForwardEventSources_SinkRaisingAnotherEvent_ShouldNotRecurse()
    {
        // Arrange
        WidgetEventSource source = WidgetEventSource.Create();
        int depth = 0;
        var provider = new RecordingLoggerProvider(onLog: _ =>
        {
            // Bounded so that a missing guard fails the assertion instead of overflowing the stack.
            if (++depth < 5)
            {
                source.WidgetStarted("nested", depth);
            }
        });
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using IDisposable forwarding = factory.ForwardEventSources();

        try
        {
            // Act
            source.WidgetStarted("outer", 0);

            // Assert
            provider.EntriesFor(source.Name).ShouldHaveSingleItem().Attributes["widgetId"].ShouldBe("outer");
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should stop forwarding and disable sources when disposed")]
    public void ForwardEventSources_AfterDispose_ShouldStopForwarding()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using WidgetEventSource source = WidgetEventSource.Create();
        IDisposable forwarding = factory.ForwardEventSources();

        // Act
        forwarding.Dispose();
        forwarding.Dispose();
        source.WidgetStarted("w1", 1);

        // Assert
        provider.EntriesFor(source.Name).ShouldBeEmpty();
        source.IsEnabled().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should not throw into instrumented code after the factory is disposed")]
    public void ForwardEventSources_FactoryDisposed_ShouldNotThrowIntoEventSource()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        ILoggerFactory factory = CreateFactory(provider, LogLevel.Information);
        using WidgetEventSource existing = WidgetEventSource.Create();
        using IDisposable forwarding = factory.ForwardEventSources();

        // Act
        factory.Dispose();
        using WidgetEventSource created = WidgetEventSource.Create();
        Exception? writeException = Record.Exception(() =>
        {
            existing.WidgetStarted("w1", 1);
            created.WidgetStarted("w2", 1);
        });

        // Assert
        writeException.ShouldBeNull();
        created.ConstructionException.ShouldBeNull();
        provider.EntriesFor(existing.Name).ShouldBeEmpty();
        provider.EntriesFor(created.Name).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should contain a factory that throws while a source is constructed")]
    public void ForwardEventSources_FactoryThrowingOnCreate_ShouldLeaveSourceUsable()
    {
        // Arrange
        var factory = new FaultingLoggerFactory(throwOnCreate: true);
        using IDisposable forwarding = factory.ForwardEventSources();

        // Act
        WidgetEventSource? source = null;
        Exception? exception = Record.Exception(() =>
        {
            source = WidgetEventSource.Create();
            source.WidgetStarted("w1", 1);
        });

        // Assert
        exception.ShouldBeNull();
        source.ShouldNotBeNull().ConstructionException.ShouldBeNull();
        source.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should contain a sink that throws while writing")]
    public void ForwardEventSources_LoggerThrowingOnLog_ShouldNotThrowIntoEventSource()
    {
        // Arrange
        var factory = new FaultingLoggerFactory(throwOnCreate: false);
        using WidgetEventSource source = WidgetEventSource.Create();
        using IDisposable forwarding = factory.ForwardEventSources();

        // Act
        Exception? exception = Record.Exception(() => source.WidgetStarted("w1", 1));

        // Assert
        exception.ShouldBeNull();
        factory.LogAttempts.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should reject a null factory")]
    public void ForwardEventSources_NullFactory_ShouldThrowArgumentNullException()
    {
        // Arrange
        ILoggerFactory factory = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => factory.ForwardEventSources());
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should reject options that select no source")]
    public void ForwardEventSources_EmptySources_ShouldThrowArgumentException()
    {
        // Arrange
        using ILoggerFactory factory = new LoggerFactoryBuilder().Build();
        var options = new EventSourceForwardingOptions();
        options.Sources.Clear();

        // Act / Assert
        Should.Throw<ArgumentException>(() => factory.ForwardEventSources(options));
    }

    [Theory(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should reject a blank source prefix")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForwardEventSources_BlankSourcePrefix_ShouldThrowArgumentException(string? prefix)
    {
        // Arrange
        using ILoggerFactory factory = new LoggerFactoryBuilder().Build();
        var options = new EventSourceForwardingOptions { Sources = { prefix! } };

        // Act / Assert
        Should.Throw<ArgumentException>(() => factory.ForwardEventSources(options));
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - EventSourceForwardingOptions: Should default to the Cohesion source prefix")]
    public void EventSourceForwardingOptions_Default_ShouldSelectCohesionPrefix()
    {
        // Arrange / Act
        var options = new EventSourceForwardingOptions();

        // Assert
        options.Sources.ShouldBe(new List<string> { "Assimalign.Cohesion." });
    }

    private static ILoggerFactory CreateFactory(RecordingLoggerProvider provider, LogLevel minimumLevel)
        => new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(minimumLevel)
            .Build();

    private static void WriteWidgetEvent(WidgetEventSource source, int eventId)
    {
        switch (eventId)
        {
            case 1:
                source.WidgetStarted("w1", 1);
                break;
            case 2:
                source.WidgetTicked("w1");
                break;
            case 3:
                source.WidgetDegraded("w1");
                break;
            case 4:
                source.WidgetFailed("w1", "jammed");
                break;
            case 5:
                source.WidgetLost("w1");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(eventId));
        }
    }
}
