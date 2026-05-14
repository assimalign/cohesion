using System;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class ScopedLoggerBaseTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - ScopedLoggerBase: stores ParentId")]
    public void Ctor_StoresParentId()
    {
        var parent = LogId.New();
        var scope = new SimpleScope("Cat", parent);
        Assert.Equal(parent, scope.ParentId);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - ScopedLoggerBase: IsEnabled returns false after disposal")]
    public void DisposedScope_IsEnabledFalse()
    {
        var scope = new SimpleScope("Cat", LogId.New());
        Assert.True(scope.IsEnabled(LogLevel.Information));
        scope.Dispose();
        Assert.False(scope.IsEnabled(LogLevel.Information));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - ScopedLoggerBase: Dispose is idempotent")]
    public void Dispose_Idempotent()
    {
        var scope = new SimpleScope("Cat", LogId.New());
        scope.Dispose();
        scope.Dispose();
        Assert.Equal(1, scope.DisposeCalls);
        Assert.True(scope.IsDisposed);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - ScopedLoggerBase: Log on disposed scope silently drops")]
    public void DisposedScope_Log_SilentlyDrops()
    {
        var scope = new SimpleScope("Cat", LogId.New());
        scope.Dispose();

        // The template short-circuits via IsEnabled before reaching WriteCore.
        scope.Log(new LoggerEntry(LogLevel.Information, "Cat", "after dispose"));
        Assert.Equal(0, scope.WriteCalls);
    }

    private sealed class SimpleScope : ScopedLoggerBase
    {
        public int WriteCalls;
        public int DisposeCalls;

        public SimpleScope(string category, LogId parentId) : base(category, parentId) { }
        protected override void WriteCore(ILoggerEntry entry) => WriteCalls++;
        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry) => this;
        protected override void DisposeCore() => DisposeCalls++;
    }
}
