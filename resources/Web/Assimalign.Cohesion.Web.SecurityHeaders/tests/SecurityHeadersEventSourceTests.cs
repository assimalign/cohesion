using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.SecurityHeaders.Internal;
using Assimalign.Cohesion.Web.Testing;
using Assimalign.Cohesion.Web.WebSockets;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

[Collection("SecurityHeaders event source")]
public sealed class SecurityHeadersEventSourceTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Event source: The provider name matches the assembly")]
    public void GetName_EventSource_ShouldMatchAssemblyName()
    {
        // Arrange / Act
        string name = EventSource.GetName(typeof(SecurityHeadersEventSource));

        // Assert
        name.ShouldBe("Assimalign.Cohesion.Web.SecurityHeaders");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Event source: The strict manifest is valid")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest validation of the statically referenced source.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest validation of the statically referenced source.")]
    public void GenerateManifest_Strict_ShouldSucceed()
    {
        // Arrange / Act
        string? manifest = EventSource.GenerateManifest(typeof(SecurityHeadersEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Event source: Read-only headers raise one correlated warning at response starting")]
    public async Task UseSecurityHeaders_ReadOnlyHead_ShouldRaiseHeadersNotAppliedOnce()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        SecurityHeadersEventSource source = SecurityHeadersEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Warning);
        await Task.Delay(1100, cancellation.Token);
        long before = source.UnprotectedResponses;
        string requestId = ActivityTraceId.CreateRandom().ToHexString();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Application.Use((context, next) => next.Invoke(new SecurityHeadersReadOnlyContext(context)));
        factory.Application.UseSecurityHeaders();
        factory.Application.UseRouting().Map(CohesionHttpMethod.Get, "/diagnostic/{id}", new RouterRouteHandler(context =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Accepted;
            return Task.CompletedTask;
        }));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateRequest("/diagnostic/private-path", requestId);

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        EventWrittenEventArgs warning = recorder.Events.Single(entry => entry.EventId == 1 && entry.Payload![4]!.Equals(requestId));
        warning.EventName.ShouldBe("HeadersNotApplied");
        warning.Level.ShouldBe(EventLevel.Warning);
        warning.PayloadNames.ShouldBe(new[] { "statusCode", "routeTemplate", "committedBeforeTurn", "suppressedCount", "requestId" });
        warning.Payload![0].ShouldBe(202);
        warning.Payload[1].ShouldBe("/diagnostic/{id}");
        warning.Payload[2].ShouldBe(false);
        (source.UnprotectedResponses - before).ShouldBe(1);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
        recorder.Events.SelectMany(entry => entry.Payload ?? Enumerable.Empty<object?>()).ShouldNotContain(value => Equals(value, "/diagnostic/private-path"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Event source: The unprotected-responses counter publishes")]
    public async Task OnEventCommand_Enabled_ShouldPublishCounter()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        using EventSourceRecorder recorder = new(SecurityHeadersEventSource.Log, EventLevel.Warning, TimeSpan.FromMilliseconds(100));

        // Act / Assert
        await recorder.WaitForCountersAsync(["unprotected-responses"], cancellation.Token);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Event source: Suppressed warnings keep an exact response count")]
    public async Task UseSecurityHeaders_RepeatedReadOnlyHeads_ShouldThrottleWarningsAndCountEveryResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        SecurityHeadersEventSource source = SecurityHeadersEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Warning);
        await using WebApplicationTestFactory factory = CreateReadOnlyFactory();
        factory.Application.UseSecurityHeaders();
        factory.Application.Use((context, next) => Task.CompletedTask);
        using HttpClient client = factory.CreateClient();
        string requestId = ActivityTraceId.CreateRandom().ToHexString();
        client.DefaultRequestHeaders.Add("traceparent", $"00-{requestId}-0123456789abcdef-00");
        await Task.Delay(1100, cancellation.Token);
        long before = source.UnprotectedResponses;

        // Act
        for (int index = 0; index < 3; index++)
        {
            using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);
        }
        await Task.Delay(1100, cancellation.Token);
        using HttpResponseMessage last = await client.GetAsync("/", cancellation.Token);

        // Assert
        EventWrittenEventArgs[] warnings = recorder.Events.Where(entry => entry.EventId == 1 && entry.Payload![4]!.Equals(requestId)).ToArray();
        warnings.Length.ShouldBeGreaterThanOrEqualTo(2);
        // A loaded host may take more than a second between requests. Pin the rate and
        // complete accounting, without requiring an upper bound on HTTP round-trip time.
        (warnings.Length + warnings.Skip(1).Sum(warning => (int)warning.Payload![3]!)).ShouldBe(4);
        for (int index = 1; index < warnings.Length; index++)
        {
            (warnings[index].TimeStamp - warnings[index - 1].TimeStamp).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(990));
        }
        (source.UnprotectedResponses - before).ShouldBe(4);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Event source: The response counter remains exact without a listener")]
    public async Task UseSecurityHeaders_NoListener_ShouldCountUnprotectedResponse()
    {
        // Arrange
        SecurityHeadersEventSource source = SecurityHeadersEventSource.Log;
        source.IsEnabled().ShouldBeFalse();
        using CancellationTokenSource cancellation = new(_timeout);
        await using WebApplicationTestFactory factory = CreateReadOnlyFactory();
        factory.Application.UseSecurityHeaders();
        factory.Application.Use((context, next) => Task.CompletedTask);
        using HttpClient client = factory.CreateClient();
        long before = source.UnprotectedResponses;

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        (source.UnprotectedResponses - before).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Event source: Nested registrations count an unprotected response once")]
    public async Task UseSecurityHeaders_NestedReadOnlyCallbacks_ShouldCountResponseOnce()
    {
        // Arrange
        SecurityHeadersEventSource source = SecurityHeadersEventSource.Log;
        using CancellationTokenSource cancellation = new(_timeout);
        await using WebApplicationTestFactory factory = CreateReadOnlyFactory();
        factory.Application.UseSecurityHeaders();
        factory.Application.UseSecurityHeaders();
        factory.Application.Use((context, next) => Task.CompletedTask);
        using HttpClient client = factory.CreateClient();
        long before = source.UnprotectedResponses;

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        (source.UnprotectedResponses - before).ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Event source: Successful, not-modified and disabled responses do not warn")]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public async Task UseSecurityHeaders_ProtectedOrExcludedResponse_ShouldNotWarn(bool readOnly, bool notModified, bool disabled, bool noCsp)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        SecurityHeadersEventSource source = SecurityHeadersEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Warning);
        long before = source.UnprotectedResponses;
        string requestId = ActivityTraceId.CreateRandom().ToHexString();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddSecurityHeaders(headers =>
        {
            if (noCsp)
            {
                headers.Options.Framing = null;
            }
        });
        if (readOnly)
        {
            factory.Application.Use((context, next) => next.Invoke(new SecurityHeadersReadOnlyContext(context)));
        }
        factory.Application.UseSecurityHeaders();
        var route = factory.Application.UseRouting().Map(CohesionHttpMethod.Get, "/", new RouterRouteHandler(context =>
        {
            context.Response.StatusCode = notModified ? CohesionHttpStatusCode.NotModified : CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        }));
        if (disabled)
        {
            route.DisableSecurityHeaders();
        }
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateRequest("/", requestId);

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        (source.UnprotectedResponses - before).ShouldBe(0);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 1 && entry.Payload![4]!.Equals(requestId));
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Event source: A custom server's 101 callback skips policy and warnings")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseSecurityHeaders_SwitchingProtocolsCallback_ShouldSkipPolicyAndWarning(bool readOnly)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        SecurityHeadersEventSource source = SecurityHeadersEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Warning);
        long before = source.UnprotectedResponses;
        string requestId = ActivityTraceId.CreateRandom().ToHexString();
        SecurityHeadersStartingFeature starting = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.Use((context, next) =>
        {
            context.Features.Set<IWebResponseStartingFeature>(starting);
            return next.Invoke(readOnly ? new SecurityHeadersReadOnlyContext(context) : context);
        });
        factory.Application.UseSecurityHeaders();
        factory.Application.Use(async (context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.SwitchingProtocols;
            await starting.InvokeAsync();
            context.Response.Headers.ContainsKey(HttpHeaderKey.ContentSecurityPolicy).ShouldBeFalse();
            context.Response.Headers.ContainsKey(HttpHeaderKey.XContentTypeOptions).ShouldBeFalse();
            // The synthetic server callback has been exercised; complete this test request
            // with an ordinary final response instead of asking the real transport to upgrade.
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        });
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateRequest("/", requestId);

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (source.UnprotectedResponses - before).ShouldBe(0);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 1 && entry.Payload![4]!.Equals(requestId));
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Event source: A failed starting callback aborts without an unprotected-response warning")]
    public async Task UseSecurityHeaders_CallbackFailure_ShouldNotCountUnsentResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        SecurityHeadersEventSource source = SecurityHeadersEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Warning);
        long before = source.UnprotectedResponses;
        string requestId = ActivityTraceId.CreateRandom().ToHexString();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddSecurityHeaders(headers => headers.Options.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp.ScriptSrc(sources => sources.Nonce())));
        factory.Application.Use((context, next) =>
        {
            context.Features.Set<ISecurityHeadersFeature>(new FixedNonceFeature("invalid nonce"));
            return next.Invoke(context);
        });
        factory.Application.UseSecurityHeaders();
        factory.Application.Use((context, next) => Task.CompletedTask);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateRequest("/", requestId);

        // Act
        await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);
        });

        // Assert
        (source.UnprotectedResponses - before).ShouldBe(0);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 1 && entry.Payload![4]!.Equals(requestId));
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Event source: WebSocket and extended-CONNECT takeovers skip policy and warnings")]
    [InlineData(WebApplicationTestProtocol.Http1)]
    [InlineData(WebApplicationTestProtocol.Http2)]
    public async Task UseSecurityHeaders_WebSocketAndExtendedConnect_ShouldSkipPolicyAndWarning(WebApplicationTestProtocol protocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        SecurityHeadersEventSource source = SecurityHeadersEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Warning);
        long before = source.UnprotectedResponses;
        string requestId = ActivityTraceId.CreateRandom().ToHexString();
        FixedNonceFeature nonce = new("invalid nonce that would fail a starting callback");
        int startingCallbacks = 0;
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = protocol });
        factory.Builder.Services.AddSecurityHeaders(headers => headers.Options.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp.ScriptSrc(sources => sources.Nonce())));
        factory.Application.Use((context, next) =>
        {
            context.Features.Set<ISecurityHeadersFeature>(nonce);
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                Interlocked.Increment(ref startingCallbacks);
                return Task.CompletedTask;
            }, null);
            return next.Invoke(context);
        });
        factory.Application.UseSecurityHeaders();
        factory.Application.UseWebSockets();
        factory.Application.Use(async (context, next) =>
        {
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(cancellationToken: cancellation.Token);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellation.Token);
            completed.TrySetResult();
        });
        using HttpClient httpClient = factory.CreateClient();
        using ClientWebSocket client = new();
        client.Options.HttpVersion = protocol == WebApplicationTestProtocol.Http2 ? System.Net.HttpVersion.Version20 : System.Net.HttpVersion.Version11;
        client.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        client.Options.SetRequestHeader("traceparent", $"00-{requestId}-0123456789abcdef-00");

        // Act
        await client.ConnectAsync(new Uri("ws://localhost/"), httpClient, cancellation.Token);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellation.Token);
        await completed.Task.WaitAsync(cancellation.Token);

        // Assert
        client.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
        startingCallbacks.ShouldBe(0);
        nonce.Reads.ShouldBe(0);
        (source.UnprotectedResponses - before).ShouldBe(0);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 1 && entry.Payload![4]!.Equals(requestId));
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    private static WebApplicationTestFactory CreateReadOnlyFactory()
    {
        WebApplicationTestFactory factory = new();
        factory.Application.Use((context, next) => next.Invoke(new SecurityHeadersReadOnlyContext(context)));
        return factory;
    }

    private static HttpRequestMessage CreateRequest(string path, string requestId)
    {
        HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, path);
        request.Headers.Add("traceparent", $"00-{requestId}-0123456789abcdef-00");
        return request;
    }
}
