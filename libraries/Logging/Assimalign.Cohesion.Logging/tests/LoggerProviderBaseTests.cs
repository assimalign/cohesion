using System;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerProviderBaseTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerProviderBase: Create rejects empty category")]
    public void Create_EmptyCategory_Throws()
    {
        using var provider = new RecordingTestProvider();
        Assert.Throws<ArgumentException>(() => provider.Create(""));
        Assert.Throws<ArgumentNullException>(() => provider.Create(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerProviderBase: Create returns LoggerBase (covariant)")]
    public void Create_ReturnsLoggerBase()
    {
        using var provider = new RecordingTestProvider();
        LoggerBase logger = provider.Create("Cat");
        Assert.NotNull(logger);
        Assert.Equal("Cat", logger.Category);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerProviderBase: Create throws after dispose")]
    public void Create_DisposedThrows()
    {
        var provider = new RecordingTestProvider();
        provider.Dispose();
        Assert.Throws<ObjectDisposedException>(() => provider.Create("Cat"));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerProviderBase: Dispose is idempotent")]
    public void Dispose_Idempotent()
    {
        var provider = new RecordingTestProvider();
        provider.Dispose();
        provider.Dispose();
        Assert.Equal(1, provider.DisposeCoreCalls);
        Assert.True(provider.IsDisposed);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerProviderBase: ILoggerProvider.Create bridges to typed Create")]
    public void InterfaceCreate_BridgesToTypedCreate()
    {
        using var provider = new RecordingTestProvider();
        ILoggerProvider asInterface = provider;
        ILogger logger = asInterface.Create("Cat");

        Assert.IsAssignableFrom<LoggerBase>(logger);
        Assert.Equal("Cat", ((LoggerBase)logger).Category);
    }

    private sealed class RecordingTestProvider : LoggerProviderBase
    {
        public int DisposeCoreCalls;
        public override string Name => "RecordingTest";
        protected override LoggerBase CreateCore(string category) => new SimpleLogger(category);
        protected override void DisposeCore() => DisposeCoreCalls++;

        private sealed class SimpleLogger : LoggerBase
        {
            public SimpleLogger(string category) : base(category) { }
            protected override void WriteCore(ILoggerEntry entry) { }
            protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
                => new SimpleScope(Category, entry.Id);
        }

        private sealed class SimpleScope : ScopedLoggerBase
        {
            public SimpleScope(string category, LogId parentId) : base(category, parentId) { }
            protected override void WriteCore(ILoggerEntry entry) { }
            protected override IScopedLogger BeginScopeCore(ILoggerEntry entry) => this;
        }
    }
}
