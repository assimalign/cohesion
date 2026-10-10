using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// The default server's telemetry (#1064), end to end over the in-memory transport: one server span
/// per request from the <c>Assimalign.Cohesion.Web.Hosting</c> activity source, parented to the
/// caller's W3C trace context and tagged per the OpenTelemetry HTTP server conventions; the
/// <c>http.server.request.duration</c> and <c>http.server.active_requests</c> instruments; the request
/// id; and nothing at all when nobody listens.
/// </summary>
[Collection(nameof(TelemetryCollection))]
public class WebServerTelemetryTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: The source and the meter should be named for the assembly")]
    public void Name_SourceAndMeter_ShouldBeTheAssemblyName()
    {
        // Act
        string? assemblyName = typeof(WebApplication).Assembly.GetName().Name;

        // Assert
        WebServerTelemetry.Name.ShouldBe("Assimalign.Cohesion.Web.Hosting");
        WebServerTelemetry.Name.ShouldBe(assemblyName);
        WebServerTelemetry.Source.Name.ShouldBe(WebServerTelemetry.Name);
        WebServerTelemetry.Meter.Name.ShouldBe(WebServerTelemetry.Name);
        WebServerTelemetry.RequestDuration.Unit.ShouldBe("s");
        WebServerTelemetry.ActiveRequests.Unit.ShouldBe("{request}");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A request with a traceparent should get one server span parented to the caller")]
    public async Task ServerSpan_RequestWithTraceParent_ShouldBeOneServerSpanParentedToTheCaller()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        ActivityTraceId traceId = ActivityTraceId.CreateRandom();
        ActivitySpanId callerSpanId = ActivitySpanId.CreateRandom();
        Activity? currentInPipeline = null;
        ActivityTraceId requestIdInPipeline = default;

        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
        {
            BaseAddress = new Uri("http://localhost:5080/"),
        });

        factory.Application.Use(async (context, next) =>
        {
            currentInPipeline = Activity.Current;
            requestIdInPipeline = context.Features.Get<IWebRequestIdFeature>()!.RequestId;
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("ok"), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(NetHttpMethod.Get, "/telemetry/parented");
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId.ToHexString()}-{callerSpanId.ToHexString()}-01");
        request.Headers.TryAddWithoutValidation("tracestate", "cohesion=1");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => a.TraceId == traceId, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        recorder.Stopped.Where(a => a.TraceId == traceId).ShouldHaveSingleItem();

        span.Source.Name.ShouldBe("Assimalign.Cohesion.Web.Hosting");
        span.Kind.ShouldBe(ActivityKind.Server);
        span.ParentSpanId.ShouldBe(callerSpanId);
        span.HasRemoteParent.ShouldBeTrue();
        span.TraceStateString.ShouldBe("cohesion=1");
        span.DisplayName.ShouldBe("GET");
        span.Status.ShouldBe(ActivityStatusCode.Unset);

        span.GetTagItem("http.request.method").ShouldBe("GET");
        span.GetTagItem("url.path").ShouldBe("/telemetry/parented");
        span.GetTagItem("url.scheme").ShouldBe("http");
        span.GetTagItem("server.address").ShouldBe("localhost");
        span.GetTagItem("server.port").ShouldBe(5080);
        span.GetTagItem("network.protocol.version").ShouldBe("1.1");
        span.GetTagItem("http.response.status_code").ShouldBe(200);
        span.GetTagItem("http.route").ShouldBeNull();
        span.GetTagItem("error.type").ShouldBeNull();
        span.GetTagItem("http.request.method_original").ShouldBeNull();

        // The span is current while the pipeline runs, and the request id is its trace id.
        currentInPipeline.ShouldBeSameAs(span);
        requestIdInPipeline.ShouldBe(traceId);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A routed request should name its span and duration by the route template")]
    public async Task ServerSpan_RoutedRequest_ShouldCarryTheRouteTemplateOnSpanAndDuration()
    {
        // Arrange — a grouped route: routing composes the group's prefix into the template, which the
        // server reports with its leading '/'.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new();

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Application.UseRouting()
            .MapGroup("/telemetry")
            .Map(CohesionHttpMethod.Get, "orders/{id:int}", new RouterRouteHandler(async context =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("order"), context.RequestCancelled);
            }));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/telemetry/orders/42", cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), "/telemetry/orders/42"), cancellationToken);
        RecordedMeasurement duration = await recorder.WaitForMeasurementAsync(
            m => m.Instrument == "http.server.request.duration" && Equals(m.Tags.GetValueOrDefault("http.route"), "/telemetry/orders/{id:int}"),
            cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        span.DisplayName.ShouldBe("GET /telemetry/orders/{id:int}");
        span.GetTagItem("http.route").ShouldBe("/telemetry/orders/{id:int}");
        span.GetTagItem("http.response.status_code").ShouldBe(200);

        duration.Tags["http.request.method"].ShouldBe("GET");
        duration.Tags["url.scheme"].ShouldBe("http");
        duration.Tags["http.response.status_code"].ShouldBe(200);
        duration.Tags["network.protocol.version"].ShouldBe("1.1");
        duration.Tags.ContainsKey("error.type").ShouldBeFalse();

        // The span and the metric measure the same interval.
        duration.Value.ShouldBeGreaterThan(0);
        span.Duration.TotalSeconds.ShouldBe(duration.Value);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: With no listener the server should create no activity")]
    public async Task ServerSpan_NoListener_ShouldCreateNoActivity()
    {
        // Arrange — no recorder: nothing subscribes to the server's source or meter.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        ActivityTraceId traceId = ActivityTraceId.CreateRandom();
        bool pipelineRan = false;
        Activity? currentInPipeline = null;
        ActivityTraceId requestIdInPipeline = default;

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use((context, next) =>
        {
            pipelineRan = true;
            currentInPipeline = Activity.Current;
            requestIdInPipeline = context.Features.Get<IWebRequestIdFeature>()!.RequestId;
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(NetHttpMethod.Get, "/telemetry/unobserved");
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        // Assert
        WebServerTelemetry.Source.HasListeners().ShouldBeFalse();
        WebServerTelemetry.RequestDuration.Enabled.ShouldBeFalse();
        WebServerTelemetry.ActiveRequests.Enabled.ShouldBeFalse();
        response.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        pipelineRan.ShouldBeTrue();
        currentInPipeline.ShouldBeNull();

        // Without a span the request id still follows the caller's trace.
        requestIdInPipeline.ShouldBe(traceId);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: Without a span or traceparent the request id should be random and stable per request")]
    public async Task RequestId_NoListenerAndNoTraceParent_ShouldBeRandomAndStablePerRequest()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        ActivityTraceId[] firstReads = new ActivityTraceId[2];
        ActivityTraceId[] secondReads = new ActivityTraceId[2];
        int requests = 0;

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use((context, next) =>
        {
            int index = Interlocked.Increment(ref requests) - 1;
            IWebRequestIdFeature feature = context.Features.Get<IWebRequestIdFeature>()!;
            firstReads[index] = feature.RequestId;
            secondReads[index] = feature.RequestId;
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/telemetry/random/1", cancellationToken);
        using HttpResponseMessage second = await client.GetAsync("/telemetry/random/2", cancellationToken);

        // Assert
        requests.ShouldBe(2);
        firstReads[0].ShouldNotBe(default);
        firstReads[1].ShouldNotBe(default);
        secondReads[0].ShouldBe(firstReads[0]);
        secondReads[1].ShouldBe(firstReads[1]);
        firstReads[1].ShouldNotBe(firstReads[0]);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: An invalid traceparent should start a new trace")]
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01")]
    [InlineData("ff-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("not-a-trace-context")]
    public async Task ServerSpan_InvalidTraceParent_ShouldStartANewTrace(string traceParent)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        ActivityTraceId requestIdInPipeline = default;
        string path = $"/telemetry/invalid/{Guid.NewGuid():N}";

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use((context, next) =>
        {
            requestIdInPipeline = context.Features.Get<IWebRequestIdFeature>()!.RequestId;
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(NetHttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("traceparent", traceParent);

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        span.ParentSpanId.ShouldBe(default);
        span.HasRemoteParent.ShouldBeFalse();
        span.TraceId.ShouldNotBe(default);
        span.TraceId.ToHexString().ShouldNotBe("4bf92f3577b34da6a3ce929d0e0e4736");
        requestIdInPipeline.ShouldBe(span.TraceId);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: The activity current when the server started should not parent its requests")]
    public async Task ServerSpan_AmbientActivityAtServerStart_ShouldNotParentRequests()
    {
        // Arrange — the server starts while an activity is current, so its accept loop inherits it. A
        // request's span must still start a trace of its own.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        string path = $"/telemetry/ambient/{Guid.NewGuid():N}";

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            return Task.CompletedTask;
        });

        HttpClient client;
        ActivityTraceId ambientTraceId;

        using (Activity ambient = new Activity("server-start").Start())
        {
            ambientTraceId = ambient.TraceId;
            client = factory.CreateClient();
        }

        using (client)
        {
            // Act — the client sends with no activity current, so it propagates no trace context.
            using HttpResponseMessage response = await client.GetAsync(path, cancellationToken);
            Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

            // Assert
            response.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
            span.Parent.ShouldBeNull();
            span.ParentSpanId.ShouldBe(default);
            span.TraceId.ShouldNotBe(ambientTraceId);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: The meter should record the duration and balance the active-request count")]
    public async Task RequestDuration_Request_ShouldRecordDurationAndBalanceActiveRequests()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(traces: false);

        double activeInPipeline = double.NaN;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Application.UseRouting().Map(CohesionHttpMethod.Get, "/telemetry/metrics/{id}", new RouterRouteHandler(context =>
        {
            activeInPipeline = SumActiveRequests(recorder);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        }));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/telemetry/metrics/7", cancellationToken);
        RecordedMeasurement duration = await recorder.WaitForMeasurementAsync(
            m => m.Instrument == "http.server.request.duration" && Equals(m.Tags.GetValueOrDefault("http.route"), "/telemetry/metrics/{id}"),
            cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        duration.Value.ShouldBeGreaterThan(0);
        duration.Tags.Count.ShouldBe(5);
        duration.Tags["http.request.method"].ShouldBe("GET");
        duration.Tags["url.scheme"].ShouldBe("http");
        duration.Tags["http.response.status_code"].ShouldBe(200);
        duration.Tags["network.protocol.version"].ShouldBe("1.1");

        // One request was active while the pipeline ran, and none once it was recorded.
        activeInPipeline.ShouldBe(1);
        SumActiveRequests(recorder).ShouldBe(0);

        RecordedMeasurement[] active = recorder.Measurements.Where(m => m.Instrument == "http.server.active_requests").ToArray();
        active.Length.ShouldBe(2);
        active.ShouldAllBe(m => m.Tags.Count == 2 && Equals(m.Tags["http.request.method"], "GET") && Equals(m.Tags["url.scheme"], "http"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A pipeline fault should report the replacement 500 as an error")]
    public async Task ServerSpan_PipelineFault_ShouldReportTheReplacement500AsAnError()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new();

        string path = $"/telemetry/fault/{Guid.NewGuid():N}";

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use((context, next) => throw new InvalidOperationException("Deliberate pipeline fault."));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync(path, cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);
        RecordedMeasurement duration = await recorder.WaitForMeasurementAsync(
            m => m.Instrument == "http.server.request.duration" && Equals(m.Tags.GetValueOrDefault("http.response.status_code"), 500),
            cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem("http.response.status_code").ShouldBe(500);
        span.GetTagItem("error.type").ShouldBe("500");
        duration.Tags["error.type"].ShouldBe("500");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A client error should not mark the server span as failed")]
    public async Task ServerSpan_ClientError_ShouldLeaveTheStatusUnset()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        string path = $"/telemetry/missing/{Guid.NewGuid():N}";

        await using WebApplicationTestFactory factory = new();
        using HttpClient client = factory.CreateClient();

        // Act — no middleware answers, so the terminal ends the request with a 404.
        using HttpResponseMessage response = await client.GetAsync(path, cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        span.GetTagItem("http.response.status_code").ShouldBe(404);
        span.GetTagItem("error.type").ShouldBeNull();
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A client fault found reading the body should report the transport's status, not a 500 error")]
    [InlineData("Transfer-Encoding: chunked\r\n\r\nzz\r\nabc\r\n0\r\n\r\n", 400)]
    [InlineData("Content-Length: 32\r\n\r\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 413)]
    public async Task ServerSpan_ClientFaultReadingTheBody_ShouldReportTheTransportStatusWithoutAnError(string framingAndBody, int expectedStatus)
    {
        // Arrange — the pipeline reads the body and lets the failed read escape, so the server's fault
        // boundary stages a 500 that the transport replaces with its own status (#1333, #1339, #1340).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new();

        string path = $"/telemetry/client-fault/{Guid.NewGuid():N}";

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp1(transport, http1 => http1.Limits.MaxRequestBodySize = 16));
        factory.Application.Use(async (context, next) =>
        {
            byte[] buffer = new byte[256];

            while (await context.Request.Body.ReadAsync(buffer, context.RequestCancelled) > 0)
            {
            }

            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
        });

        await factory.StartAsync(cancellationToken);

        await using Connection client = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
        Stream stream = client.AsStream();

        // Act
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST {path} HTTP/1.1\r\nHost: localhost\r\n{framingAndBody}"), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);
        RecordedMeasurement duration = await recorder.WaitForMeasurementAsync(
            m => m.Instrument == "http.server.request.duration" && Equals(m.Tags.GetValueOrDefault("http.response.status_code"), expectedStatus),
            cancellationToken);

        // Assert
        span.GetTagItem("http.response.status_code").ShouldBe(expectedStatus);
        span.GetTagItem("error.type").ShouldBeNull();
        span.Status.ShouldBe(ActivityStatusCode.Unset);
        duration.Tags.ContainsKey("error.type").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: An HTTP/2 body below the minimum data rate should report the transport's 408, not a 500 error")]
    public async Task ServerSpan_Http2BodyBelowMinimumDataRate_ShouldReportTheTransport408WithoutAnError()
    {
        // Arrange — the pipeline reads a body the client never sends and lets the failed read escape, so
        // the server's fault boundary stages a 500. The transport has already answered the stream with
        // 408 and reset it (#1085), so the 500 never reaches the wire and must not be what is recorded.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new();

        string path = $"/telemetry/h2-slow-body/{Guid.NewGuid():N}";

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp2(
            transport,
            http2 => http2.Limits.MinRequestBodyDataRate = new HttpMinDataRate(bytesPerSecond: 1000, gracePeriod: TimeSpan.FromMilliseconds(100))));
        factory.Application.Use(async (context, next) =>
        {
            byte[] buffer = new byte[256];

            while (await context.Request.Body.ReadAsync(buffer, context.RequestCancelled) > 0)
            {
            }

            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
        });

        await factory.StartAsync(cancellationToken);

        await using Http2RawClient client = await Http2RawClient.ConnectAsync(transport, cancellationToken);

        // Act — the HEADERS frame leaves the stream open for a body that never comes.
        await client.SendPostHeadersAsync(1, path, cancellationToken);
        Http2RawFrame answer = await client.ReadUntilAsync(frame => frame.StreamId == 1 && frame.Type == Http2RawFrame.HeadersType, cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // The span stops after the duration is recorded, so the exchange's measurement is already in.
        RecordedMeasurement duration = recorder.Measurements.Single(
            m => m.Instrument == "http.server.request.duration" && Equals(m.Tags.GetValueOrDefault("network.protocol.version"), "2"));

        // Assert — the transport's 408 is the stream's only response, and it is what the server reports.
        answer.EndStream.ShouldBeTrue();
        client.Frames.Count(frame => frame.StreamId == 1 && frame.Type == Http2RawFrame.HeadersType).ShouldBe(1);
        span.GetTagItem("network.protocol.version").ShouldBe("2");
        span.GetTagItem("http.response.status_code").ShouldBe(408);
        span.GetTagItem("error.type").ShouldBeNull();
        span.Status.ShouldBe(ActivityStatusCode.Unset);
        duration.Tags.GetValueOrDefault("http.response.status_code").ShouldBe(408);
        duration.Tags.ContainsKey("error.type").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A cancelled exchange should report request_canceled and no status code")]
    public async Task ServerSpan_CanceledExchange_ShouldReportRequestCanceled()
    {
        // Arrange — the application cancels its own exchange, which the server resets: an HTTP/1.1
        // connection ends with no response.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new();

        string path = $"/telemetry/canceled/{Guid.NewGuid():N}";

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use(async (context, next) =>
        {
            await context.CancelAsync();
            context.RequestCancelled.ThrowIfCancellationRequested();
        });

        using HttpClient client = factory.CreateClient();

        // Act
        await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(path, cancellationToken));
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);
        RecordedMeasurement duration = await recorder.WaitForMeasurementAsync(
            m => m.Instrument == "http.server.request.duration" && Equals(m.Tags.GetValueOrDefault("error.type"), "request_canceled"),
            cancellationToken);

        // Assert
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem("error.type").ShouldBe("request_canceled");
        span.GetTagItem("http.response.status_code").ShouldBeNull();
        duration.Tags.ContainsKey("http.response.status_code").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A fault after the response started should report unhandled_exception with the sent status")]
    public async Task ServerSpan_FaultAfterResponseStarted_ShouldReportUnhandledException()
    {
        // Arrange — the response head and part of the body go out through the streaming feature, then
        // the pipeline throws: the status was sent, but the exchange is reset.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        string path = $"/telemetry/streamed/{Guid.NewGuid():N}";

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));

        factory.Application.Use(async (context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            IHttpResponseStreamingFeature streaming = context.Response.Streaming;
            await streaming.WriteAsync(Encoding.UTF8.GetBytes("partial"), cancellationToken);
            await streaming.FlushAsync(cancellationToken);

            throw new InvalidOperationException("Deliberate fault after the response started.");
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await Should.ThrowAsync<Exception>(() => response.Content.ReadAsStringAsync(cancellationToken));
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem("error.type").ShouldBe("unhandled_exception");
        span.GetTagItem("http.response.status_code").ShouldBe(200);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A client fault found reading the body after the response started should report client_fault, not unhandled_exception")]
    public async Task ServerSpan_ClientFaultAfterResponseStarted_ShouldReportClientFault()
    {
        // Arrange — the pipeline starts a streamed response, then reads a malformed chunked body and lets
        // the failed read escape. The transport's 400 can no longer replace the response, so the server
        // resets the exchange; the fault is still the client's, not the application's (#1340).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new();

        string path = $"/telemetry/client-fault-streamed/{Guid.NewGuid():N}";

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options =>
        {
            options.UseHttp1(transport);
            options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        });
        factory.Application.Use(async (context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            IHttpResponseStreamingFeature streaming = context.Response.Streaming;
            await streaming.WriteAsync(Encoding.UTF8.GetBytes("partial"), cancellationToken);
            await streaming.FlushAsync(cancellationToken);

            byte[] buffer = new byte[256];

            while (await context.Request.Body.ReadAsync(buffer, context.RequestCancelled) > 0)
            {
            }
        });

        await factory.StartAsync(cancellationToken);

        await using Connection client = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
        Stream stream = client.AsStream();

        // Act — "zz" is not a chunk size (RFC 9112 §7.1).
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST {path} HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\nabc\r\n0\r\n\r\n"), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);
        RecordedMeasurement duration = await recorder.WaitForMeasurementAsync(
            m => m.Instrument == "http.server.request.duration" && Equals(m.Tags.GetValueOrDefault("error.type"), "client_fault"),
            cancellationToken);

        // Assert
        span.GetTagItem("error.type").ShouldBe("client_fault");
        span.GetTagItem("http.response.status_code").ShouldBe(200);
        span.Status.ShouldBe(ActivityStatusCode.Error);
        duration.Tags.GetValueOrDefault("http.response.status_code").ShouldBe(200);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A response that cannot be sent should report response_send_failed")]
    public async Task ServerSpan_ResponseSendFailure_ShouldReportResponseSendFailed()
    {
        // Arrange — the response body throws when the transport reads it to send it.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        string path = $"/telemetry/unsendable/{Guid.NewGuid():N}";

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            context.Response.Body = new UnreadableStream();
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(path, cancellationToken));
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // Assert
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem("error.type").ShouldBe("response_send_failed");
        span.GetTagItem("http.response.status_code").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A method outside the known list should be reported as _OTHER")]
    public async Task ServerSpan_UnknownMethod_ShouldReportOtherAndTheOriginalMethod()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new();

        string path = $"/telemetry/purge/{Guid.NewGuid():N}";

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(new NetHttpMethod("PURGE"), path);

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);
        RecordedMeasurement duration = await recorder.WaitForMeasurementAsync(
            m => m.Instrument == "http.server.request.duration" && Equals(m.Tags.GetValueOrDefault("http.request.method"), "_OTHER"),
            cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        span.DisplayName.ShouldBe("HTTP");
        span.GetTagItem("http.request.method").ShouldBe("_OTHER");
        span.GetTagItem("http.request.method_original").ShouldBe("PURGE");
        duration.Tags.ContainsKey("http.request.method_original").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A standard method in another case should be reported as _OTHER with its original case")]
    public async Task ServerSpan_MethodInAnotherCase_ShouldReportOtherAndTheOriginalCase()
    {
        // Arrange — HttpClient upper-cases 'get' before sending it, so the request is written raw. Methods
        // are case-sensitive (RFC 9110 §9.1): 'get' is an unknown method, not GET.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        string path = $"/telemetry/lowercase/{Guid.NewGuid():N}";

        await using InMemoryConnectionListener transport = new();
        IHttpConnectionListener listener = HttpConnectionListener.Create(options => options.UseHttp1(transport));
        WebApplicationServer server = new(new WebApplicationServerOptions
        {
            Pipeline = new FakePipeline((context, _) =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
                return Task.CompletedTask;
            }),
            Listener = listener,
        });

        await server.StartAsync(cancellationToken);

        try
        {
            await using Connection client = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
            Stream stream = client.AsStream();

            // Act
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"get {path} HTTP/1.1\r\nHost: localhost\r\n\r\n"), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

            // Assert
            span.DisplayName.ShouldBe("HTTP");
            span.GetTagItem("http.request.method").ShouldBe("_OTHER");
            span.GetTagItem("http.request.method_original").ShouldBe("get");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: Each HTTP/2 stream should get its own server span")]
    public async Task ServerSpan_Http2Streams_ShouldEachGetOneServerSpan()
    {
        // Arrange — two requests multiplexed on one connection, each from a different caller trace.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        ActivityTraceId[] traceIds = [ActivityTraceId.CreateRandom(), ActivityTraceId.CreateRandom()];
        ActivitySpanId[] callerSpanIds = [ActivitySpanId.CreateRandom(), ActivitySpanId.CreateRandom()];

        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
        {
            Protocol = WebApplicationTestProtocol.Http2,
        });

        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        Task<HttpResponseMessage> Send(int index)
        {
            HttpRequestMessage request = new(NetHttpMethod.Get, $"/telemetry/h2/{index}")
            {
                Version = client.DefaultRequestVersion,
                VersionPolicy = client.DefaultVersionPolicy,
            };
            request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceIds[index].ToHexString()}-{callerSpanIds[index].ToHexString()}-01");
            return client.SendAsync(request, cancellationToken);
        }

        // Act
        HttpResponseMessage[] responses = await Task.WhenAll(Send(0), Send(1));
        Activity first = await recorder.WaitForStoppedAsync(a => a.TraceId == traceIds[0], cancellationToken);
        Activity second = await recorder.WaitForStoppedAsync(a => a.TraceId == traceIds[1], cancellationToken);

        // Assert
        responses.ShouldAllBe(r => r.StatusCode == NetHttpStatusCode.NoContent);
        recorder.Stopped.Count(a => a.TraceId == traceIds[0] || a.TraceId == traceIds[1]).ShouldBe(2);
        first.ParentSpanId.ShouldBe(callerSpanIds[0]);
        second.ParentSpanId.ShouldBe(callerSpanIds[1]);
        first.GetTagItem("network.protocol.version").ShouldBe("2");
        second.GetTagItem("network.protocol.version").ShouldBe("2");
        first.SpanId.ShouldNotBe(second.SpanId);

        foreach (HttpResponseMessage response in responses)
        {
            response.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: The known-method list should default to the semantic convention's and be replaceable")]
    public void GetMethodAttribute_KnownMethods_ShouldDefaultToTheConventionAndBeReplaceable()
    {
        // Arrange
        FrozenSet<string>? replaced = WebServerTelemetry.ParseKnownMethods(" GET, PROPFIND ,,");

        // Act
        string query = WebServerTelemetry.GetMethodAttribute(CohesionHttpMethod.Query, knownMethods: null, out bool queryKnown);
        string propfindDefault = WebServerTelemetry.GetMethodAttribute(new CohesionHttpMethod("PROPFIND"), knownMethods: null, out bool propfindKnownByDefault);
        string propfindReplaced = WebServerTelemetry.GetMethodAttribute(new CohesionHttpMethod("PROPFIND"), replaced, out bool propfindKnown);
        string postReplaced = WebServerTelemetry.GetMethodAttribute(CohesionHttpMethod.Post, replaced, out bool postKnown);

        // Assert
        WebServerTelemetry.ParseKnownMethods(null).ShouldBeNull();
        WebServerTelemetry.ParseKnownMethods("  ").ShouldBeNull();
        replaced.ShouldNotBeNull();
        replaced.Order().ShouldBe(["GET", "PROPFIND"]);

        query.ShouldBe("QUERY");
        queryKnown.ShouldBeTrue();
        propfindDefault.ShouldBe("_OTHER");
        propfindKnownByDefault.ShouldBeFalse();
        propfindReplaced.ShouldBe("PROPFIND");
        propfindKnown.ShouldBeTrue();

        // The list replaces the default one; it does not add to it.
        postReplaced.ShouldBe("_OTHER");
        postKnown.ShouldBeFalse();
    }

    private static double SumActiveRequests(TelemetryRecorder recorder)
    {
        return recorder.Measurements
            .Where(m => m.Instrument == "http.server.active_requests")
            .Sum(m => m.Value);
    }
}
