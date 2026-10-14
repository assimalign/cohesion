using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Logging.Tests;

public sealed class LoggerFactoryBuilderForwardingTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - HasForwarders: Registration is visible without creating the forwarder")]
    public void HasForwarders_WhenRegistered_ShouldReportTheRegistrationWithoutCreatingIt()
    {
        // Arrange
        ILoggerFactoryBuilder builder = new LoggerFactoryBuilder();
        int created = 0;
        builder.HasForwarders.ShouldBeFalse();

        // Act
        builder.AddForwarder(factory =>
        {
            created++;
            return new RecordingForwarder("application", factory);
        });

        // Assert
        builder.HasForwarders.ShouldBeTrue();
        created.ShouldBe(0);
        using ILoggerFactory factory = builder.Build();
        created.ShouldBe(1);
        factory.Forwarders.ShouldHaveSingleItem().Name.ShouldBe("application");
        builder.HasForwarders.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - HasForwarders: Providers and rules do not count as forwarding")]
    public void HasForwarders_WithOnlyProvidersAndRules_ShouldRemainFalse()
    {
        // Arrange
        ILoggerFactoryBuilder builder = new LoggerFactoryBuilder();

        // Act
        builder.AddProvider(new RecordingProvider());
        builder.AddRule("application", LogLevel.Debug);
        using ILoggerFactory factory = builder.Build();

        // Assert
        builder.HasForwarders.ShouldBeFalse();
        factory.Forwarders.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - HasForwarders: An existing custom builder inherits a compatible default")]
    public void HasForwarders_WithAnExistingImplementation_ShouldDefaultToFalse()
    {
        // Arrange — implements the pre-probe interface without adding the new member.
        ILoggerFactoryBuilder builder = new ExistingLoggerFactoryBuilder();

        // Act
        bool hasForwarders = builder.HasForwarders;

        // Assert
        hasForwarders.ShouldBeFalse();
    }

    private sealed class ExistingLoggerFactoryBuilder : ILoggerFactoryBuilder
    {
        public ILoggerFactoryBuilder AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public ILoggerFactoryBuilder SetMinimumLevel(LogLevel level) => throw new NotSupportedException();

        public ILoggerFactoryBuilder AddRule(LoggerFilterRule rule) => throw new NotSupportedException();

        public ILoggerFactoryBuilder AddRule(string categoryPrefix, LogLevel minimumLevel) => throw new NotSupportedException();

        public ILoggerFactoryBuilder AddEnricher(ILoggerEnricher enricher) => throw new NotSupportedException();

        public ILoggerFactoryBuilder AddForwarder(Func<ILoggerFactory, ILoggerForwarder> create) => throw new NotSupportedException();

        public ILoggerFactory Build() => throw new NotSupportedException();
    }
}
