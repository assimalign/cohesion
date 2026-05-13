using System;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class CompositeLoggerTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: fans out to every provider")]
    public void Log_FansOut()
    {
        var p1 = new RecordingProvider("a");
        var p2 = new RecordingProvider("b");
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(p1)
            .AddProvider(p2)
            .Build();

        var logger = factory.Create("Cat");
        logger.LogInformation("Cat", "hello");

        Assert.Single(p1.Entries);
        Assert.Single(p2.Entries);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: short-circuits below minimum level")]
    public void Log_BelowMinimumLevel_ShortCircuits()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Warning)
            .Build();

        var logger = factory.Create("Cat");
        logger.LogInformation("Cat", "should not fire");

        Assert.Empty(provider.Entries);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: filter override raises and lowers minimum level")]
    public void Filter_Override()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Warning)
            .AddFilter("App.Network", LogLevel.Trace)
            .Build();

        // Default category - Warning required.
        var generic = factory.Create("Generic");
        generic.LogInformation("Generic", "filtered");
        Assert.Empty(provider.Entries);

        // Network category - filter override accepts Trace.
        var network = factory.Create("App.Network.Http");
        network.LogTrace("App.Network.Http", "should fire");
        Assert.Single(provider.Entries);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: longest filter prefix wins")]
    public void Filter_LongestPrefixWins()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("App", LogLevel.Error)
            .AddFilter("App.Important", LogLevel.Trace)
            .Build();

        var important = factory.Create("App.Important.Component");
        important.LogTrace("App.Important.Component", "should fire");
        Assert.Single(provider.Entries);

        var generic = factory.Create("App.Other");
        generic.LogInformation("App.Other", "below Error filter");
        Assert.Single(provider.Entries); // count unchanged
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: failure from one provider does not block others")]
    public void ProviderFailure_Isolated()
    {
        var bad = new RecordingProvider("bad", throwOnLog: true);
        var good = new RecordingProvider("good");
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(bad)
            .AddProvider(good)
            .Build();

        factory.Create("Cat").LogInformation("Cat", "hello");

        Assert.Empty(bad.Entries); // threw before recording
        Assert.Single(good.Entries);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: enrichers add attributes")]
    public void Enrichers_AddAttributes()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .AddEnricher(new ProcessEnricher())
            .Build();

        factory.Create("Cat").LogInformation("Cat", "hello");

        var entry = Assert.Single(provider.Entries);
        Assert.True(entry.Attributes.ContainsKey("process.id"));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: enricher cannot overwrite entry attributes")]
    public void Enrichers_CannotOverwrite()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .AddEnricher(new FixedValueEnricher("k", "from-enricher"))
            .Build();

        factory.Create("Cat").LogInformation(
            "Cat",
            "hello",
            attributes: new Dictionary<string, object?> { ["k"] = "from-caller" });

        var entry = Assert.Single(provider.Entries);
        Assert.Equal("from-caller", entry.Attributes["k"]);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: enricher exception is swallowed")]
    public void Enricher_ExceptionSwallowed()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .AddEnricher(new ThrowingEnricher())
            .Build();

        factory.Create("Cat").LogInformation("Cat", "hello");
        Assert.Single(provider.Entries);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: IsEnabled returns false when every provider disabled")]
    public void IsEnabled_AllDisabled_ReturnsFalse()
    {
        var disabled = new RecordingProvider();
        disabled.Dispose(); // RecordingProvider.IsEnabled returns false after dispose

        using var factory = new LoggerFactoryBuilder()
            .AddProvider(disabled)
            .Build();

        var logger = factory.Create("Cat");

        // The composite still respects the default minimum (Information) but every underlying
        // provider returns false, so the composite must too.
        Assert.False(logger.IsEnabled(LogLevel.Information));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: IsEnabled rejects LogLevel.None")]
    public void IsEnabled_NoneRejected()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .Build();

        Assert.False(factory.Create("X").IsEnabled(LogLevel.None));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: rejects null entry")]
    public void Log_NullEntry_Throws()
    {
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(new RecordingProvider())
            .Build();

        var logger = factory.Create("Cat");
        Assert.Throws<ArgumentNullException>(() => logger.Log(null!));
        Assert.Throws<ArgumentNullException>(() => logger.BeginScope(null!));
    }

    private sealed class ProcessEnricher : ILogEnricher
    {
        public void Enrich(ILogEntry entry, IDictionary<string, object?> attributes)
        {
            attributes["process.id"] = Environment.ProcessId;
        }
    }

    private sealed class FixedValueEnricher : ILogEnricher
    {
        private readonly string _key;
        private readonly object? _value;
        public FixedValueEnricher(string key, object? value) { _key = key; _value = value; }
        public void Enrich(ILogEntry entry, IDictionary<string, object?> attributes)
        {
            attributes[_key] = _value;
        }
    }

    private sealed class ThrowingEnricher : ILogEnricher
    {
        public void Enrich(ILogEntry entry, IDictionary<string, object?> attributes)
            => throw new InvalidOperationException("enrich failed");
    }
}
