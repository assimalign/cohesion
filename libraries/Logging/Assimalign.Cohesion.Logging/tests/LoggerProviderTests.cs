using System;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerProviderTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerProvider: Create rejects empty category")]
    public void Create_EmptyCategory_Throws()
    {
        using var provider = new TestProvider();
        Assert.Throws<ArgumentException>(() => provider.Create(""));
        Assert.Throws<ArgumentNullException>(() => provider.Create(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerProvider: Create returns Logger (covariant)")]
    public void Create_ReturnsLogger()
    {
        using var provider = new TestProvider();
        Logger logger = provider.Create("Cat");
        Assert.NotNull(logger);
        Assert.Equal("Cat", logger.Category);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerProvider: Create throws after dispose")]
    public void Create_DisposedThrows()
    {
        var provider = new TestProvider();
        provider.Dispose();
        Assert.Throws<ObjectDisposedException>(() => provider.Create("Cat"));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerProvider: Dispose is idempotent")]
    public void Dispose_Idempotent()
    {
        var provider = new TestProvider();
        provider.Dispose();
        provider.Dispose();
        Assert.Equal(1, provider.DisposeCoreCalls);
        Assert.True(provider.IsDisposed);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerProvider: ILoggerProvider.Create bridges to typed Create")]
    public void InterfaceCreate_BridgesToTypedCreate()
    {
        using var provider = new TestProvider();
        ILoggerProvider asInterface = provider;
        ILogger logger = asInterface.Create("Cat");

        Assert.IsAssignableFrom<Logger>(logger);
        Assert.Equal("Cat", ((Logger)logger).Category);
    }

    private sealed class TestProvider : LoggerProvider
    {
        public int DisposeCoreCalls;
        public override string Name => "Test";
        protected override Logger CreateCore(string category) => new SimpleLogger(category);
        protected override void DisposeCore() => DisposeCoreCalls++;

        private sealed class SimpleLogger : Logger
        {
            public SimpleLogger(string category) : base(category) { }
            protected override void WriteCore(ILoggerEntry entry) { }
            protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
                => new SimpleScope(Category, entry.Id);
        }

        private sealed class SimpleScope : ScopedLogger
        {
            public SimpleScope(string category, LogId parentId) : base(category, parentId) { }
            protected override void WriteCore(ILoggerEntry entry) { }
            protected override IScopedLogger BeginScopeCore(ILoggerEntry entry) => this;
        }
    }
}
