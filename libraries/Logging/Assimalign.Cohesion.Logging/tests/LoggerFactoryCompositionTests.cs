using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerFactoryCompositionTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - Composition: Should expose every registration in order")]
    public void Properties_BuiltFactory_ShouldExposeRegistrationsInOrder()
    {
        // Arrange
        var provider = new RecordingProvider("sink");
        var first = new NamedEnricher("first");
        var second = new NamedEnricher("second");
        var rule = new LoggerFilterRule(category: "App", level: LogLevel.Debug);

        // Act
        using ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .AddEnricher(first)
            .AddEnricher(second)
            .AddRule(rule)
            .AddForwarder(built => new RecordingForwarder("alpha", built))
            .AddForwarder(built => new RecordingForwarder("beta", built))
            .Build();

        // Assert
        factory.Providers.ShouldHaveSingleItem().ShouldBeSameAs(provider);
        factory.Enrichers.ShouldBe(new ILoggerEnricher[] { first, second });
        factory.Rules.ShouldHaveSingleItem().ShouldBeSameAs(rule);
        factory.Forwarders.Select(forwarder => forwarder.Name).ShouldBe(new[] { "alpha", "beta" });
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composition: Should return read-only views, not the factory's arrays")]
    public void Properties_BuiltFactory_ShouldBeReadOnlyViews()
    {
        // Arrange
        using ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(new RecordingProvider())
            .AddEnricher(new NamedEnricher("only"))
            .AddRule("App", LogLevel.Debug)
            .AddForwarder(built => new RecordingForwarder("only", built))
            .Build();

        // Act
        object[] views = [factory.Providers, factory.Enrichers, factory.Rules, factory.Forwarders];

        // Assert
        views.ShouldAllBe(view => !(view is Array));
        factory.Enrichers.ShouldBeAssignableTo<ICollection<ILoggerEnricher>>().IsReadOnly.ShouldBeTrue();
        factory.Forwarders.ShouldBeAssignableTo<ICollection<ILoggerForwarder>>().IsReadOnly.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composition: Should list no forwarders while they are being created")]
    public void Forwarders_DuringCreation_ShouldBeEmpty()
    {
        // Arrange
        int? seenDuringCreation = null;
        var builder = new LoggerFactoryBuilder()
            .AddForwarder(built =>
            {
                seenDuringCreation = built.Forwarders.Count;
                return new RecordingForwarder("only", built);
            });

        // Act
        using ILoggerFactory factory = builder.Build();

        // Assert
        seenDuringCreation.ShouldBe(0);
        factory.Forwarders.Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Logging] - Composition: Should reject a second enricher with the same name")]
    [InlineData("trace")]
    [InlineData("TRACE")]
    public void AddEnricher_DuplicateName_ShouldThrowInvalidOperationException(string secondName)
    {
        // Arrange
        var builder = new LoggerFactoryBuilder().AddEnricher(new NamedEnricher("trace"));

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => builder.AddEnricher(new NamedEnricher(secondName)));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composition: Should fail construction on a duplicate forwarder name and release everything")]
    public void Build_DuplicateForwarderName_ShouldDisposeCreatedAndThrow()
    {
        // Arrange
        var teardown = new List<string>();
        var builder = new LoggerFactoryBuilder()
            .AddProvider(new TeardownProvider("a", teardown))
            .AddForwarder(built => new RecordingForwarder("same", built, teardown))
            .AddForwarder(built => new RecordingForwarder("SAME", built, teardown));

        // Act
        Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        teardown.ShouldBe(new[] { "forwarder:SAME", "forwarder:same", "provider:a" });
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composition: Should allow one filter on several rules")]
    public void AddRule_SameFilterOnTwoRules_ShouldListBothRules()
    {
        // Arrange
        var filter = new NamedFilter("shared");

        // Act
        using ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddRule(new LoggerFilterRule(category: "App.A", filter: filter))
            .AddRule(new LoggerFilterRule(category: "App.B", filter: filter))
            .Build();

        // Assert
        factory.Rules.Count.ShouldBe(2);
        factory.Rules.ShouldAllBe(rule => ReferenceEquals(rule.Filter, filter));
    }
}
