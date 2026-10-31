using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

public class HttpLoggingCancellationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Diagnostics] - Cancellation: an application cancellation exception with a live request remains an Error")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_OperationCanceledWithoutRequestCancellation_ShouldLogApplicationFaultAndRethrowAsync(bool unrelatedTokenCancelled)
    {
        // Arrange — an application's dependency can cancel independently of the HTTP request.
        // The exception's token does not establish that the transport cancelled this exchange.
        using var operationCancellation = new CancellationTokenSource();
        if (unrelatedTokenCancelled)
        {
            operationCancellation.Cancel();
        }
        var exception = new OperationCanceledException("The application operation was cancelled.", operationCancellation.Token);
        var recorded = new RecordingLoggerProvider();
        using ILoggerFactory factory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        IWebApplicationMiddleware middleware = new HttpLoggingBuilder().UseLoggerFactory(factory).Build();
        var context = new LoggingTestContext(new HttpConnectionInfo());

        // Act
        OperationCanceledException? thrown = null;
        try
        {
            await middleware.InvokeAsync(context, _ => Task.FromException(exception));
        }
        catch (OperationCanceledException failure)
        {
            // Observe the exception directly: a task-based assertion wrapper may substitute
            // TaskCanceledException when it sees the middleware's canceled task.
            thrown = failure;
        }

        // Assert
        context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
        thrown.ShouldBeSameAs(exception);
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeSameAs(exception);
        entry.Message.ShouldEndWith("(faulted)");
        entry.Attributes[HttpLoggingAttributes.Event].ShouldBe(HttpLoggingAttributes.EventExchange);
        entry.Attributes.ContainsKey(HttpLoggingAttributes.ClientFault).ShouldBeFalse();
    }
}
