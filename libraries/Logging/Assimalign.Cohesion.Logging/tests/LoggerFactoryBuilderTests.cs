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

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: AddFilter rejects empty prefix")]
    public void AddFilter_RejectsEmptyPrefix()
    {
        Assert.Throws<ArgumentException>(() => new LoggerFactoryBuilder().AddFilter("", LogLevel.Trace));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: UseFilter rejects null")]
    public void UseFilter_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new LoggerFactoryBuilder().UseFilter(null!));
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
        Assert.Throws<InvalidOperationException>(() => builder.AddFilter("X", LogLevel.Trace));
        Assert.Throws<InvalidOperationException>(() => builder.UseFilter(new PassthroughFilter()));
        Assert.Throws<InvalidOperationException>(() => builder.AddEnricher(new NoopEnricher()));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: factory minimum level governs IsEnabled")]
    public void Build_MinimumLevel_GovernsIsEnabled()
    {
        ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(new RecordingProvider("rec"))
            .SetMinimumLevel(LogLevel.Warning)
            .Build();

        var logger = factory.Create("Other");
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: AddFilter raises per-category minimum")]
    public void Build_AddFilter_RaisesMinimumForCategory()
    {
        var provider = new RecordingProvider();
        ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .AddFilter("App.Network", LogLevel.Error)
            .Build();

        // App.Network requires Error+ via the filter; Warning is rejected.
        factory.Create("App.Network.Http").LogWarning("App.Network.Http", "below filter");
        // Other categories defer to the factory minimum (Trace), so Debug passes.
        factory.Create("Other").LogDebug("Other", "above floor");

        Assert.Single(provider.Entries);
        Assert.Equal("above floor", System.Linq.Enumerable.Single(provider.Entries).Message);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: UseFilter composes with AddFilter")]
    public void Build_UseFilter_ComposesWithCategoryRules()
    {
        var provider = new RecordingProvider();
        var customFilter = new CategoryContainsFilter(forbidden: "secret");

        ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .UseFilter(customFilter)
            .AddFilter("App", LogLevel.Information)
            .Build();

        // App + Information: passes both filters.
        factory.Create("App.Other").LogInformation("App.Other", "ok");
        // App + Debug: blocked by category filter (App requires Information+).
        factory.Create("App.Other").LogDebug("App.Other", "filtered by category");
        // App.secret: blocked by custom filter regardless of level.
        factory.Create("App.secret.Component").LogInformation("App.secret.Component", "blocked by custom");

        Assert.Single(provider.Entries);
        Assert.Equal("ok", System.Linq.Enumerable.Single(provider.Entries).Message);
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

    private sealed class PassthroughFilter : ILoggerFilter
    {
        public bool ShouldLog(ILoggerEntry entry) => true;
    }

    private sealed class CategoryContainsFilter : ILoggerFilter
    {
        private readonly string _forbidden;
        public CategoryContainsFilter(string forbidden) { _forbidden = forbidden; }
        public bool ShouldLog(ILoggerEntry entry) => !entry.Category.Contains(_forbidden, StringComparison.OrdinalIgnoreCase);
    }
}
