using Assimalign.Cohesion.Logging;

using Shouldly;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

/// <summary>
/// File-behavior coverage for <see cref="W3CAccessLogProvider"/>: directive blocks, day and
/// size rolling, retention sweeps, exchange-entry filtering, and flush semantics.
/// </summary>
public class W3CAccessLogProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cohesion-w3c-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; leaked temp directories are harmless.
        }
    }

    /// <summary>
    /// Reads a log file the provider may still hold open for writing: the writer opens with
    /// <see cref="FileShare.Read"/>, so readers must share write access.
    /// </summary>
    private static string ReadLogFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static LoggerEntry CreateExchangeEntry(DateTimeOffset timestamp, string path = "/orders", int status = 200)
        => new(
            LogLevel.Information,
            "access",
            "msg",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [HttpLoggingAttributes.Event] = HttpLoggingAttributes.EventExchange,
                [HttpLoggingAttributes.RequestMethod] = "GET",
                [HttpLoggingAttributes.RequestPath] = path,
                [HttpLoggingAttributes.ResponseStatusCode] = status,
            },
            timestamp: timestamp);

    private W3CAccessLogOptions CreateOptions(Action<W3CAccessLogOptions>? mutate = null)
    {
        W3CAccessLogOptions options = new()
        {
            Directory = _directory,
            FlushInterval = TimeSpan.Zero,
        };
        mutate?.Invoke(options);
        return options;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C provider: Writes the directive block and one line per exchange")]
    public void Write_ExchangeEntry_ShouldProduceDirectivesAndLine()
    {
        // Arrange
        using W3CAccessLogProvider provider = new(CreateOptions());
        ILogger logger = ((ILoggerProvider)provider).Create("access");

        // Act
        logger.Log(CreateExchangeEntry(new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero)));

        // Assert
        string file = Path.Combine(_directory, "access-20260711.log");
        File.Exists(file).ShouldBeTrue();

        string content = ReadLogFile(file);
        content.ShouldStartWith("#Version: 1.0\n");
        content.ShouldContain("#Fields: date time c-ip cs-method", Case.Sensitive);
        content.ShouldContain("2026-07-11 08:00:00 - GET /orders - 200", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C provider: Ignores entries that are not completed exchanges")]
    public void Write_NonExchangeEntries_ShouldBeIgnored()
    {
        // Arrange
        using W3CAccessLogProvider provider = new(CreateOptions());
        ILogger logger = ((ILoggerProvider)provider).Create("app");

        // Act — plain application logging and a request-start entry share the factory.
        logger.Log(new LoggerEntry(LogLevel.Information, "app", "application message"));
        logger.Log(new LoggerEntry(
            LogLevel.Information,
            "access",
            "start",
            attributes: new Dictionary<string, object?> { [HttpLoggingAttributes.Event] = HttpLoggingAttributes.EventStart }));

        // Assert — no exchange, no file.
        Directory.Exists(_directory).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C provider: Rolls to a new file when the UTC day changes")]
    public void Write_AcrossUtcDays_ShouldRollFiles()
    {
        // Arrange
        using W3CAccessLogProvider provider = new(CreateOptions());
        ILogger logger = ((ILoggerProvider)provider).Create("access");

        // Act
        logger.Log(CreateExchangeEntry(new DateTimeOffset(2026, 7, 11, 23, 59, 59, TimeSpan.Zero)));
        logger.Log(CreateExchangeEntry(new DateTimeOffset(2026, 7, 12, 0, 0, 1, TimeSpan.Zero)));

        // Assert
        File.Exists(Path.Combine(_directory, "access-20260711.log")).ShouldBeTrue();
        File.Exists(Path.Combine(_directory, "access-20260712.log")).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C provider: Rolls to a sequenced file when the size limit is exceeded")]
    public void Write_OverSizeLimit_ShouldRollToSequencedFile()
    {
        // Arrange — the limit is far below one directive block, so every entry rolls.
        using W3CAccessLogProvider provider = new(CreateOptions(options => options.FileSizeLimit = 16));
        ILogger logger = ((ILoggerProvider)provider).Create("access");
        DateTimeOffset timestamp = new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);

        // Act
        logger.Log(CreateExchangeEntry(timestamp, "/first"));
        logger.Log(CreateExchangeEntry(timestamp, "/second"));

        // Assert
        ReadLogFile(Path.Combine(_directory, "access-20260711.log")).ShouldContain("/first", Case.Sensitive);
        ReadLogFile(Path.Combine(_directory, "access-20260711.001.log")).ShouldContain("/second", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C provider: Deletes the oldest files beyond the retention limit")]
    public void Write_BeyondRetention_ShouldDeleteOldestFiles()
    {
        // Arrange
        using W3CAccessLogProvider provider = new(CreateOptions(options => options.RetainedFileCountLimit = 2));
        ILogger logger = ((ILoggerProvider)provider).Create("access");

        // Act — three UTC days; the roll to day three sweeps day one away.
        logger.Log(CreateExchangeEntry(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero)));
        logger.Log(CreateExchangeEntry(new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero)));
        logger.Log(CreateExchangeEntry(new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero)));

        // Assert
        File.Exists(Path.Combine(_directory, "access-20260710.log")).ShouldBeFalse();
        File.Exists(Path.Combine(_directory, "access-20260711.log")).ShouldBeTrue();
        File.Exists(Path.Combine(_directory, "access-20260712.log")).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C provider: Buffered output reaches disk on Flush and on dispose")]
    public void Write_Buffered_ShouldFlushOnDemandAndOnDispose()
    {
        // Arrange — a flush interval long enough that only explicit flushes matter.
        string file = Path.Combine(_directory, "access-20260711.log");
        W3CAccessLogProvider provider = new(CreateOptions(options => options.FlushInterval = TimeSpan.FromHours(1)));

        try
        {
            ILogger logger = ((ILoggerProvider)provider).Create("access");
            DateTimeOffset timestamp = new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);

            // Act + Assert — buffered: the first line may not be on disk yet.
            logger.Log(CreateExchangeEntry(timestamp, "/buffered"));
            provider.Flush();
            ReadLogFile(file).ShouldContain("/buffered", Case.Sensitive);

            logger.Log(CreateExchangeEntry(timestamp, "/on-dispose"));
            provider.Dispose();
            ReadLogFile(file).ShouldContain("/on-dispose", Case.Sensitive);
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C provider: NCSA formats write bare lines without directives")]
    public void Write_CommonFormat_ShouldNotWriteDirectives()
    {
        // Arrange
        using W3CAccessLogProvider provider = new(CreateOptions(options => options.Format = AccessLogFormat.Common));
        ILogger logger = ((ILoggerProvider)provider).Create("access");

        // Act
        logger.Log(CreateExchangeEntry(new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero)));

        // Assert
        string content = ReadLogFile(Path.Combine(_directory, "access-20260711.log"));
        content.ShouldNotContain("#Version");
        content.ShouldStartWith("- - - [11/Jul/2026:08:00:00 +0000] \"GET /orders -\" 200 -");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C provider: Constructor rejects invalid options")]
    public void Constructor_InvalidOptions_ShouldThrow()
    {
        Should.Throw<ArgumentNullException>(() => new W3CAccessLogProvider(null!));
        Should.Throw<ArgumentException>(() => new W3CAccessLogProvider(new W3CAccessLogOptions()));
        Should.Throw<ArgumentException>(() => new W3CAccessLogProvider(CreateOptions(options => options.FileNamePrefix = "")));
        Should.Throw<ArgumentException>(() => new W3CAccessLogProvider(CreateOptions(options => options.FileNamePrefix = "pre/fix")));
        Should.Throw<ArgumentException>(() => new W3CAccessLogProvider(CreateOptions(options => options.Format = (AccessLogFormat)99)));
        Should.Throw<ArgumentOutOfRangeException>(() => new W3CAccessLogProvider(CreateOptions(options => options.FileSizeLimit = 0)));
        Should.Throw<ArgumentOutOfRangeException>(() => new W3CAccessLogProvider(CreateOptions(options => options.RetainedFileCountLimit = 0)));
        Should.Throw<ArgumentOutOfRangeException>(() => new W3CAccessLogProvider(CreateOptions(options => options.FlushInterval = TimeSpan.FromSeconds(-1))));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C provider: Registers on a logger factory like any other provider")]
    public void Provider_OnLoggerFactory_ShouldReceiveFanOut()
    {
        // Arrange — the provider is an ordinary member of the logging pipeline.
        using W3CAccessLogProvider provider = new(CreateOptions());
        using ILoggerFactory factory = new LoggerFactoryBuilder().AddProvider(provider).Build();
        ILogger logger = factory.Create("access");

        // Act
        logger.Log(CreateExchangeEntry(new DateTimeOffset(2026, 7, 11, 9, 30, 0, TimeSpan.Zero), "/via-factory"));

        // Assert
        ReadLogFile(Path.Combine(_directory, "access-20260711.log")).ShouldContain("/via-factory", Case.Sensitive);
    }
}
