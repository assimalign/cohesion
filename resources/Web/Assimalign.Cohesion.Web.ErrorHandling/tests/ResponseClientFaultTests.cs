using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.ErrorHandling.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.ErrorHandling.Tests;

/// <summary>Response-side client outcomes are classified by the transport report, not exception type.</summary>
public class ResponseClientFaultTests
{
    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Response client fault: A wrapped transport failure should cancel without fault observation or error handlers")]
    public async Task InvokeAsync_ResponseClientFault_ShouldCancelWithTheExchangeTokenWithoutObserving()
    {
        // Arrange
        using CancellationTokenSource request = new();
        await request.CancelAsync();
        InvalidOperationException wrapped = new("The response writer wrapped its failure.");
        int observed = 0;
        RecordingErrorHandler handler = new(handles: true);
        TestHttpContext context = new() { RequestCancelled = request.Token };
        context.Features.Set<IWebResponseClientFaultFeature>(new FakeResponseClientFaultFeature(true));
        context.Features.Set(new ErrorHandlingBuilder().OnError(handler).Build());
        TestPipelineBuilder builder = new();
        builder.UseErrorHandling(options => options.OnException = (_, _) =>
        {
            observed++;
            return ValueTask.CompletedTask;
        });
        builder.Run(_ => throw wrapped);

        // Act — await directly: Shouldly 4.3 synthesizes TaskCanceledException for a cancelled task,
        // which drops the actual exception's inner exception.
        OperationCanceledException? caught = null;
        try
        {
            await builder.Build().ExecuteAsync(context, CancellationToken.None);
        }
        catch (OperationCanceledException exception)
        {
            caught = exception;
        }

        // Assert
        OperationCanceledException failure = caught.ShouldNotBeNull();
        failure.CancellationToken.ShouldBe(request.Token);
        failure.InnerException.ShouldBeSameAs(wrapped);
        context.CancelRequested.ShouldBeTrue();
        context.Features.Get<IHttpExceptionFeature>().ShouldBeNull();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ResponseBodyText().ShouldBeEmpty();
        observed.ShouldBe(0);
        handler.WasConsulted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Response client fault: An unset latch should preserve application fault handling")]
    public async Task InvokeAsync_NoResponseClientFault_ShouldObserveTheApplicationFailure()
    {
        // Arrange — this companion pins the existing application-fault behavior.
        InvalidOperationException fault = new("Application failure.");
        Exception? observed = null;
        TestHttpContext context = new();
        context.Features.Set<IWebResponseClientFaultFeature>(new FakeResponseClientFaultFeature(false));
        TestPipelineBuilder builder = new();
        builder.UseErrorHandling(options => options.OnException = (_, exception) =>
        {
            observed = exception;
            return ValueTask.CompletedTask;
        });
        builder.Run(_ => throw fault);

        // Act
        await builder.Build().ExecuteAsync(context, CancellationToken.None);

        // Assert
        observed.ShouldBeSameAs(fault);
        context.CancelRequested.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    private sealed class FakeResponseClientFaultFeature : IWebResponseClientFaultFeature
    {
        public FakeResponseClientFaultFeature(bool responseAbortedByClient)
        {
            ResponseAbortedByClient = responseAbortedByClient;
        }

        public string Name => nameof(IWebResponseClientFaultFeature);

        public bool ResponseAbortedByClient { get; }
    }
}
