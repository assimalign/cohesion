using System;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - Logger: requires non-empty category")]
    public void Ctor_RejectsEmptyCategory()
    {
        Assert.Throws<ArgumentException>(() => new TestLogger(""));
        Assert.Throws<ArgumentNullException>(() => new TestLogger(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Logger: Log rejects null entry")]
    public void Log_NullEntry_Throws()
    {
        var logger = new TestLogger("Cat");
        Assert.Throws<ArgumentNullException>(() => logger.Log(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Logger: Log short-circuits when IsEnabled is false")]
    public void Log_DisabledShortCircuits()
    {
        var logger = new TestLogger("Cat", enabled: false);
        logger.Log(new LoggerEntry(LogLevel.Information, "Cat", "ignored"));
        Assert.Equal(0, logger.WriteCount);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Logger: Log dispatches to WriteCore once when enabled")]
    public void Log_EnabledDispatches()
    {
        var logger = new TestLogger("Cat");
        logger.Log(new LoggerEntry(LogLevel.Information, "Cat", "ok"));
        Assert.Equal(1, logger.WriteCount);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Logger: BeginScope rejects null entry")]
    public void BeginScope_NullThrows()
    {
        var logger = new TestLogger("Cat");
        Assert.Throws<ArgumentNullException>(() => logger.BeginScope(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Logger: BeginScope does not auto-emit seed")]
    public void BeginScope_NoAutoSeedEmission()
    {
        var logger = new TestLogger("Cat");
        using var scope = logger.BeginScope(new LoggerEntry(LogLevel.Information, "Cat", "seed"));

        // The base class does not auto-emit the seed entry through WriteCore - derived classes
        // decide whether to emit. Our TestLogger does not emit in BeginScopeCore.
        Assert.Equal(0, logger.WriteCount);
        Assert.NotNull(scope);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Logger: IsEnabled rejects LogLevel.None")]
    public void IsEnabled_NoneRejected()
    {
        var logger = new TestLogger("Cat");
        Assert.False(logger.IsEnabled(LogLevel.None));
        Assert.True(logger.IsEnabled(LogLevel.Trace));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Logger: Category is exposed")]
    public void Category_Exposed()
    {
        var logger = new TestLogger("App.Network.Http");
        Assert.Equal("App.Network.Http", logger.Category);
    }

    private sealed class TestLogger : Logger
    {
        private readonly bool _enabled;
        public int WriteCount;
        public TestLogger(string category, bool enabled = true) : base(category) { _enabled = enabled; }
        public override bool IsEnabled(LogLevel level) => _enabled && base.IsEnabled(level);
        protected override void WriteCore(ILoggerEntry entry) => WriteCount++;
        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
            => new TestScope(Category, entry.Id);
    }

    private sealed class TestScope : ScopedLogger
    {
        public TestScope(string category, LogId parentId) : base(category, parentId) { }
        protected override void WriteCore(ILoggerEntry entry) { }
        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry) => this;
    }
}
