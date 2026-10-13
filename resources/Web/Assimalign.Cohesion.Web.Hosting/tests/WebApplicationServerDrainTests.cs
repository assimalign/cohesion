using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// The lame-duck drain of <see cref="WebApplicationServer.StopAsync"/> (#146): the stop takes nothing new
/// and lets the exchanges in flight finish within its budget, announcing the close to every peer, and
/// cancels only what outlives the budget. The first two tests pin the server against instrumented
/// doubles; the rest prove it end to end over the in-memory transport.
/// </summary>
public class WebApplicationServerDrainTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server/Drain: An HTTP/1.1 request in flight when the stop begins completes in full, with Connection: close")]
    public async Task StopAsync_Http1WithRequestInFlight_ShouldCompleteItWithConnectionClose()
    {
        // Arrange — the handler parks on a test-owned gate, so it is still running when the stop begins.
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestCancelled = default;

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use(async (context, next) =>
        {
            requestCancelled = context.RequestCancelled;
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("finished"), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();
        Task<HttpResponseMessage> request = client.GetAsync("/work", cancellationToken);
        await entered.Task.WaitAsync(cancellationToken);

        // Act — the drain begins while the request is in flight; nothing is cancelled.
        Task stop = factory.StopAsync(CancellationToken.None);
        requestCancelled.IsCancellationRequested.ShouldBeFalse();
        release.TrySetResult();

        // Assert — the full response arrives and announces the close (RFC 9112 §9.6).
        using HttpResponseMessage response = await request.WaitAsync(cancellationToken);
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Headers.ConnectionClose.ShouldBe(true);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("finished");
        await stop.WaitAsync(cancellationToken);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server/Drain: An HTTP/1.1 request that outlives the stop budget is cancelled and its connection aborted")]
    public async Task StopAsync_Http1WhenBudgetRunsOut_ShouldCancelTheRequestInFlight()
    {
        // Arrange — the handler runs until its request is cancelled.
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestCancelled = default;

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use(async (context, next) =>
        {
            requestCancelled = context.RequestCancelled;
            entered.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestCancelled);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        });

        using HttpClient client = factory.CreateClient();
        Task<HttpResponseMessage> request = client.GetAsync("/forever", cancellationToken);
        await entered.Task.WaitAsync(cancellationToken);

        using CancellationTokenSource budget = new();
        Task stop = factory.StopAsync(budget.Token);
        requestCancelled.IsCancellationRequested.ShouldBeFalse();

        // Act — the budget runs out with the request still running.
        budget.Cancel();

        // Assert — the request observes RequestCancelled, the client gets no response (the in-memory
        // pair hands the client the server's abort reason as is), and the stop completes.
        await cancelled.Task.WaitAsync(cancellationToken);
        Exception failure = await Should.ThrowAsync<Exception>(() => request.WaitAsync(cancellationToken));
        (failure is HttpRequestException or ConnectionAbortedException).ShouldBeTrue(failure.ToString());

        // Awaited directly: Shouldly's NotThrowAsync counts a cancelled task as a pass, so a stop that hung
        // until the guard token fired would have passed.
        await stop.WaitAsync(cancellationToken);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server/Drain: Every exchange carries the drain signal, which fires when the stop begins and cancels nothing")]
    public async Task StopAsync_WithExchangeInFlight_ShouldSignalTheDrainThroughTheExchangeFeature()
    {
        // Arrange — the handler parks with the drain feature it was given.
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;

        TaskCompletionSource<IWebServerDrainFeature?> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestCancelled = default;

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use(async (context, next) =>
        {
            IWebServerDrainFeature? drain = context.Features.Get<IWebServerDrainFeature>();
            requestCancelled = context.RequestCancelled;
            entered.TrySetResult(drain);
            await release.Task.WaitAsync(cancellationToken);

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            string state = drain is { Draining.IsCancellationRequested: true } ? "draining" : "serving";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(state), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();
        Task<HttpResponseMessage> request = client.GetAsync("/long-lived", cancellationToken);
        IWebServerDrainFeature? feature = await entered.Task.WaitAsync(cancellationToken);
        feature.ShouldNotBeNull();
        feature.Draining.IsCancellationRequested.ShouldBeFalse();

        TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = feature.Draining.Register(drained.SetResult);

        // Act
        Task stop = factory.StopAsync(CancellationToken.None);

        // Assert — the signal fires while the exchange keeps running, and its response is delivered.
        await drained.Task.WaitAsync(cancellationToken);
        requestCancelled.IsCancellationRequested.ShouldBeFalse();
        release.TrySetResult();

        using HttpResponseMessage response = await request.WaitAsync(cancellationToken);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("draining");
        await stop.WaitAsync(cancellationToken);
    }

}
