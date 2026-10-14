using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// Verifies that cancellation in a buffered response-starting callback never counts as a response.
/// </summary>
[Collection(nameof(TelemetryCollection))]
public class ResponseStartingCancellationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: Cancellation skips completion callbacks and reports a cancelled exchange")]
    [InlineData(WebApplicationTestProtocol.Http1)]
    [InlineData(WebApplicationTestProtocol.Http2)]
    public async Task OnStarting_CancellingCallback_ShouldSkipCompletionAndReportCancellation(WebApplicationTestProtocol protocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        using TelemetryRecorder recorder = new();
        string path = $"/starting-cancellation/{Guid.NewGuid():N}";
        int startingCallbacks = 0;
        int completionCallbacks = 0;

        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = protocol });
        factory.Application.Use(async (context, _) =>
        {
            context.Features.Get<IWebResponseCompletionFeature>()!.Register(() =>
            {
                Interlocked.Increment(ref completionCallbacks);
                return ValueTask.CompletedTask;
            });
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                Interlocked.Increment(ref startingCallbacks);
                context.Cancel();
                return Task.CompletedTask;
            }, null);
            context.Response.StatusCode = HttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("must-not-reach-client"), cancellation.Token);
        });
        using HttpClient client = factory.CreateClient();
        // A request body prevents the client retrying an HTTP/1.1 connection closed without a head.
        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Post, path)
        {
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy,
            Content = new StringContent("request"),
        };

        // Act
        await Should.ThrowAsync<HttpRequestException>(() => client.SendAsync(request, cancellation.Token));
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellation.Token);
        RecordedMeasurement duration = recorder.Measurements.Single(m => m.Instrument == "http.server.request.duration");

        // Assert — stopping the span follows completion callbacks, so their count is final.
        Volatile.Read(ref startingCallbacks).ShouldBe(1);
        Volatile.Read(ref completionCallbacks).ShouldBe(0);
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem("error.type").ShouldBe("request_canceled");
        span.GetTagItem("http.response.status_code").ShouldBeNull();
        duration.Tags.GetValueOrDefault("error.type").ShouldBe("request_canceled");
        duration.Tags.ContainsKey("http.response.status_code").ShouldBeFalse();
    }
}
