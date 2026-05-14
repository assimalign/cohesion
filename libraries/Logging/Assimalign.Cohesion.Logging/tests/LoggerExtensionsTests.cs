using System;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerExtensions: each typed helper writes at the expected level")]
    public void TypedHelpers_WriteAtExpectedLevel()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .Build();
        var logger = factory.Create("Cat");

        logger.LogTrace("Cat", "t");
        logger.LogDebug("Cat", "d");
        logger.LogInformation("Cat", "i");
        logger.LogWarning("Cat", "w");
        logger.LogError("Cat", "e");
        logger.LogCritical("Cat", "c");
        logger.Log(LogLevel.Event, "Cat", "ev");

        var byLevel = provider.Entries.GroupBy(e => e.Level).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(1, byLevel[LogLevel.Trace]);
        Assert.Equal(1, byLevel[LogLevel.Debug]);
        Assert.Equal(1, byLevel[LogLevel.Information]);
        Assert.Equal(1, byLevel[LogLevel.Warning]);
        Assert.Equal(1, byLevel[LogLevel.Error]);
        Assert.Equal(1, byLevel[LogLevel.Critical]);
        Assert.Equal(1, byLevel[LogLevel.Event]);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerExtensions: short-circuits when level disabled")]
    public void DisabledLevel_ShortCircuits()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Error)
            .Build();

        factory.Create("Cat").LogInformation("Cat", "skipped");
        Assert.Empty(provider.Entries);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerExtensions: error helper captures exception")]
    public void LogError_CapturesException()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .Build();

        var exception = new InvalidOperationException("boom");
        factory.Create("Cat").LogError("Cat", "oops", exception);

        var entry = Assert.Single(provider.Entries);
        Assert.Same(exception, entry.Exception);
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerExtensions: BeginScope helper builds seed entry")]
    public void BeginScope_BuildsSeed()
    {
        var provider = new RecordingProvider();
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .Build();

        using var scope = factory.Create("Cat").BeginScope(
            "Cat",
            "scope opened",
            new Dictionary<string, object?> { ["k"] = 1 });

        Assert.NotEqual(LogId.Empty, scope.ParentId);

        var seed = Assert.Single(provider.Entries);
        Assert.Equal("scope opened", seed.Message);
        Assert.Equal(1, seed.Attributes["k"]);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerExtensions: rejects null logger or empty category")]
    public void NullArgs_Throws()
    {
        ILogger logger = null!;
        Assert.Throws<ArgumentNullException>(() => logger!.LogInformation("Cat", "m"));

        using var factory = new LoggerFactoryBuilder()
            .AddProvider(new RecordingProvider())
            .Build();
        logger = factory.Create("Cat");

        Assert.Throws<ArgumentException>(() => logger.LogInformation("", "m"));
        Assert.Throws<ArgumentNullException>(() => logger.LogInformation(null!, "m"));
        Assert.Throws<ArgumentException>(() => logger.BeginScope("", "msg"));
    }
}
