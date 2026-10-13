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
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.ErrorHandling;
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
/// per request from the <c>Assimalign.Cohesion.Web.Server</c> activity source, parented to the
/// caller's W3C trace context and tagged per the OpenTelemetry HTTP server conventions; the
/// <c>http.server.request.duration</c> and <c>http.server.active_requests</c> instruments; the request
/// id; and nothing at all when nobody listens.
/// </summary>
[Collection(nameof(TelemetryCollection))]
public class WebServerTelemetryTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

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

        span.Source.Name.ShouldBe("Assimalign.Cohesion.Web.Server");
        span.Kind.ShouldBe(ActivityKind.Server);
        span.ParentSpanId.ShouldBe(callerSpanId);
        span.HasRemoteParent.ShouldBeTrue();
        span.TraceStateString.ShouldBe("cohesion=1");
        span.DisplayName.ShouldBe("GET");
        span.Status.ShouldBe(ActivityStatusCode.Unset);

        span.GetTagItem("http.request.method").ShouldBe("GET");
        span.GetTagItem("url.path").ShouldBe("/telemetry/parented");
        span.GetTagItem("url.query").ShouldBeNull();
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

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A version-00 or later-version traceparent should continue the caller's trace")]
    [InlineData("00", "")]
    [InlineData("cc", "")]
    [InlineData("cc", "-what-the-future-will-be-like")]
    [InlineData("01", "-a1-b2")]
    public async Task ServerSpan_SupportedTraceParentVersion_ShouldContinueTheCallersTrace(string version, string laterFields)
    {
        // Arrange — W3C Trace Context §3.2.4, "Versioning of traceparent": a header of a version the server
        // does not know is read by its version-00 fields, and the fields after them are ignored (#1299).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        ActivityTraceId traceId = ActivityTraceId.CreateRandom();
        ActivitySpanId callerSpanId = ActivitySpanId.CreateRandom();
        ActivityTraceId requestIdInPipeline = default;
        string path = $"/telemetry/version/{Guid.NewGuid():N}";

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use((context, next) =>
        {
            requestIdInPipeline = context.Features.Get<IWebRequestIdFeature>()!.RequestId;
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(NetHttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("traceparent", $"{version}-{traceId.ToHexString()}-{callerSpanId.ToHexString()}-01{laterFields}");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        span.TraceId.ShouldBe(traceId);
        span.ParentSpanId.ShouldBe(callerSpanId);
        span.HasRemoteParent.ShouldBeTrue();
        requestIdInPipeline.ShouldBe(traceId);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: An invalid traceparent should start a new trace")]
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01")]
    [InlineData("ff-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("ff-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01-what-the-future-will-be-like")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01-what-the-future-will-be-like")]
    [InlineData("cc-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01.what-the-future-will-be-like")]
    [InlineData("CC-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01-what-the-future-will-be-like")]
    [InlineData("cc-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-0")]
    [InlineData("00_4bf92f3577b34da6a3ce929d0e0e4736_00f067aa0ba902b7_01")]
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

        await using IDuplexConnection client = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
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

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A malformed HTTP/2 body the transport resets the stream for should report the client fault's 400, not a 500 error")]
    public async Task ServerSpan_Http2MalformedBody_ShouldReportTheClientFault400WithoutAnError()
    {
        // Arrange — the pipeline reads a body that ends short of its content-length and lets the failed read
        // escape, so the server's fault boundary stages a 500. The transport resets the stream with
        // PROTOCOL_ERROR (RFC 9113 §8.1.1): no status reaches the wire, and the exchange records the client
        // fault's 400 (#1378).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new();

        string path = $"/telemetry/h2-malformed-body/{Guid.NewGuid():N}";

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp2(transport));
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

        // Act
        await client.SendHeadersAsync(1, "POST", path, endStream: false, cancellationToken, ("content-length", "100"));
        await client.SendDataAsync(1, Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}"), endStream: true, cancellationToken);
        Http2RawFrame reset = await client.ReadUntilAsync(frame => frame.StreamId == 1 && frame.Type == Http2RawFrame.RstStreamType, cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);
        // The span stops after the duration is recorded, so the exchange's measurement is already in.
        RecordedMeasurement duration = recorder.Measurements.Single(
            m => m.Instrument == "http.server.request.duration" && Equals(m.Tags.GetValueOrDefault("network.protocol.version"), "2"));

        // Assert
        reset.RstStreamErrorCode.ShouldBe(0x1u);
        span.GetTagItem("http.response.status_code").ShouldBe(400);
        span.GetTagItem("error.type").ShouldBeNull();
        span.Status.ShouldBe(ActivityStatusCode.Unset);
        duration.Tags.GetValueOrDefault("http.response.status_code").ShouldBe(400);
        duration.Tags.ContainsKey("error.type").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: An HTTP/2 exchange cancelled by the reset for a malformed body should report the client fault's 400 without an error")]
    public async Task ServerSpan_Http2ExchangeCancelledByAClientFaultReset_ShouldReportTheClientFault400WithoutAnError()
    {
        // Arrange — the handler has read the body it was sent and waits on RequestCancelled, so the reset for
        // the malformed trailer section that follows reaches it as the exchange's cancellation, not as a read
        // failure. That cancellation was the transport's answer to the client's fault (#1378), and the
        // exchange ends as it does when the read meets the fault: with the 400 the transport records.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        string path = $"/telemetry/h2-client-fault-cancel/{Guid.NewGuid():N}";

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp2(transport));
        factory.Application.Use(async (context, next) =>
        {
            await context.Request.Body.ReadExactlyAsync(new byte[16], context.RequestCancelled);
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, context.RequestCancelled);
        });

        await factory.StartAsync(cancellationToken);

        await using Http2RawClient client = await Http2RawClient.ConnectAsync(transport, cancellationToken);

        // Act
        await client.SendHeadersAsync(1, "POST", path, endStream: false, cancellationToken);
        await client.SendDataAsync(1, Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}"), endStream: false, cancellationToken);
        await waiting.Task.WaitAsync(cancellationToken);
        await client.SendTrailersAsync(1, cancellationToken, ("content-length", "5"));
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // Assert
        span.GetTagItem("http.response.status_code").ShouldBe(400);
        span.GetTagItem("error.type").ShouldBeNull();
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: An HTTP/2 exchange cancelled after the transport answered its body 413 should report the 413 without an error")]
    public async Task ServerSpan_Http2ExchangeCancelledAfterTheTransport413_ShouldReportThe413WithoutAnError()
    {
        // Arrange — the handler never reads the body and waits on RequestCancelled. The frame pump answers the
        // over-cap body 413 itself and stops the rest of the request with RST_STREAM(NO_ERROR), which reaches
        // the handler as the exchange's cancellation (#1378). The 413 went out; it is how the exchange ended.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new();

        string path = $"/telemetry/h2-413-cancel/{Guid.NewGuid():N}";

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp2(transport, http2 => http2.Limits.MaxRequestBodySize = 16));
        factory.Application.Use(async (context, next) => await Task.Delay(Timeout.Infinite, context.RequestCancelled));

        await factory.StartAsync(cancellationToken);

        await using Http2RawClient client = await Http2RawClient.ConnectAsync(transport, cancellationToken);

        // Act — no content-length, so only the running total crosses the cap.
        await client.SendHeadersAsync(1, "POST", path, endStream: false, cancellationToken);
        await client.SendDataAsync(1, new byte[32], endStream: false, cancellationToken);
        Http2RawFrame answer = await client.ReadUntilAsync(frame => frame.StreamId == 1 && frame.Type == Http2RawFrame.HeadersType, cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // The span stops after the duration is recorded, so the exchange's measurement is already in.
        RecordedMeasurement duration = recorder.Measurements.Single(
            m => m.Instrument == "http.server.request.duration" && Equals(m.Tags.GetValueOrDefault("network.protocol.version"), "2"));

        // Assert
        answer.EndStream.ShouldBeTrue();
        span.GetTagItem("http.response.status_code").ShouldBe(413);
        span.GetTagItem("error.type").ShouldBeNull();
        span.Status.ShouldBe(ActivityStatusCode.Unset);
        duration.Tags.GetValueOrDefault("http.response.status_code").ShouldBe(413);
        duration.Tags.ContainsKey("error.type").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A malformed HTTP/3 body the transport resets the stream for should report the client fault's 400, not a 500 error")]
    public async Task ServerSpan_Http3MalformedBody_ShouldReportTheClientFault400WithoutAnError()
    {
        // Arrange — as for HTTP/2: the stream ends short of its Content-Length, the transport resets it with
        // H3_MESSAGE_ERROR (RFC 9114 §4.1.2), and the exchange records the client fault's 400 (#1378).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        string path = $"/telemetry/h3-malformed-body/{Guid.NewGuid():N}";

        InMemoryMultiplexedConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp3(transport));
        factory.Application.Use(async (context, next) =>
        {
            byte[] buffer = new byte[256];

            while (await context.Request.Body.ReadAsync(buffer, context.RequestCancelled) > 0)
            {
            }

            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
        });

        await factory.StartAsync(cancellationToken);

        await using Http3RawClient client = await Http3RawClient.ConnectAsync(transport, cancellationToken);

        // Act
        IMultiplexedConnectionStream stream = await client.SendRequestAsync(
            "POST",
            path,
            [("content-length", "100")],
            [Http3RawClient.Data(Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}"))],
            endStream: true,
            cancellationToken);
        (_, long? resetCode) = await Http3RawClient.ReadResponseAsync(stream, cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // Assert
        resetCode.ShouldBe(0x10eL);
        span.GetTagItem("http.response.status_code").ShouldBe(400);
        span.GetTagItem("error.type").ShouldBeNull();
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: An HTTP/3 request the client resets mid-body should report request_canceled, not a 500 error")]
    public async Task ServerSpan_Http3RequestResetMidBody_ShouldReportRequestCanceled()
    {
        // Arrange — the pipeline reads the body and lets the read's failure escape. The client resets its
        // request stream after the first DATA frame (RFC 9114 §4.1.1): a cancellation, as an HTTP/2
        // RST_STREAM is, so the server resets the exchange rather than staging and sending a 500 (#1378).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);
        TaskCompletionSource firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        string path = $"/telemetry/h3-reset-mid-body/{Guid.NewGuid():N}";

        InMemoryMultiplexedConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp3(transport));
        factory.Application.Use(async (context, next) =>
        {
            byte[] buffer = new byte[256];

            if (await context.Request.Body.ReadAsync(buffer, context.RequestCancelled) > 0)
            {
                firstRead.TrySetResult();
            }

            while (await context.Request.Body.ReadAsync(buffer, context.RequestCancelled) > 0)
            {
            }

            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
        });

        await factory.StartAsync(cancellationToken);

        await using Http3RawClient client = await Http3RawClient.ConnectAsync(transport, cancellationToken);

        // Act
        IMultiplexedConnectionStream stream = await client.SendRequestAsync(
            "POST",
            path,
            [],
            [Http3RawClient.Data(Encoding.ASCII.GetBytes("{\"name\":"))],
            endStream: false,
            cancellationToken);
        await firstRead.Task.WaitAsync(cancellationToken);
        stream!.DirectionalAbort!.AbortWrite(0x10c);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // Assert
        span.GetTagItem("error.type").ShouldBe("request_canceled");
        span.GetTagItem("http.response.status_code").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: Losing an HTTP/3 connection mid-body should cancel the exchange without an Error log or a 500")]
    public async Task ServerSpan_Http3ConnectionLostMidBody_ShouldReportRequestCanceledWithoutAnError()
    {
        // Arrange — the request body is still incoming when the peer ends its critical control stream.
        // The handler supplies no read cancellation token, so cancellation must come from the transport.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);
        RecordingLoggerProvider recorded = new();
        TaskCompletionSource firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(Exception Failure, CancellationToken RequestCancelled, CohesionHttpStatusCode? Fault)> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string path = $"/telemetry/h3-connection-lost-mid-body/{Guid.NewGuid():N}";

        InMemoryMultiplexedConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Logging.AddProvider(recorded);
        factory.Builder.Server.UseServer(options => options.UseHttp3(transport));
        factory.Application.Use(async (context, next) =>
        {
            IWebClientFaultFeature? feature = context.Features.Get<IWebClientFaultFeature>();
            CancellationToken requestToken = context.RequestCancelled;
            byte[] buffer = new byte[256];

            try
            {
                await context.Request.Body.ReadExactlyAsync(buffer.AsMemory(0, 5), CancellationToken.None);
                firstRead.TrySetResult();

                while (await context.Request.Body.ReadAsync(buffer, CancellationToken.None) > 0)
                {
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                failed.TrySetResult((exception, requestToken, feature?.StatusCode));
                throw;
            }
        });
        await factory.StartAsync(cancellationToken);
        await using Http3RawClient client = await Http3RawClient.ConnectAsync(transport, cancellationToken);
        await using IDuplexConnection controlStream = await client.OpenControlStreamAsync(cancellationToken);
        await using IMultiplexedConnectionStream requestStream = await client.SendRequestAsync(
            "POST",
            path,
            [],
            [Http3RawClient.Data(Encoding.ASCII.GetBytes("hello"))],
            endStream: false,
            cancellationToken);
        await firstRead.Task.WaitAsync(cancellationToken);

        // Act — H3_CLOSED_CRITICAL_STREAM is raised outside the body reader (RFC 9114 §6.2.1).
        await controlStream.Output.CompleteAsync();
        (Exception failure, CancellationToken requestCancelled, CohesionHttpStatusCode? fault) = await failed.Task.WaitAsync(cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);
        await factory.StopAsync(cancellationToken);

        // Assert — this is an abandoned exchange, with no client-body fault or attempted error response.
        OperationCanceledException canceled = failure.ShouldBeAssignableTo<OperationCanceledException>()!;
        requestCancelled.IsCancellationRequested.ShouldBeTrue();
        canceled.CancellationToken.ShouldBe(requestCancelled);
        fault.ShouldBeNull();
        span.GetTagItem("error.type").ShouldBe("request_canceled");
        span.GetTagItem("http.response.status_code").ShouldBeNull();
        recorded.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Error);
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

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: The error boundary's streamed-response abort is cancelled and skips completion callbacks")]
    [InlineData(WebApplicationTestProtocol.Http1)]
    [InlineData(WebApplicationTestProtocol.Http2)]
    public async Task ServerSpan_ErrorBoundaryAbortsStartedResponse_ShouldReportCancellationAndSkipCompletion(WebApplicationTestProtocol protocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new();
        TaskCompletionSource releaseFault = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string path = $"/telemetry/boundary-abort/{Guid.NewGuid():N}";
        int completionCallbacks = 0;

        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = protocol });
        factory.Builder.Server.UseServer(options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));
        factory.Builder.Services.AddRouting();
        factory.Application.UseErrorHandling();
        factory.Application.UseRouting().Map(CohesionHttpMethod.Get, path, new RouterRouteHandler(async context =>
        {
            context.Features.Get<IWebResponseCompletionFeature>().ShouldNotBeNull().Register(() =>
            {
                Interlocked.Increment(ref completionCallbacks);
                return ValueTask.CompletedTask;
            });
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            IHttpResponseStreamingFeature streaming = context.Response.Streaming;
            await streaming.WriteAsync(Encoding.UTF8.GetBytes("partial"), cancellationToken);
            await streaming.FlushAsync(cancellationToken);
            await releaseFault.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("Deliberate fault after the client read a partial response.");
        }));
        using HttpClient client = factory.CreateClient();

        try
        {
            // Act: read the streamed bytes before allowing the handler to fault, on either protocol.
            using HttpResponseMessage response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            byte[] partial = new byte[7];
            await body.ReadExactlyAsync(partial, cancellationToken);
            releaseFault.TrySetResult();
            Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);
            RecordedMeasurement duration = await recorder.WaitForMeasurementAsync(
                m => m.Instrument == "http.server.request.duration" && Equals(m.Tags.GetValueOrDefault("http.route"), path),
                cancellationToken);

            // Assert
            Encoding.UTF8.GetString(partial).ShouldBe("partial");
            response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            Volatile.Read(ref completionCallbacks).ShouldBe(0);
            span.Status.ShouldBe(ActivityStatusCode.Error);
            span.GetTagItem("error.type").ShouldBe("request_canceled");
            span.GetTagItem("http.response.status_code").ShouldBe(200);
            duration.Tags.GetValueOrDefault("error.type").ShouldBe("request_canceled");
            duration.Tags.GetValueOrDefault("http.response.status_code").ShouldBe(200);
            await Should.ThrowAsync<Exception>(() => body.ReadAsync(new byte[1], cancellationToken).AsTask());
        }
        finally
        {
            releaseFault.TrySetResult();
        }
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

        await using IDuplexConnection client = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
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

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: A request with a query should tag url.query with every value redacted and the names kept")]
    public async Task ServerSpan_RequestWithQuery_ShouldTagUrlQueryWithEveryValueRedacted()
    {
        // Arrange — a token, an ordinary parameter, and a parameter without a value (#1298).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        string path = $"/telemetry/query/{Guid.NewGuid():N}";

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync($"{path}?access_token=s3cr3t&page=2&debug", cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        span.GetTagItem("url.query").ShouldBe("access_token=REDACTED&page=REDACTED&debug");
        span.TagObjects.Select(tag => tag.Value as string).ShouldNotContain(text => text != null && text.Contains("s3cr3t"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Telemetry: Query parameters the application declares safe should keep their values in url.query")]
    public async Task ServerSpan_QueryParametersDeclaredSafe_ShouldShowTheirValues()
    {
        // Arrange — names compare case-insensitively, as the query collection compares them, and the
        // calls add up.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        string path = $"/telemetry/query-shown/{Guid.NewGuid():N}";

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.AllowQueryValuesInTelemetry("page").AllowQueryValuesInTelemetry("SORT", "q");

        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync($"{path}?page=2&sort=name&q=a%20b&access_token=s3cr3t", cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        span.GetTagItem("url.query").ShouldBe("page=2&sort=name&q=a%20b&access_token=REDACTED");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - AllowQueryValuesInTelemetry: A missing or empty name should be rejected")]
    public void AllowQueryValuesInTelemetry_MissingOrEmptyName_ShouldThrow()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // Act
        Exception missingList = Should.Throw<ArgumentNullException>(() => builder.Server.AllowQueryValuesInTelemetry(null!));
        Exception missingName = Should.Throw<ArgumentNullException>(() => builder.Server.AllowQueryValuesInTelemetry("page", null!));
        Exception emptyName = Should.Throw<ArgumentException>(() => builder.Server.AllowQueryValuesInTelemetry(string.Empty));

        // Assert
        missingList.ShouldBeOfType<ArgumentNullException>();
        missingName.ShouldBeOfType<ArgumentNullException>();
        emptyName.ShouldBeOfType<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - AllowQueryValuesInTelemetry: A call rejected for a missing or empty name should declare none of its names")]
    public async Task AllowQueryValuesInTelemetry_CallRejectedForAMissingOrEmptyName_ShouldDeclareNoneOfItsNames()
    {
        // Arrange — each rejected call names a valid parameter before the invalid one. Keeping the valid
        // name would show a value the application believes it never allowed (#1298).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);

        string path = $"/telemetry/query-rejected/{Guid.NewGuid():N}";

        await using WebApplicationTestFactory factory = new();

        Should.Throw<ArgumentNullException>(() => factory.Builder.Server.AllowQueryValuesInTelemetry("page", null!));
        Should.Throw<ArgumentException>(() => factory.Builder.Server.AllowQueryValuesInTelemetry("sort", string.Empty));

        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync($"{path}?page=2&sort=name", cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(a => Equals(a.GetTagItem("url.path"), path), cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        span.GetTagItem("url.query").ShouldBe("page=REDACTED&sort=REDACTED");
    }


    private static double SumActiveRequests(TelemetryRecorder recorder)
    {
        return recorder.Measurements
            .Where(m => m.Instrument == "http.server.active_requests")
            .Sum(m => m.Value);
    }
}
