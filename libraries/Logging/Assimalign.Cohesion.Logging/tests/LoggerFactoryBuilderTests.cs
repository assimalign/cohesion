using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerFactoryBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: rejects duplicate provider names")]
    public void DuplicateProvider_Throws()
    {
        var builder = new LoggerFactoryBuilder();
        builder.AddProvider(new TestProvider("alpha"));

        Assert.Throws<InvalidOperationException>(() => builder.AddProvider(new TestProvider("alpha")));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: AddProvider rejects null")]
    public void AddProvider_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new LoggerFactoryBuilder().AddProvider(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: AddRule rejects null")]
    public void AddRule_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new LoggerFactoryBuilder().AddRule(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: AddRule(prefix, level) rejects empty prefix")]
    public void AddRule_ConvenienceRejectsEmptyPrefix()
    {
        Assert.Throws<ArgumentException>(() => new LoggerFactoryBuilder().AddRule("", LogLevel.Trace));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: AddEnricher rejects null")]
    public void AddEnricher_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new LoggerFactoryBuilder().AddEnricher(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: cannot rebuild")]
    public void Rebuild_Throws()
    {
        var builder = new LoggerFactoryBuilder();
        builder.AddProvider(new TestProvider("p"));
        builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.AddProvider(new TestProvider("q")));
        Assert.Throws<InvalidOperationException>(() => builder.SetMinimumLevel(LogLevel.Trace));
        Assert.Throws<InvalidOperationException>(() => builder.AddRule(new LoggerFilterRule()));
        Assert.Throws<InvalidOperationException>(() => builder.AddRule("X", LogLevel.Trace));
        Assert.Throws<InvalidOperationException>(() => builder.AddEnricher(new NoopEnricher()));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: factory minimum level governs default IsEnabled")]
    public void Build_MinimumLevel_GovernsDefaultIsEnabled()
    {
        ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(new RecordingProvider("rec"))
            .SetMinimumLevel(LogLevel.Warning)
            .Build();

        var logger = factory.Create("Other");
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: AddRule(prefix, level) overrides minimum for category")]
    public void Build_AddRulePrefix_OverridesMinimum()
    {
        var provider = new RecordingProvider();
        ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Error)
            .AddRule("App.Network", LogLevel.Trace)
            .Build();

        // App.Network matches the rule -> Trace OK.
        factory.Create("App.Network.Http").LogTrace("App.Network.Http", "matched rule");
        // Other category falls back to factory minimum (Error); Warning is dropped.
        factory.Create("Other").LogWarning("Other", "below factory minimum");

        Assert.Single(provider.Entries);
        Assert.Equal("matched rule", System.Linq.Enumerable.Single(provider.Entries).Message);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: AddRule with custom filter applies after level")]
    public void Build_AddRuleWithFilter_GatesByFilter()
    {
        var provider = new RecordingProvider();
        ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .AddRule(new LoggerFilterRule(filter: new AttributeKeyFilter("audit")))
            .Build();

        factory.Create("Cat").LogInformation("Cat", "missing audit");
        Assert.Empty(provider.Entries);

        factory.Create("Cat").LogInformation(
            "Cat",
            "carries audit",
            attributes: new Dictionary<string, object?> { ["audit"] = true });
        Assert.Single(provider.Entries);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: provider-type-specific rule overrides default")]
    public void Build_ProviderTypeSpecificRule_Overrides()
    {
        var consoleLike = new RecordingProvider("console-like");
        var debugLike = new RecordingProvider("debug-like");
        ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(consoleLike)
            .AddProvider(debugLike)
            .SetMinimumLevel(LogLevel.Trace)
            .AddRule(new LoggerFilterRule(providerType: typeof(RecordingProvider), level: LogLevel.Error))
            .Build();

        factory.Create("Cat").LogInformation("Cat", "below rule");

        // Both providers are RecordingProvider so the type-specific rule applies to both.
        Assert.Empty(consoleLike.Entries);
        Assert.Empty(debugLike.Entries);

        factory.Create("Cat").LogError("Cat", "above rule");
        Assert.Single(consoleLike.Entries);
        Assert.Single(debugLike.Entries);
    }

    private sealed class TestProvider : ILoggerProvider
    {
        public TestProvider(string name) { Name = name; }
        public string Name { get; }
        public ILogger Create(string category) => new TestLogger();
        public void Dispose() { }
    }

    private sealed class TestLogger : ILogger
    {
        public bool IsEnabled(LogLevel level) => true;
        public void Log(ILoggerEntry entry) { }
        public IScopedLogger BeginScope(ILoggerEntry entry) => new TestScope();
    }

    private sealed class TestScope : IScopedLogger
    {
        public LogId ParentId => LogId.Empty;
        public bool IsEnabled(LogLevel level) => true;
        public void Log(ILoggerEntry entry) { }
        public IScopedLogger BeginScope(ILoggerEntry entry) => this;
        public void Dispose() { }
    }

    private sealed class NoopEnricher : ILoggerEnricher
    {
        public void Enrich(ILoggerEntry entry, IDictionary<string, object?> attributes) { }
    }

    private sealed class AttributeKeyFilter : ILoggerFilter
    {
        private readonly string _key;
        public AttributeKeyFilter(string key) { _key = key; }
        public bool ShouldLog(ILoggerEntry entry) => entry.Attributes.ContainsKey(_key);
    }
}
