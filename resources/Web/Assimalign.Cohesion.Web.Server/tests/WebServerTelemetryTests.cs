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
using Assimalign.Cohesion.Web.Internal;
using Assimalign.Cohesion.Web.Server.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;

using Shouldly;

using Xunit;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Server.Tests;

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

    [Fact(DisplayName = "Cohesion Test [Web.Server] - Telemetry: The source and the meter should be named for the assembly")]
    public void Name_SourceAndMeter_ShouldBeTheAssemblyName()
    {
        // Act
        string? assemblyName = typeof(WebApplicationServer).Assembly.GetName().Name;

        // Assert
        WebServerTelemetry.Name.ShouldBe("Assimalign.Cohesion.Web.Server");
        WebServerTelemetry.Name.ShouldBe(assemblyName);
        WebServerTelemetry.Source.Name.ShouldBe(WebServerTelemetry.Name);
        WebServerTelemetry.Meter.Name.ShouldBe(WebServerTelemetry.Name);
        WebServerTelemetry.RequestDuration.Unit.ShouldBe("s");
        WebServerTelemetry.ActiveRequests.Unit.ShouldBe("{request}");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Server] - Telemetry: A standard method in another case should be reported as _OTHER with its original case")]
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

        }, listener);

        await server.StartAsync(cancellationToken);

        try
        {
            await using IDuplexConnection client = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
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

    [Fact(DisplayName = "Cohesion Test [Web.Server] - Telemetry: The request's query should be read only when a span listener subscribes")]
    public void Start_WithoutSpanListener_ShouldNotReadTheQuery()
    {
        // Arrange — the same exchange with nobody listening, with only the meter listened to, and with the
        // source listened to.
        FakeHttpRequest unobserved = new($"/telemetry/zero-cost/{Guid.NewGuid():N}", "access_token=s3cr3t");
        FakeHttpRequest measured = new($"/telemetry/zero-cost/{Guid.NewGuid():N}", "access_token=s3cr3t");
        FakeHttpRequest traced = new($"/telemetry/zero-cost/{Guid.NewGuid():N}", "access_token=s3cr3t");
        Activity span;

        // Act
        Serve(unobserved);

        using (new TelemetryRecorder(traces: false))
        {
            Serve(measured);
        }

        using (TelemetryRecorder recorder = new(metrics: false))
        {
            Serve(traced);
            span = recorder.Stopped.Single(a => Equals(a.GetTagItem("url.path"), traced.Path.Value));
        }

        // Assert
        unobserved.QueryReads.ShouldBe(0);
        measured.QueryReads.ShouldBe(0);
        traced.QueryReads.ShouldBe(1);
        span.GetTagItem("url.query").ShouldBe("access_token=REDACTED");

        static void Serve(FakeHttpRequest request)
        {
            FakeHttpContext exchange = new(response: new FakeHttpResponse(), request: request);
            WebExchangeTelemetry telemetry = WebExchangeTelemetry.Start(exchange, shownQueryValues: null);
            telemetry.SetOutcome(responded: true, canceled: false);
            telemetry.Stop();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.Server] - Telemetry: url.query should redact every value and keep every name")]
    [InlineData("", null)]
    [InlineData("access_token=s3cr3t", "access_token=REDACTED")]
    [InlineData("a=1&b=2", "a=REDACTED&b=REDACTED")]
    [InlineData("flag&empty=&token=s3cr3t", "flag&empty&token=REDACTED")]
    [InlineData("a%20b=1&%C3%A9=2", "a%20b=REDACTED&%C3%A9=REDACTED")]
    [InlineData("=orphan&a=1", "a=REDACTED")]
    public void GetQueryAttribute_NoParameterDeclaredSafe_ShouldRedactEveryValue(string query, string? expected)
    {
        // Arrange
        IHttpQueryCollection parsed = new HttpQuery(query).Parse();

        // Act
        string? attribute = WebServerTelemetry.GetQueryAttribute(parsed, shownValues: null);

        // Assert
        attribute.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Server] - Telemetry: url.query should show the values of the parameters declared safe, re-encoded")]
    public void GetQueryAttribute_ParametersDeclaredSafe_ShouldShowTheirValuesReEncoded()
    {
        // Arrange
        FrozenSet<string> shown = new[] { "page", "Sort", "q" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        IHttpQueryCollection parsed = new HttpQuery("PAGE=2&sort=name&q=a%20b%26c&token=s3cr3t").Parse();

        // Act
        string? attribute = WebServerTelemetry.GetQueryAttribute(parsed, shown);

        // Assert
        attribute.ShouldBe("PAGE=2&sort=name&q=a%20b%26c&token=REDACTED");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Server] - Telemetry: A later-version traceparent should keep only its sampled flag")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-03", 3)]
    [InlineData("cc-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-03", 1)]
    [InlineData("cc-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-02-what-the-future-will-be-like", 0)]
    public void TryParseTraceParent_TraceFlags_ShouldKeepOnlyTheSampledBitOfALaterVersion(string traceParent, int expectedFlags)
    {
        // Act — W3C Trace Context defines the flag bits for version 00; of a later version's flags only
        // the sampled bit is read (§3.2.4, "Versioning of traceparent").
        bool parsed = WebServerTelemetry.TryParseTraceParent(traceParent, "cohesion=1", out ActivityContext context);

        // Assert
        parsed.ShouldBeTrue();
        context.TraceId.ToHexString().ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");
        context.SpanId.ToHexString().ShouldBe("00f067aa0ba902b7");
        context.TraceFlags.ShouldBe((ActivityTraceFlags)expectedFlags);
        context.TraceState.ShouldBe("cohesion=1");
        context.IsRemote.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Server] - Telemetry: The known-method list should default to the semantic convention's and be replaceable")]
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
