using System;
using System.Linq;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class ScopedLoggerTests
{
    // ----------------------------------------------------------------------
    // Pipeline-level scope behavior (through factory.BeginScope + composite).
    // ----------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [Logging] - Scope: entry inside scope inherits seed id as parent")]
    public void ScopedEntry_InheritsParentId()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .Build();

        var logger = factory.Create("Cat");
        var seed = new LoggerEntry(LogLevel.Information, "Cat", "open scope");
        using IScopedLogger scope = logger.BeginScope(seed);

        scope.LogInformation("Cat", "inside scope");

        // Two entries: the seed (recorded on BeginScope) and the inner one.
        Assert.Equal(2, provider.Entries.Count);
        var inner = provider.Entries.Single(e => e.Message == "inside scope");
        Assert.Equal(seed.Id, inner.ParentId);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Scope: scope reports seed id as ParentId")]
    public void Scope_ReportsSeedAsParentId()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .Build();

        var seed = new LoggerEntry(LogLevel.Information, "Cat", "open scope");
        using IScopedLogger scope = factory.Create("Cat").BeginScope(seed);

        Assert.Equal(seed.Id, scope.ParentId);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Scope: nested scopes chain through parent ids")]
    public void NestedScopes_ChainParentIds()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .Build();

        var seedA = new LoggerEntry(LogLevel.Information, "Cat", "outer");
        using IScopedLogger outer = factory.Create("Cat").BeginScope(seedA);

        var seedB = new LoggerEntry(LogLevel.Information, "Cat", "inner");
        using IScopedLogger inner = outer.BeginScope(seedB);

        inner.LogInformation("Cat", "leaf");

        var leaf = provider.Entries.Single(e => e.Message == "leaf");
        Assert.Equal(seedB.Id, leaf.ParentId);

        // The inner seed itself was stamped with the outer's id as parent.
        var innerSeed = provider.Entries.Single(e => e.Message == "inner");
        Assert.Equal(seedA.Id, innerSeed.ParentId);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Scope: entry that already has parent id is not stamped over")]
    public void Scope_DoesNotOverwriteExplicitParent()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .Build();

        var customParent = LogId.New();
        using IScopedLogger scope = factory.Create("Cat").BeginScope(
            new LoggerEntry(LogLevel.Information, "Cat", "open scope"));

        scope.Log(new LoggerEntry(LogLevel.Information, "Cat", "explicit parent", parentId: customParent));

        var entry = provider.Entries.Single(e => e.Message == "explicit parent");
        Assert.Equal(customParent, entry.ParentId);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Scope: dispose is idempotent (pipeline)")]
    public void Dispose_Idempotent_Pipeline()
    {
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(new RecordingProvider())
            .Build();
        var seed = new LoggerEntry(LogLevel.Information, "Cat", "open");
        var scope = factory.Create("Cat").BeginScope(seed);
        scope.Dispose();
        scope.Dispose(); // no throw
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Scope: disposed scope silently drops Log")]
    public void DisposedScope_LogSilentlyDrops()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .Build();
        var scope = factory.Create("Cat").BeginScope(new LoggerEntry(LogLevel.Information, "Cat", "open"));
        int countAtDispose = provider.Entries.Count;
        scope.Dispose();

        // ScopedLogger.IsEnabled returns false after disposal, so the Logger.Log path
        // short-circuits without dispatching to WriteCore. This is consistent with the rest of
        // the pipeline which silently tolerates dead sinks (provider failures, filter throws).
        scope.Log(new LoggerEntry(LogLevel.Information, "Cat", "after dispose"));

        Assert.Equal(countAtDispose, provider.Entries.Count);
        Assert.False(scope.IsEnabled(LogLevel.Information));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Scope: provider that throws on BeginScope is replaced with noop")]
    public void ProviderThrowsOnScope_NoopSubstituted()
    {
        var bad = new RecordingProvider("bad", throwOnScope: true);
        var good = new RecordingProvider("good");
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(bad)
            .AddProvider(good)
            .Build();

        using IScopedLogger scope = factory.Create("Cat").BeginScope(
            new LoggerEntry(LogLevel.Information, "Cat", "open"));

        // Good provider observes the seed; bad provider threw before recording.
        Assert.Empty(bad.Entries);
        Assert.Single(good.Entries);

        // Disposing must not throw (NoopScopedLogger substitution).
        scope.Dispose();
    }

    // ----------------------------------------------------------------------
    // ScopedLogger abstract class - direct tests.
    // ----------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [Logging] - ScopedLogger: stores ParentId")]
    public void ScopedLogger_Ctor_StoresParentId()
    {
        var parent = LogId.New();
        var scope = new TestScope("Cat", parent);
        Assert.Equal(parent, scope.ParentId);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - ScopedLogger: IsEnabled returns false after disposal")]
    public void ScopedLogger_DisposedScope_IsEnabledFalse()
    {
        var scope = new TestScope("Cat", LogId.New());
        Assert.True(scope.IsEnabled(LogLevel.Information));
        scope.Dispose();
        Assert.False(scope.IsEnabled(LogLevel.Information));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - ScopedLogger: Dispose is idempotent (direct)")]
    public void ScopedLogger_Dispose_Idempotent()
    {
        var scope = new TestScope("Cat", LogId.New());
        scope.Dispose();
        scope.Dispose();
        Assert.Equal(1, scope.DisposeCalls);
        Assert.True(scope.IsDisposed);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - ScopedLogger: Log on disposed scope short-circuits to no-op")]
    public void ScopedLogger_DisposedLog_NoOps()
    {
        var scope = new TestScope("Cat", LogId.New());
        scope.Dispose();

        // The template short-circuits via IsEnabled before reaching WriteCore.
        scope.Log(new LoggerEntry(LogLevel.Information, "Cat", "after dispose"));
        Assert.Equal(0, scope.WriteCalls);
    }

    private sealed class TestScope : ScopedLogger
    {
        public int WriteCalls;
        public int DisposeCalls;

        public TestScope(string category, LogId parentId) : base(category, parentId) { }
        protected override void WriteCore(ILoggerEntry entry) => WriteCalls++;
        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry) => this;
        protected override void DisposeCore() => DisposeCalls++;
    }
}
