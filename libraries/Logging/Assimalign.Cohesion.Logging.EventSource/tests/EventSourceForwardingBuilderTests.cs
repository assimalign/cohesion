using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Logging.Tests;

public class EventSourceForwardingBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - AddEventSourceForwarding: Should forward an event raised right after Build with no handle to hold")]
    public void AddEventSourceForwarding_EventRaisedAfterBuild_ShouldForward()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using WidgetEventSource source = WidgetEventSource.Create();

        // Act
        using ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .AddEventSourceForwarding()
            .Build();
        source.WidgetStarted("startup", 1);

        // Assert
        provider.EntriesFor(source.Name).ShouldHaveSingleItem().Attributes["widgetId"].ShouldBe("startup");
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - AddEventSourceForwarding: Should forward a source created after the factory was built")]
    public void AddEventSourceForwarding_SourceCreatedAfterBuild_ShouldForward()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .AddEventSourceForwarding()
            .Build();

        // Act
        using WidgetEventSource source = WidgetEventSource.Create();
        source.WidgetStarted("late", 1);

        // Assert
        provider.EntriesFor(source.Name).ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - AddEventSourceForwarding: Should stop forwarding and disable sources when the factory is disposed")]
    public void AddEventSourceForwarding_FactoryDisposed_ShouldStopForwarding()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using WidgetEventSource source = WidgetEventSource.Create();

        // The shape a host uses: the verb called on the concrete builder the host exposes.
        var logging = new LoggerFactoryBuilder();
        logging.AddProvider(provider);
        logging.AddEventSourceForwarding();
        ILoggerFactory factory = logging.Build();
        source.IsEnabled().ShouldBeTrue();

        // Act
        factory.Dispose();

        // Assert
        source.IsEnabled().ShouldBeFalse();
        Should.NotThrow(() => source.WidgetStarted("after", 1));
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - AddEventSourceForwarding: Should keep the prefixes it was registered with")]
    public void AddEventSourceForwarding_OptionsChangedAfterRegistration_ShouldKeepRegisteredPrefixes()
    {
        // Arrange
        var provider = new RecordingLoggerProvider();
        using WidgetEventSource contoso = WidgetEventSource.Create("Contoso.");
        var options = new EventSourceForwardingOptions { Sources = { "Contoso." } };
        ILoggerFactoryBuilder builder = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .AddEventSourceForwarding(options);

        // Act
        options.Sources.Clear();
        using ILoggerFactory factory = builder.Build();
        contoso.WidgetStarted("c", 1);

        // Assert
        provider.EntriesFor(contoso.Name).ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - AddEventSourceForwarding: Should list the forwarder as EventSource on the factory")]
    public void AddEventSourceForwarding_Built_ShouldListForwarderNamedEventSource()
    {
        // Arrange / Act
        using ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddEventSourceForwarding()
            .Build();

        // Assert
        factory.Forwarders.ShouldHaveSingleItem().Name.ShouldBe("EventSource");
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - AddEventSourceForwarding: Should fail the build when registered twice")]
    public void AddEventSourceForwarding_RegisteredTwice_ShouldThrowOnBuild()
    {
        // Arrange
        ILoggerFactoryBuilder builder = new LoggerFactoryBuilder()
            .AddEventSourceForwarding()
            .AddEventSourceForwarding(new EventSourceForwardingOptions { Sources = { "Contoso." } });

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - ForwardEventSources: Should not list a caller-owned forwarder on the factory")]
    public void ForwardEventSources_CallerOwnedForwarder_ShouldNotBeListed()
    {
        // Arrange
        using ILoggerFactory factory = new LoggerFactoryBuilder().Build();

        // Act
        using ILoggerForwarder forwarder = factory.ForwardEventSources();

        // Assert
        forwarder.Name.ShouldBe("EventSource");
        factory.Forwarders.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - AddEventSourceForwarding: Should reject options that select no source when registered")]
    public void AddEventSourceForwarding_EmptySources_ShouldThrowArgumentException()
    {
        // Arrange
        var builder = new LoggerFactoryBuilder();
        var options = new EventSourceForwardingOptions();
        options.Sources.Clear();

        // Act / Assert
        Should.Throw<ArgumentException>(() => builder.AddEventSourceForwarding(options));
    }

    [Fact(DisplayName = "Cohesion Test [Logging.EventSource] - AddEventSourceForwarding: Should reject a null builder")]
    public void AddEventSourceForwarding_NullBuilder_ShouldThrowArgumentNullException()
    {
        // Arrange
        ILoggerFactoryBuilder builder = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.AddEventSourceForwarding());
    }
}
