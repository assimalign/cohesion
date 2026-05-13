using System;
using System.Collections.Generic;
using System.IO;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Logging.Console;

namespace Assimalign.Cohesion.Logging.Console.Tests;

public class ConsoleLoggerProviderTests
{
    [Fact(DisplayName = "Cohesion Test [Logging.Console] - Provider: emits to configured writer")]
    public void EmitsToWriter()
    {
        var output = new StringWriter();
        var options = new ConsoleLoggerOptions { Output = output, ErrorOutput = new StringWriter() };
        using var provider = new ConsoleLoggerProvider(options);

        var logger = provider.Create("App");
        logger.LogInformation("App", "hello");

        var text = output.ToString();
        Assert.Contains("INFO", text);
        Assert.Contains("App", text);
        Assert.Contains("hello", text);
    }

    [Fact(DisplayName = "Cohesion Test [Logging.Console] - Provider: error-level routes to ErrorOutput")]
    public void ErrorRoutesToErrorOutput()
    {
        var standard = new StringWriter();
        var error = new StringWriter();
        var options = new ConsoleLoggerOptions { Output = standard, ErrorOutput = error };
        using var provider = new ConsoleLoggerProvider(options);

        provider.Create("App").LogError("App", "oh no");

        Assert.DoesNotContain("oh no", standard.ToString());
        Assert.Contains("oh no", error.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Logging.Console] - Provider: emits attributes by default")]
    public void EmitsAttributes()
    {
        var output = new StringWriter();
        using var provider = new ConsoleLoggerProvider(new ConsoleLoggerOptions
        {
            Output = output,
            ErrorOutput = new StringWriter(),
        });

        provider.Create("App").LogInformation(
            "App",
            "hello",
            attributes: new Dictionary<string, object?> { ["userId"] = 42 });

        var text = output.ToString();
        Assert.Contains("userId=42", text);
    }

    [Fact(DisplayName = "Cohesion Test [Logging.Console] - Provider: IncludeAttributes=false suppresses attributes")]
    public void SuppressesAttributes()
    {
        var output = new StringWriter();
        using var provider = new ConsoleLoggerProvider(new ConsoleLoggerOptions
        {
            Output = output,
            ErrorOutput = new StringWriter(),
            IncludeAttributes = false,
        });

        provider.Create("App").LogInformation(
            "App",
            "hello",
            attributes: new Dictionary<string, object?> { ["userId"] = 42 });

        Assert.DoesNotContain("userId", output.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Logging.Console] - Provider: writes exception when IncludeException=true")]
    public void WritesException()
    {
        var error = new StringWriter();
        using var provider = new ConsoleLoggerProvider(new ConsoleLoggerOptions
        {
            Output = new StringWriter(),
            ErrorOutput = error,
        });

        var exception = new InvalidOperationException("boom");
        provider.Create("App").LogError("App", "uh oh", exception);

        var text = error.ToString();
        Assert.Contains("boom", text);
    }

    [Fact(DisplayName = "Cohesion Test [Logging.Console] - Provider: custom formatter overrides built-in")]
    public void CustomFormatter()
    {
        var output = new StringWriter();
        using var provider = new ConsoleLoggerProvider(new ConsoleLoggerOptions
        {
            Output = output,
            ErrorOutput = new StringWriter(),
            Formatter = (entry, writer) => writer.WriteLine($"<<{entry.Level}|{entry.Message}>>"),
        });

        provider.Create("App").LogInformation("App", "hello");

        Assert.Contains("<<Information|hello>>", output.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Logging.Console] - Provider: name is Console")]
    public void Name_IsConsole()
    {
        using var provider = new ConsoleLoggerProvider(new ConsoleLoggerOptions
        {
            Output = new StringWriter(),
            ErrorOutput = new StringWriter(),
        });

        Assert.Equal("Console", provider.Name);
    }

    [Fact(DisplayName = "Cohesion Test [Logging.Console] - Provider: throws after dispose")]
    public void DisposedProvider_Throws()
    {
        var provider = new ConsoleLoggerProvider(new ConsoleLoggerOptions
        {
            Output = new StringWriter(),
            ErrorOutput = new StringWriter(),
        });
        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() => provider.Create("App"));
    }

    [Fact(DisplayName = "Cohesion Test [Logging.Console] - Provider: scope chains parent id through composite")]
    public void Scope_ChainsParentId()
    {
        var output = new StringWriter();
        using var provider = new ConsoleLoggerProvider(new ConsoleLoggerOptions
        {
            Output = output,
            ErrorOutput = new StringWriter(),
            IncludeParentId = true,
        });
        using var factory = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .SetMinimumLevel(LogLevel.Trace)
            .Build();

        var logger = factory.Create("App");
        var seed = new LogEntry(LogLevel.Information, "App", "scope");
        using var scope = logger.BeginScope(seed);
        scope.LogInformation("App", "inner");

        var text = output.ToString();
        Assert.Contains($"parentId={seed.Id}", text);
    }

    [Fact(DisplayName = "Cohesion Test [Logging.Console] - Provider: Create rejects empty category")]
    public void Create_RejectsEmptyCategory()
    {
        using var provider = new ConsoleLoggerProvider(new ConsoleLoggerOptions
        {
            Output = new StringWriter(),
            ErrorOutput = new StringWriter(),
        });

        Assert.Throws<ArgumentException>(() => provider.Create(""));
        Assert.Throws<ArgumentNullException>(() => provider.Create(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging.Console] - Provider: ctor rejects null options")]
    public void Ctor_NullOptionsThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ConsoleLoggerProvider(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging.Console] - Provider: throwing custom formatter does not bring down provider")]
    public void ThrowingFormatter_Isolated()
    {
        var output = new StringWriter();
        using var provider = new ConsoleLoggerProvider(new ConsoleLoggerOptions
        {
            Output = output,
            ErrorOutput = new StringWriter(),
            Formatter = (_, _) => throw new InvalidOperationException("bad formatter"),
        });

        provider.Create("App").LogInformation("App", "hello");
        // No throw; output may be empty because the formatter aborted, but the provider keeps working.
    }
}
