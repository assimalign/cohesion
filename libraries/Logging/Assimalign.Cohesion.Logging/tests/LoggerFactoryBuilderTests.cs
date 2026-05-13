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
        builder.AddProvider(new RecordingProvider("alpha"));

        Assert.Throws<InvalidOperationException>(() => builder.AddProvider(new RecordingProvider("alpha")));
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

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: AddEnricher rejects null")]
    public void AddEnricher_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new LoggerFactoryBuilder().AddEnricher(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: cannot rebuild")]
    public void Rebuild_Throws()
    {
        var builder = new LoggerFactoryBuilder();
        builder.AddProvider(new RecordingProvider("p"));
        builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.AddProvider(new RecordingProvider("q")));
        Assert.Throws<InvalidOperationException>(() => builder.SetMinimumLevel(LogLevel.Trace));
        Assert.Throws<InvalidOperationException>(() => builder.AddFilter("X", LogLevel.Trace));
        Assert.Throws<InvalidOperationException>(() => builder.AddEnricher(new NoopEnricher()));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactoryBuilder: minimum level + filters propagate")]
    public void Build_PassesOptions()
    {
        var provider = new RecordingProvider("rec");
        ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Warning)
            .AddFilter("App.Network", LogLevel.Trace)
            .Build();

        var generic = factory.Create("Other");
        Assert.False(generic.IsEnabled(LogLevel.Information)); // below factory default
        Assert.True(generic.IsEnabled(LogLevel.Warning));

        var network = factory.Create("App.Network.Http");
        Assert.True(network.IsEnabled(LogLevel.Trace)); // filter override
    }

    private sealed class RecordingProvider : ILoggerProvider
    {
        public RecordingProvider(string name) { Name = name; }
        public string Name { get; }
        public ILogger Create(string category) => new TestLogger();
        public void Dispose() { }
    }

    private sealed class TestLogger : ILogger
    {
        public bool IsEnabled(LogLevel level) => true;
        public void Log(ILogEntry entry) { }
        public IScopedLogger BeginScope(ILogEntry entry) => new TestScope();
    }

    private sealed class TestScope : IScopedLogger
    {
        public LogId ParentId => LogId.Empty;
        public bool IsEnabled(LogLevel level) => true;
        public void Log(ILogEntry entry) { }
        public IScopedLogger BeginScope(ILogEntry entry) => this;
        public void Dispose() { }
    }

    private sealed class NoopEnricher : ILogEnricher
    {
        public void Enrich(ILogEntry entry, IDictionary<string, object?> attributes) { }
    }
}
