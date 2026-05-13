using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerFactoryTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactory: empty options produces no-op logger")]
    public void EmptyProviders_LoggerIsNoOp()
    {
        using var factory = new LoggerFactoryBuilder().Build();
        var logger = factory.Create("X");

        logger.LogInformation("X", "no providers");
        Assert.False(logger.IsEnabled(LogLevel.Information));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactory: caches loggers by category case-insensitively")]
    public void CachesByCategory()
    {
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(new RecordingProvider())
            .Build();

        var l1 = factory.Create("App");
        var l2 = factory.Create("APP");
        var l3 = factory.Create("Other");

        Assert.Same(l1, l2);
        Assert.NotSame(l1, l3);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactory: Create rejects empty category")]
    public void Create_EmptyCategory_Throws()
    {
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(new RecordingProvider())
            .Build();

        Assert.Throws<ArgumentException>(() => factory.Create(""));
        Assert.Throws<ArgumentNullException>(() => factory.Create(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactory: dispose disposes all providers")]
    public void Dispose_DisposesEveryProvider()
    {
        var providers = new[]
        {
            new RecordingProvider("a"),
            new RecordingProvider("b"),
            new RecordingProvider("c"),
        };
        var factory = new LoggerFactoryBuilder()
            .AddProvider(providers[0])
            .AddProvider(providers[1])
            .AddProvider(providers[2])
            .Build();

        factory.Dispose();

        Assert.True(providers[0].IsDisposed);
        Assert.True(providers[1].IsDisposed);
        Assert.True(providers[2].IsDisposed);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactory: disposed factory throws on use")]
    public void Disposed_ThrowsOnUse()
    {
        var factory = new LoggerFactoryBuilder()
            .AddProvider(new RecordingProvider())
            .Build();
        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => factory.Create("X"));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactory: double dispose is a no-op")]
    public void DoubleDispose_NoThrow()
    {
        var factory = new LoggerFactoryBuilder()
            .AddProvider(new RecordingProvider())
            .Build();
        factory.Dispose();
        factory.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactory: provider dispose exception does not abort teardown")]
    public void Dispose_ExceptionFromProviderSwallowed()
    {
        var throwing = new ThrowingDisposeProvider();
        var normal = new RecordingProvider("normal");

        var factory = new LoggerFactoryBuilder()
            .AddProvider(throwing)
            .AddProvider(normal)
            .Build();

        factory.Dispose();

        Assert.True(normal.IsDisposed);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactory: ctor rejects null options")]
    public void Ctor_NullOptionsThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new LoggerFactory(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactory: concurrent Create returns same instance")]
    public async Task Concurrent_Create_ReturnsSameInstance()
    {
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(new RecordingProvider())
            .Build();

        var loggers = new ILogger[Environment.ProcessorCount * 4];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, loggers.Length),
            (i, _) =>
            {
                loggers[i] = factory.Create("Hot");
                return ValueTask.CompletedTask;
            });

        var first = loggers[0];
        Assert.All(loggers, l => Assert.Same(first, l));
    }

    private sealed class ThrowingDisposeProvider : ILoggerProvider
    {
        public string Name => "Throwing";
        public ILogger Create(string category) => throw new InvalidOperationException();
        public void Dispose() => throw new InvalidOperationException("Provider disposed badly");
    }
}
