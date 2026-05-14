using System;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerFilterRuleTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFilterRule: defaults are all null")]
    public void Defaults_AllNull()
    {
        var rule = new LoggerFilterRule();

        Assert.Null(rule.ProviderType);
        Assert.Null(rule.Category);
        Assert.Null(rule.Level);
        Assert.Null(rule.Filter);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFilterRule: ctor stores supplied values")]
    public void Ctor_StoresValues()
    {
        var filter = new NoopFilter();
        var rule = new LoggerFilterRule(
            providerType: typeof(RecordingProvider),
            category: "App.Network",
            level: LogLevel.Debug,
            filter: filter);

        Assert.Equal(typeof(RecordingProvider), rule.ProviderType);
        Assert.Equal("App.Network", rule.Category);
        Assert.Equal(LogLevel.Debug, rule.Level);
        Assert.Same(filter, rule.Filter);
    }

    private sealed class NoopFilter : ILoggerFilter
    {
        public bool ShouldLog(ILoggerEntry entry) => true;
    }
}
