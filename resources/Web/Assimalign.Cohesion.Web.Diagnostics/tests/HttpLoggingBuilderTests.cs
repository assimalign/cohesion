using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

/// <summary>
/// Registration and placement ("Add configures, Use places", owner decision of 2026-10-28):
/// <see cref="HttpLoggingBuilder"/> requires the logger the entries are written to and freezes the options
/// it built the middleware with, and <c>UseHttpLogging()</c> places the last registration or, without one,
/// fails the pipeline's composition.
/// </summary>
public class HttpLoggingBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - HttpLoggingBuilder: Build without a logger should fail with a message naming the logger verbs")]
    public void Build_WithoutLogger_ShouldThrowInvalidOperation()
    {
        // Arrange
        HttpLoggingBuilder logging = new();

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => logging.Build());

        // Assert
        exception.Message.ShouldContain("UseLoggerFactory", Case.Sensitive);
        exception.Message.ShouldContain("AddHttpLogging", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - HttpLoggingBuilder: A null logger or logger factory should be rejected")]
    public void UseLogger_NullArguments_ShouldThrow()
    {
        // Arrange
        HttpLoggingBuilder logging = new();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => logging.UseLogger(null!));
        Should.Throw<ArgumentNullException>(() => logging.UseLoggerFactory(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - HttpLoggingBuilder: Invalid options should fail when the middleware is built")]
    public void Build_InvalidOptions_ShouldThrow()
    {
        // Arrange
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(new RecordingLoggerProvider()).Build();
        HttpLoggingBuilder silent = new HttpLoggingBuilder().UseLoggerFactory(loggerFactory);
        silent.Options.Level = LogLevel.None;
        HttpLoggingBuilder negative = new HttpLoggingBuilder().UseLoggerFactory(loggerFactory);
        negative.Options.RequestBodyLimit = -1;

        // Act & Assert
        Should.Throw<ArgumentException>(() => silent.Build());
        Should.Throw<ArgumentOutOfRangeException>(() => negative.Build());
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - HttpLoggingBuilder: A supplied logger should receive the entries")]
    public async Task Build_WithLogger_ShouldWriteThroughTheSuppliedLogger()
    {
        // Arrange
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        IWebApplicationMiddleware middleware = new HttpLoggingBuilder()
            .UseLogger(loggerFactory.Create(new HttpLoggingOptions().Category))
            .Build();

        // Act
        await middleware.InvokeAsync(new LoggingTestContext(new HttpConnectionInfo()), _ => Task.CompletedTask);

        // Assert
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Attributes[HttpLoggingAttributes.Event].ShouldBe(HttpLoggingAttributes.EventExchange);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - HttpLoggingBuilder: A logger factory should create the logger for the configured category")]
    public async Task Build_WithLoggerFactory_ShouldCreateTheLoggerForTheConfiguredCategory()
    {
        // Arrange — a filter rule raises one category's minimum level above the entries' level, so only a
        // logger created for the other category writes.
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder()
            .AddProvider(recorded)
            .AddRule("Contoso.Quiet", LogLevel.Warning)
            .Build();
        HttpLoggingBuilder quiet = new HttpLoggingBuilder().UseLoggerFactory(loggerFactory);
        quiet.Options.Category = "Contoso.Quiet";
        HttpLoggingBuilder loud = new HttpLoggingBuilder().UseLoggerFactory(loggerFactory);
        loud.Options.Category = "Contoso.Loud";

        // Act
        await quiet.Build().InvokeAsync(new LoggingTestContext(new HttpConnectionInfo()), _ => Task.CompletedTask);
        await loud.Build().InvokeAsync(new LoggingTestContext(new HttpConnectionInfo()), _ => Task.CompletedTask);

        // Assert
        recorded.Entries.ShouldHaveSingleItem().Category.ShouldBe("Contoso.Loud");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - HttpLoggingBuilder: A later logger call should replace an earlier one")]
    public async Task UseLoggerFactory_AfterUseLogger_ShouldReplaceIt()
    {
        // Arrange
        RecordingLoggerProvider first = new();
        RecordingLoggerProvider last = new();
        using ILoggerFactory firstFactory = new LoggerFactoryBuilder().AddProvider(first).Build();
        using ILoggerFactory lastFactory = new LoggerFactoryBuilder().AddProvider(last).Build();
        IWebApplicationMiddleware middleware = new HttpLoggingBuilder()
            .UseLogger(firstFactory.Create(new HttpLoggingOptions().Category))
            .UseLoggerFactory(lastFactory)
            .Build();

        // Act
        await middleware.InvokeAsync(new LoggingTestContext(new HttpConnectionInfo()), _ => Task.CompletedTask);

        // Assert
        first.Entries.ShouldBeEmpty();
        last.Entries.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - HttpLoggingBuilder: Options changed after Build should not reach the built middleware")]
    public async Task Build_OptionsChangedAfterBuild_ShouldKeepTheSnapshot()
    {
        // Arrange
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        HttpLoggingBuilder logging = new HttpLoggingBuilder().UseLoggerFactory(loggerFactory);
        IWebApplicationMiddleware middleware = logging.Build();
        logging.Options.Fields = HttpLoggingFields.RequestMethod;

        // Act
        await middleware.InvokeAsync(new LoggingTestContext(new HttpConnectionInfo()), _ => Task.CompletedTask);

        // Assert — the default field set the middleware was built with still logs the path.
        recorded.Entries.ShouldHaveSingleItem().Attributes[HttpLoggingAttributes.RequestPath].ShouldBe("/orders");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - UseHttpLogging: Should place the middleware the application registered")]
    public async Task UseHttpLogging_WithRegisteredMiddleware_ShouldPlaceIt()
    {
        // Arrange
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        TestPipelineBuilder pipeline = new();
        pipeline.Registered.Add(new HttpLoggingBuilder().UseLoggerFactory(loggerFactory).Build());

        // Act
        pipeline.UseHttpLogging();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(new LoggingTestContext(new HttpConnectionInfo()));

        // Assert
        recorded.Entries.ShouldHaveSingleItem().Attributes[HttpLoggingAttributes.Event].ShouldBe(HttpLoggingAttributes.EventExchange);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - UseHttpLogging: Should place the last registration when HTTP logging is registered twice")]
    public async Task UseHttpLogging_WithTwoRegistrations_ShouldPlaceTheLast()
    {
        // Arrange
        RecordingLoggerProvider first = new();
        RecordingLoggerProvider last = new();
        using ILoggerFactory firstFactory = new LoggerFactoryBuilder().AddProvider(first).Build();
        using ILoggerFactory lastFactory = new LoggerFactoryBuilder().AddProvider(last).Build();
        TestPipelineBuilder pipeline = new();
        pipeline.Registered.Add(new HttpLoggingBuilder().UseLoggerFactory(firstFactory).Build());
        pipeline.Registered.Add(new HttpLoggingBuilder().UseLoggerFactory(lastFactory).Build());

        // Act
        pipeline.UseHttpLogging();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(new LoggingTestContext(new HttpConnectionInfo()));

        // Assert
        first.Entries.ShouldBeEmpty();
        last.Entries.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - UseHttpLogging: Should fail the pipeline's composition when no HTTP logging is registered")]
    public void UseHttpLogging_WithoutRegistration_ShouldFailComposition()
    {
        // Arrange — there is no default logger, so there is nothing to place. The test pipeline composes a
        // context-aware verb when it is called.
        TestPipelineBuilder pipeline = new();

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => pipeline.UseHttpLogging());

        // Assert
        exception.Message.ShouldContain("AddHttpLogging", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - UseHttpLogging: A null pipeline builder should be rejected")]
    public void UseHttpLogging_NullBuilder_ShouldThrow()
    {
        // Arrange
        IWebApplicationPipelineBuilder pipeline = null!;

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => pipeline.UseHttpLogging());
    }
}
