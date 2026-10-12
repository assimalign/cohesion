using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Cors.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Cors.Tests;

[Collection(CorsEventSourceCollection.Name)]
public sealed class CorsEventSourceTests
{
    private const string origin = "https://app.example";

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - EventSource: Name equals assembly name")]
    public void GetName_CorsSource_ShouldEqualAssemblyName()
    {
        // Arrange / Act
        string name = EventSource.GetName(typeof(CorsEventSource));

        // Assert
        name.ShouldBe("Assimalign.Cohesion.Web.Cors");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - EventSource: Strict manifest is valid")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only strict manifest validation of the statically referenced source.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only strict manifest validation of the statically referenced source.")]
    public void GenerateManifest_CorsSource_ShouldPassStrictValidation()
    {
        // Arrange / Act
        string? manifest = EventSource.GenerateManifest(typeof(CorsEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - EventSource: Real preflights carry declared payloads and request ids once")]
    public async Task InvokeAsync_RefusedAndPassedPreflights_ShouldPublishEachEventOnce()
    {
        // Arrange
        CorsEventSource source = CorsEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        await using CorsTestContext refused = CreatePreflight("https://other.example");
        await using CorsTestContext passed = CreatePreflight(origin);
        string refusedId = RequestId(refused);
        string passedId = RequestId(passed);
        long before = source.RejectedPreflights;
        using Activity unrelated = new Activity("unrelated").Start();

        // Act
        await CorsPipeline.InvokeAsync(refused, options => options.AddDefaultPolicy(policy => policy.WithOrigins(origin)));
        await CorsPipeline.InvokeAsync(passed, configure: null);

        // Assert
        EventWrittenEventArgs rejection = EventFor(recorder, refusedId);
        rejection.EventId.ShouldBe(1);
        rejection.EventName.ShouldBe("PreflightRejected");
        rejection.Level.ShouldBe(EventLevel.Verbose);
        rejection.PayloadNames!.ToArray().ShouldBe(["reason", "origin", "requestedMethod", "rejectedHeader", "routeTemplate", "requestId"]);
        rejection.Payload!.ToArray().ShouldBe(["OriginNotAllowed", "https://other.example", "DELETE", "", "/items/{id}", refusedId]);
        EventWrittenEventArgs pass = EventFor(recorder, passedId);
        pass.EventId.ShouldBe(2);
        pass.EventName.ShouldBe("PreflightPassedThrough");
        pass.PayloadNames!.ToArray().ShouldBe(["cause", "requestedMethod", "routeTemplate", "requestId"]);
        pass.Payload!.ToArray().ShouldBe(["NoPolicy", "DELETE", "/items/{id}", passedId]);
        source.RejectedPreflights.ShouldBe(before + 1);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - EventSource: Rejection counter publishes and counts without listeners")]
    public async Task OnEventCommand_Enabled_ShouldPublishExactRejectionCounter()
    {
        // Arrange
        CorsEventSource source = CorsEventSource.Log;
        long before = source.RejectedPreflights;
        await using CorsTestContext context = CreatePreflight(origin);

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(origin)));
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await recorder.WaitForCountersAsync(["rejected-preflights"], timeout.Token);

        // Assert
        source.RejectedPreflights.ShouldBe(before + 1);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Preflight: First policy refusal supplies the diagnostic reason")]
    [InlineData("https://other.example", "DELETE", "X-Other", "OriginNotAllowed", "")]
    [InlineData(origin, "DELETE", "X-Other", "MethodNotAllowed", "")]
    [InlineData(origin, "PUT", "X-Allowed, X-Other, X-Later", "HeaderNotAllowed", "X-Other")]
    [InlineData(origin, "PUT", "X-Allowed, bad name", "HeaderNotAllowed", "bad name")]
    public async Task InvokeAsync_PolicyRefusal_ShouldReportFirstReason(string requestOrigin, string method, string headers, string reason, string rejectedHeader)
    {
        // Arrange
        using EventSourceRecorder recorder = new(CorsEventSource.Log, EventLevel.Verbose);
        await using CorsTestContext context = CreatePreflight(requestOrigin, method, headers);

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy =>
            policy.WithOrigins(origin).WithMethods("PUT").WithHeaders("X-Allowed")));

        // Assert
        continued.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBeNull();
        EventWrittenEventArgs written = EventFor(recorder, RequestId(context));
        written.Payload![0].ShouldBe(reason);
        written.Payload[3].ShouldBe(rejectedHeader);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Preflight: Every pass-through cause is attributed")]
    [InlineData("OptionsRouteOwnsPreflight")]
    [InlineData("CorsDisabled")]
    [InlineData("NoPolicy")]
    public async Task InvokeAsync_PassedThrough_ShouldReportCause(string cause)
    {
        // Arrange
        using EventSourceRecorder recorder = new(CorsEventSource.Log, EventLevel.Verbose);
        await using CorsTestContext context = CreatePreflight(origin);
        FakeRouteMatchFeature? match = cause switch
        {
            "OptionsRouteOwnsPreflight" => new() { Route = new Route(HttpMethod.Options, "/items") },
            "CorsDisabled" => new(CorsMetadata.Disabled) { IsPreflight = true },
            _ => null,
        };
        long before = CorsEventSource.Log.RejectedPreflights;

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, configure: null, match);

        // Assert
        continued.ShouldBeTrue();
        EventFor(recorder, RequestId(context)).Payload![0].ShouldBe(cause);
        CorsEventSource.Log.RejectedPreflights.ShouldBe(before);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight: Attacker identifiers are bounded and request data stays absent")]
    public async Task InvokeAsync_LongIdentifiers_ShouldBoundAllowedDiagnosticFields()
    {
        // Arrange
        string longOrigin = "https://" + new string('a', 300) + ".example";
        string header = "X-" + new string('b', 100);
        using EventSourceRecorder recorder = new(CorsEventSource.Log, EventLevel.Verbose);
        await using CorsTestContext context = CreatePreflight(longOrigin, "PUT", header);
        context.Request.Headers[HttpHeaderKey.Authorization] = "sensitive-header-value";

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy =>
            policy.WithOrigins(longOrigin).WithMethods("PUT")));

        // Assert
        EventWrittenEventArgs written = EventFor(recorder, RequestId(context));
        written.Payload![1].ShouldBe(longOrigin[..256]);
        written.Payload[3].ShouldBe(header[..64]);
        written.Payload.Any(item => item is string text && text.Contains("sensitive", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Preflight: Missing, repeated or malformed origins are not written")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WritePreflight_UnusableOrigin_ShouldWriteEmptyOrigin(int kind)
    {
        // Arrange
        using EventSourceRecorder recorder = new(CorsEventSource.Log, EventLevel.Verbose);
        await using CorsTestContext context = CreatePreflight(origin);
        if (kind == 0)
        {
            context.Request.Headers.Remove(HttpHeaderKey.Origin);
        }
        else
        {
            context.Request.Headers[HttpHeaderKey.Origin] = kind == 1
                ? new HttpHeaderValue("https://app.example/private?secret")
                : new HttpHeaderValue([origin, "https://other.example"]);
        }

        // Act
        CorsResponseHeaders.WritePreflight(context, new CorsPolicyBuilder().WithOrigins(origin).Build(), "DELETE");

        // Assert
        EventFor(recorder, RequestId(context)).Payload![1].ShouldBe(string.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - EventSource: Success and actual-origin refusal remain silent")]
    public async Task InvokeAsync_SuccessAndActualRequest_ShouldRaiseNothing()
    {
        // Arrange
        using EventSourceRecorder recorder = new(CorsEventSource.Log, EventLevel.Verbose);
        await using CorsTestContext success = CreatePreflight(origin, "PUT");
        await using CorsTestContext actual = new(HttpMethod.Get, origin: "https://other.example");
        actual.Features.Set<IWebRequestIdFeature>(new RequestIdFeature());
        long before = CorsEventSource.Log.RejectedPreflights;

        // Act
        await CorsPipeline.InvokeAsync(success, options => options.AddDefaultPolicy(policy => policy.WithOrigins(origin).WithMethods("PUT")));
        await CorsPipeline.InvokeAsync(actual, options => options.AddDefaultPolicy(policy => policy.WithOrigins(origin)));

        // Assert
        recorder.Events.Any(item => item.Payload is { } payload &&
            (payload.Contains(RequestId(success)) || payload.Contains(RequestId(actual)))).ShouldBeFalse();
        CorsEventSource.Log.RejectedPreflights.ShouldBe(before);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - EventSource: Disabled Verbose avoids allocation and identifier reads")]
    public async Task PreflightRejected_VerboseDisabled_ShouldOnlyCount()
    {
        // Arrange
        CorsEventSource source = CorsEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Informational);
        await using CorsTestContext context = CreatePreflight("https://" + new string('a', 300));
        UnreadIdentifiers identifiers = new();
        context.Features.Set<IWebRequestIdFeature>(identifiers);
        context.Features.Set<IWebEndpointFeature>(identifiers);
        string rejectedHeader = new('b', 100);
        source.PreflightRejected(context, CorsPreflightRejection.HeaderNotAllowed, "PUT", rejectedHeader);
        long before = source.RejectedPreflights;

        // Act
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
        {
            source.PreflightRejected(context, CorsPreflightRejection.HeaderNotAllowed, "PUT", rejectedHeader);
            source.PreflightPassedThrough(context, "NoPolicy", "PUT");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        allocated.ShouldBe(0);
        source.RejectedPreflights.ShouldBe(before + 1000);
        recorder.Events.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Preflight evaluator: Successful policies allocate zero bytes")]
    [InlineData(false, false, origin)]
    [InlineData(false, true, origin)]
    [InlineData(true, false, origin)]
    [InlineData(true, true, origin)]
    [InlineData(true, true, "https://[2001:db8::1]:8443")]
    [InlineData(true, true, "https://[::ffff:c000:201]")]
    [InlineData(true, true, "null")]
    public async Task EvaluatePreflight_AllowedPolicy_ShouldAllocateNothing(bool predicate, bool anyHeader, string requestOrigin)
    {
        // Arrange
        CorsPolicyBuilder builder = new();
        if (predicate)
        {
            builder.SetIsOriginAllowed(static _ => true);
        }
        else
        {
            builder.WithOrigins(requestOrigin);
        }
        builder.WithMethods("PUT");
        if (anyHeader)
        {
            builder.AllowAnyHeader();
        }
        else
        {
            builder.WithHeaders("X-One", "Authorization");
        }
        CorsPolicy policy = builder.Build();
        await using CorsTestContext context = CreatePreflight(requestOrigin, "PUT");
        context.Request.Headers[HttpHeaderKey.AccessControlRequestHeaders] = new HttpHeaderValue(["X-One", "Authorization"]);
        for (int index = 0; index < 1000; index++)
        {
            CorsResponseHeaders.EvaluatePreflight(policy, context.Request, "PUT", out _, out _, out _);
        }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        CorsPreflightRejection result = CorsPreflightRejection.None;
        for (int index = 0; index < 1000; index++)
        {
            result = CorsResponseHeaders.EvaluatePreflight(policy, context.Request, "PUT", out _, out _, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        result.ShouldBe(CorsPreflightRejection.None);
        allocated.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Serialized origin: Allocation-free validation agrees with normalization")]
    [InlineData("https://app.example")]
    [InlineData("https://APP.example")]
    [InlineData("https://app.example:443")]
    [InlineData("https://app.example:0444")]
    [InlineData("https://app.example:0")]
    [InlineData("https://app.example:65536")]
    [InlineData("https://app.example/path")]
    [InlineData("https://[::]")]
    [InlineData("https://[2001:db8::1]")]
    [InlineData("https://[2001:db8:0:0:1::1]")]
    [InlineData("https://[2001:0:0:1:0:0:1:1]")]
    [InlineData("https://[2001::1:0:0:1:1]")]
    [InlineData("https://[2001:0:0:1::1:1]")]
    [InlineData("https://[1:2:3:4:5:6:7:8]")]
    [InlineData("https://[1:2:3:4:5:6:7::]")]
    [InlineData("https://[::ffff:192.0.2.1]")]
    [InlineData("https://[::ffff:c000:201]")]
    [InlineData("https://[::1]garbage")]
    [InlineData("https://[:::1]")]
    [InlineData("https://[::1%3]")]
    [InlineData("https://[::FFFF:1]")]
    [InlineData("https://[1:2:3:4:5:6:7:8:]")]
    [InlineData("https://[:1:2:3:4:5:6:7:8]")]
    [InlineData("https://[::1::]")]
    [InlineData("https://[::01]")]
    [InlineData("https://[:: 1]")]
    [InlineData("https://[::1 ]")]
    [InlineData("https://app.example ")]
    [InlineData("https://app.example: 1")]
    [InlineData("https://app.example:\t1")]
    [InlineData("HTTPS://app.example")]
    [InlineData("null")]
    public void IsSerialized_OriginForms_ShouldPreserveNormalizationSemantics(string value)
    {
        // Arrange
        bool expected = value == CorsOrigin.Opaque ||
            (CorsOrigin.TryNormalize(value, out string serialized, out _) && serialized == value);

        // Act
        bool actual = CorsOrigin.IsSerialized(value);

        // Assert
        actual.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Serialized origin: NUL in a numeric component never reaches the predicate")]
    [InlineData("https://app.example:81\0")]
    [InlineData("https://app.example:1\0\0")]
    [InlineData("https://[::1\0]")]
    [InlineData("https://[1\0::1]")]
    [InlineData("https://[1:2:3:4:5:6:7:8\0]")]
    [InlineData("https://[::1]:81\0")]
    public void IsOriginAllowed_NulInNumericOriginPart_ShouldRejectBeforePredicate(string value)
    {
        // Arrange
        bool predicateCalled = false;
        CorsPolicy policy = new CorsPolicyBuilder().SetIsOriginAllowed(_ =>
        {
            predicateCalled = true;
            return true;
        }).Build();
        bool normalized = CorsOrigin.TryNormalize(value, out _, out _);

        // Act
        bool serialized = CorsOrigin.IsSerialized(value);
        bool allowed = policy.IsOriginAllowed(value);

        // Assert
        normalized.ShouldBeFalse();
        serialized.ShouldBeFalse();
        allowed.ShouldBeFalse();
        predicateCalled.ShouldBeFalse();
    }

    private static CorsTestContext CreatePreflight(string requestOrigin, string method = "DELETE", string? headers = null)
    {
        CorsTestContext context = CorsTestContext.Preflight(requestOrigin, method, headers, "/sensitive-path");
        context.Features.Set<IWebRequestIdFeature>(new RequestIdFeature());
        context.Features.Set<IWebEndpointFeature>(new EndpointFeature());
        return context;
    }

    private static string RequestId(IHttpContext context)
        => context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();

    private static EventWrittenEventArgs EventFor(EventSourceRecorder recorder, string requestId)
    {
        EventWrittenEventArgs[] matches = recorder.Events.Where(item => item.Payload?.Contains(requestId) == true).ToArray();
        matches.Length.ShouldBe(1);
        return matches[0];
    }

    private sealed class RequestIdFeature : IWebRequestIdFeature
    {
        public string Name => nameof(IWebRequestIdFeature);
        public ActivityTraceId RequestId { get; } = ActivityTraceId.CreateRandom();
    }

    private sealed class EndpointFeature : IWebEndpointFeature
    {
        public string Name => nameof(IWebEndpointFeature);
        public WebApplicationMiddleware Endpoint => static _ => Task.CompletedTask;
        public string RouteTemplate => "/items/{id}";
    }

    private sealed class UnreadIdentifiers : IWebRequestIdFeature, IWebEndpointFeature
    {
        public string Name => nameof(UnreadIdentifiers);
        public ActivityTraceId RequestId => throw new InvalidOperationException("Request id read while Verbose is disabled.");
        public string RouteTemplate => throw new InvalidOperationException("Route read while Verbose is disabled.");
        public WebApplicationMiddleware Endpoint => static _ => Task.CompletedTask;
    }
}
