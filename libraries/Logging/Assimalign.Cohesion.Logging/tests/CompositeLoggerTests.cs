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

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: AddRule(prefix, level) overrides minimum for matching category")]
    public void Rule_OverridesMinimumForCategory()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Error)
            .AddRule("App.Network", LogLevel.Trace)
            .Build();

        // App.Network matches -> Trace OK.
        var network = factory.Create("App.Network.Http");
        network.LogTrace("App.Network.Http", "should fire");
        Assert.Single(provider.Entries);

        // Generic category falls back to the factory minimum (Error).
        var generic = factory.Create("Generic");
        generic.LogWarning("Generic", "below factory minimum");
        Assert.Single(provider.Entries); // count unchanged
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: longest category prefix wins")]
    public void Rule_LongestPrefixWins()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .AddRule("App", LogLevel.Error)
            .AddRule("App.Important", LogLevel.Trace)
            .Build();

        // App.Important.* allowed at Trace because the more specific rule wins.
        factory.Create("App.Important.Component").LogTrace("App.Important.Component", "should fire");
        Assert.Single(provider.Entries);

        // App.Other still gated by Error+.
        factory.Create("App.Other").LogInformation("App.Other", "below Error rule");
        Assert.Single(provider.Entries); // count unchanged
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: last rule wins among ties")]
    public void Rule_LastWinsAmongTies()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            // Two rules for the same category prefix; the second registered wins.
            .AddRule("App", LogLevel.Error)
            .AddRule("App", LogLevel.Trace)
            .Build();

        factory.Create("App.Anything").LogTrace("App.Anything", "should fire because last rule wins");
        Assert.Single(provider.Entries);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: rules without category target every category in the candidate set")]
    public void Rule_NoCategory_AppliesToAll()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Error) // would otherwise drop Trace
            .AddRule(new LoggerFilterRule(level: LogLevel.Trace)) // global rule lowers gate to Trace
            .Build();

        factory.Create("Any.Category").LogTrace("Any.Category", "should fire");
        Assert.Single(provider.Entries);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: rule custom filter further screens entries")]
    public void Rule_Filter_FurtherScreens()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .AddRule(new LoggerFilterRule(filter: new AttributeKeyFilter("audit")))
            .Build();

        // Without the audit attribute - rejected.
        factory.Create("Cat").LogInformation("Cat", "missing audit");
        Assert.Empty(provider.Entries);

        // With the audit attribute - accepted.
        factory.Create("Cat").LogInformation(
            "Cat",
            "carries audit",
            attributes: new Dictionary<string, object?> { ["audit"] = true });
        Assert.Single(provider.Entries);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: throwing filter is treated as admit")]
    public void Filter_Throw_Admits()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .AddRule(new LoggerFilterRule(filter: new ThrowingFilter()))
            .Build();

        factory.Create("Cat").LogInformation("Cat", "should fire despite filter throw");
        Assert.Single(provider.Entries);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Composite: provider-type-specific rule gates only that provider")]
    public void Rule_PerProviderType()
    {
        var p1 = new RecordingProvider("p1");
        // Different runtime type so the rule does NOT apply to p2.
        var p2 = new AltRecordingProvider("p2");
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(p1)
            .AddProvider(p2)
            .SetMinimumLevel(LogLevel.Trace)
            .AddRule(new LoggerFilterRule(providerType: typeof(RecordingProvider), level: LogLevel.Error))
            .Build();

        factory.Create("Cat").LogInformation("Cat", "info");

        // p1 (RecordingProvider) gated to Error+; p2 (AltRecordingProvider) falls back to factory floor.
        Assert.Empty(p1.Entries);
        Assert.Single(p2.Entries);
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

    private sealed class ProcessEnricher : ILoggerEnricher
    {
        public void Enrich(ILoggerEntry entry, IDictionary<string, object?> attributes)
        {
            attributes["process.id"] = Environment.ProcessId;
        }
    }

    private sealed class FixedValueEnricher : ILoggerEnricher
    {
        private readonly string _key;
        private readonly object? _value;
        public FixedValueEnricher(string key, object? value) { _key = key; _value = value; }
        public void Enrich(ILoggerEntry entry, IDictionary<string, object?> attributes)
        {
            attributes[_key] = _value;
        }
    }

    private sealed class ThrowingEnricher : ILoggerEnricher
    {
        public void Enrich(ILoggerEntry entry, IDictionary<string, object?> attributes)
            => throw new InvalidOperationException("enrich failed");
    }

    private sealed class AttributeKeyFilter : ILoggerFilter
    {
        private readonly string _key;
        public AttributeKeyFilter(string key) { _key = key; }
        public bool ShouldLog(ILoggerEntry entry) => entry.Attributes.ContainsKey(_key);
    }

    private sealed class ThrowingFilter : ILoggerFilter
    {
        public bool ShouldLog(ILoggerEntry entry) => throw new InvalidOperationException("filter failed");
    }
}
