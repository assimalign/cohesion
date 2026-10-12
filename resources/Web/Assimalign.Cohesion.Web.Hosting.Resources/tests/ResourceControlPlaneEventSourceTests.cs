using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Hosting.Resources.Internal;

using IPAddress = System.Net.IPAddress;
using IPEndPoint = System.Net.IPEndPoint;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

[Collection(ResourceControlPlaneEventSourceCollection.Name)]
public sealed class ResourceControlPlaneEventSourceTests
{
    private const string displayPrefix = "Cohesion Test [Web.Hosting.Resources] - Event source: ";

    [Fact(DisplayName = displayPrefix + "provider name matches its assembly")]
    public void GetName_ResourceControlPlaneSource_ShouldMatchAssemblyName()
    {
        // Arrange / Act
        string name = EventSource.GetName(typeof(ResourceControlPlaneEventSource));

        // Assert
        name.ShouldBe("Assimalign.Cohesion.Web.Hosting.Resources");
    }

    [Fact(DisplayName = displayPrefix + "strict manifest validates every event")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest validation preserves the concrete source type.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest validation inspects the concrete source type.")]
    public void GenerateManifest_ResourceControlPlaneSource_ShouldPassStrictValidation()
    {
        // Arrange / Act
        string? manifest = EventSource.GenerateManifest(typeof(ResourceControlPlaneEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNullOrEmpty();
    }

    [Theory(DisplayName = displayPrefix + "accepted stop emits once with the request feature and declared fields")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_StopAccepted_ShouldEmitLifecycleOnce(bool deferred)
    {
        // Arrange
        using var recorder = new EventSourceRecorder(ResourceControlPlaneEventSource.Log, EventLevel.Verbose);
        using var ambient = new Activity("unrelated-ambient").Start();
        var resource = new ResourceContext(resourceName: "stop-" + Guid.NewGuid().ToString("N"));
        var plane = new RecordingControlPlane();
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/stop", HttpMethod.Post);
        var requestId = new ControlPlaneRequestIdFeature();
        exchange.Features.Set<IWebRequestIdFeature>(requestId);
        if (deferred)
        {
            exchange.Features.Set<IWebResponseCompletionFeature>(new RecordingCompletionFeature());
        }

        // Act
        await InvokeAsync(plane, resource, exchange);

        // Assert
        EventWrittenEventArgs accepted = ForRequest(recorder, requestId).ShouldHaveSingleItem();
        accepted.EventId.ShouldBe(3);
        accepted.EventName.ShouldBe("StopAccepted");
        accepted.Level.ShouldBe(EventLevel.Informational);
        accepted.PayloadNames.ShouldBe(new[] { "resourceName", "requestId", "deferred" });
        Payload(accepted, "resourceName").ShouldBe(resource.ResourceName);
        Payload(accepted, "deferred").ShouldBe(deferred);
        requestId.ExpectedId.ShouldNotBe(ambient.TraceId.ToHexString());
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = displayPrefix + "rejected credential counter publishes and counts while disabled")]
    public async Task OnEventCommand_Enable_ShouldPublishExactCredentialCounter()
    {
        // Arrange
        ResourceControlPlaneEventSource source = ResourceControlPlaneEventSource.Log;
        long before = source.RejectedCredentials;
        var resource = new ResourceContext(resourceName: "counter-" + Guid.NewGuid().ToString("N"));
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/endpoints", HttpMethod.Get);
        exchange.Connection.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.11"), 12345);
        var requestId = new ControlPlaneRequestIdFeature();
        exchange.Features.Set<IWebRequestIdFeature>(requestId);

        // Act
        await InvokeAsync(new RecordingControlPlane(), resource, exchange);
        await InvokeAsync(new RecordingControlPlane(), resource, exchange);

        // Assert: a listener attaching later must observe the exact total, without eager id reads.
        (source.RejectedCredentials - before).ShouldBe(2);
        requestId.Reads.ShouldBe(0);
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await recorder.WaitForCountersAsync(["rejected-credentials"], cancellation.Token);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Theory(DisplayName = displayPrefix + "credential failures expose bounded reasons without credentials or callers")]
    [InlineData("missing-credential", 401)]
    [InlineData("unsupported-scheme", 401)]
    [InlineData("invalid-token", 401)]
    [InlineData("audience-mismatch", 403)]
    [InlineData("caller-mismatch", 403)]
    [InlineData("verifier-rejected", 401)]
    public async Task InvokeAsync_RejectedCredential_ShouldReportReasonWithoutSecrets(string reason, int status)
    {
        // Arrange
        await Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken.None);
        using var identity = new TestBootstrapIdentity("application-secret", "gateway-secret");
        var resource = new ResourceContext("application-secret", "credential-" + Guid.NewGuid().ToString("N"),
            "Testing", "gateway-secret", contentRootPath: null, endpoints: null, mounts: null, settings: null,
            references: null, bootstrapCredential: default, applicationTrustKey: identity.PublicKey, ambientValues: null);
        using var recorder = new EventSourceRecorder(ResourceControlPlaneEventSource.Log, EventLevel.Verbose);
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/endpoints", HttpMethod.Get);
        var requestId = new ControlPlaneRequestIdFeature();
        exchange.Features.Set<IWebRequestIdFeature>(requestId);
        string? authorization = reason switch
        {
            "missing-credential" => null,
            "unsupported-scheme" => "scheme-secret credential-secret",
            "invalid-token" => "Bearer credential-secret",
            "audience-mismatch" => "Bearer " + identity.Issue("audience-secret"),
            _ => "Bearer " + identity.Issue(resource.ResourceName!),
        };
        if (authorization is not null)
        {
            exchange.Request.Headers[HttpHeaderKey.Authorization] = authorization;
        }
        if (reason is "caller-mismatch" or "verifier-rejected")
        {
            var verification = reason == "caller-mismatch"
                ? new ResourceCredentialVerification(ResourceCredentialStatus.Authorized,
                    new ResourceCaller("caller-application-secret", "caller-subject-secret", ResourceCallerKind.Peer, []), null)
                : new ResourceCredentialVerification(ResourceCredentialStatus.Unauthorized, null, "refusal-secret");
            new ScriptedCredentialVerifier(verification).BindTo(resource, "EventSource-" + Guid.NewGuid().ToString("N"));
        }

        // Act
        await InvokeAsync(new RecordingControlPlane(), resource, exchange);

        // Assert
        EventWrittenEventArgs rejected = ForRequest(recorder, requestId).ShouldHaveSingleItem();
        rejected.EventId.ShouldBe(1);
        rejected.Level.ShouldBe(EventLevel.Warning);
        rejected.PayloadNames.ShouldBe(new[] { "resourceName", "requestId", "status", "reason", "route", "remoteEndPoint", "suppressedCount" });
        Payload(rejected, "reason").ShouldBe(reason);
        Payload(rejected, "route").ShouldBe("endpoints");
        Payload(rejected, "status").ShouldBe(status == 401 ? "Unauthorized" : "Forbidden");
        exchange.Response.StatusCode.ShouldBe(new HttpStatusCode(status));
        string text = string.Join("|", rejected.Payload!);
        text.ShouldNotContain("secret", Case.Sensitive);
        if (authorization is not null)
        {
            text.ShouldNotContain(authorization, Case.Sensitive);
        }
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = displayPrefix + "standalone refusals throttle events but retain exact counts")]
    public async Task InvokeAsync_RepeatedStandaloneRefusals_ShouldThrottleWithoutLosingCounter()
    {
        // Arrange
        await Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken.None);
        ResourceControlPlaneEventSource source = ResourceControlPlaneEventSource.Log;
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var resource = new ResourceContext(resourceName: "remote-" + Guid.NewGuid().ToString("N"));
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/path-secret", HttpMethod.Post);
        exchange.Connection.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.12"), 12345);
        exchange.Request.Headers["X-Forwarded-For"] = "forwarded-secret";
        var requestId = new ControlPlaneRequestIdFeature();
        exchange.Features.Set<IWebRequestIdFeature>(requestId);
        long before = source.RejectedCredentials;

        // Act
        for (int index = 0; index < 5; index++)
        {
            await InvokeAsync(new RecordingControlPlane(), resource, exchange);
        }
        EventWrittenEventArgs[] immediate = ForRequest(recorder, requestId);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken.None);
        await InvokeAsync(new RecordingControlPlane(), resource, exchange);

        // Assert
        immediate.ShouldHaveSingleItem();
        EventWrittenEventArgs[] events = ForRequest(recorder, requestId);
        events.Length.ShouldBe(2);
        Payload(events[1], "suppressedCount").ShouldBe(4);
        Payload(events[1], "reason").ShouldBe("non-loopback-peer");
        Payload(events[1], "status").ShouldBe("Forbidden");
        Payload(events[1], "route").ShouldBe("unknown");
        Payload(events[1], "remoteEndPoint").ShouldBe("192.0.2.12:12345");
        string.Join("|", events.SelectMany(item => item.Payload!)).ShouldNotContain("secret", Case.Sensitive);
        (source.RejectedCredentials - before).ShouldBe(6);
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Theory(DisplayName = displayPrefix + "malformed JSON and base64 report only the envelope exception type")]
    [InlineData("[\"body-secret\"]", "System.Text.Json.JsonException")]
    [InlineData("{\"id\":\"id-secret\",\"kind\":\"kind-secret\",\"owner\":\"owner-secret\",\"key\":\"key-secret\",\"payload\":\"invalid-base64-secret\"}", "System.FormatException")]
    public async Task InvokeAsync_MalformedEnvelope_ShouldReportEnvelopePhase(string body, string exceptionType)
    {
        // Arrange
        await Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken.None);
        using var recorder = new EventSourceRecorder(ResourceControlPlaneEventSource.Log, EventLevel.Verbose);
        var resource = new ResourceContext(resourceName: "envelope-" + Guid.NewGuid().ToString("N"));
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/commands", HttpMethod.Post);
        var requestId = new ControlPlaneRequestIdFeature();
        exchange.Features.Set<IWebRequestIdFeature>(requestId);
        await exchange.Request.Body.WriteAsync(Encoding.UTF8.GetBytes(body), CancellationToken.None);
        exchange.Request.Body.Position = 0;

        // Act
        await InvokeAsync(new RecordingControlPlane(), resource, exchange);

        // Assert
        AssertMalformed(recorder, requestId, "POST", "Envelope", exceptionType);
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory(DisplayName = displayPrefix + "handler ArgumentException is distinguished from envelope parsing")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task InvokeAsync_HandlerArgumentException_ShouldReportHandlerPhase(string method)
    {
        // Arrange
        await Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken.None);
        using var recorder = new EventSourceRecorder(ResourceControlPlaneEventSource.Log, EventLevel.Verbose);
        var resource = new ResourceContext(resourceName: "handler-" + Guid.NewGuid().ToString("N"));
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/commands", new HttpMethod(method));
        var requestId = new ControlPlaneRequestIdFeature();
        exchange.Features.Set<IWebRequestIdFeature>(requestId);
        await exchange.Request.Body.WriteAsync(Encoding.UTF8.GetBytes(
            "{\"id\":\"command-id-secret\",\"kind\":\"registered.kind\",\"owner\":\"command-owner-secret\",\"key\":\"command-key-secret\",\"payload\":\"cGF5bG9hZC1zZWNyZXQ=\"}"), CancellationToken.None);
        exchange.Request.Body.Position = 0;

        // Act
        await InvokeAsync(new ThrowingCommandControlPlane(), resource, exchange);

        // Assert
        AssertMalformed(recorder, requestId, method, "Handler", "System.ArgumentException");
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = displayPrefix + "malformed command floods report suppressed occurrences")]
    public async Task InvokeAsync_RepeatedMalformedCommands_ShouldReportSuppressedCount()
    {
        // Arrange
        await Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken.None);
        using var recorder = new EventSourceRecorder(ResourceControlPlaneEventSource.Log, EventLevel.Verbose);
        var resource = new ResourceContext(resourceName: "malformed-" + Guid.NewGuid().ToString("N"));
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/commands", HttpMethod.Post);
        var requestId = new ControlPlaneRequestIdFeature();
        exchange.Features.Set<IWebRequestIdFeature>(requestId);
        await exchange.Request.Body.WriteAsync("[]"u8.ToArray(), CancellationToken.None);
        var plane = new RecordingControlPlane();

        // Act
        for (int index = 0; index < 5; index++)
        {
            exchange.Request.Body.Position = 0;
            await InvokeAsync(plane, resource, exchange);
        }
        EventWrittenEventArgs[] immediate = ForRequest(recorder, requestId);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken.None);
        exchange.Request.Body.Position = 0;
        await InvokeAsync(plane, resource, exchange);

        // Assert
        immediate.ShouldHaveSingleItem();
        EventWrittenEventArgs[] events = ForRequest(recorder, requestId);
        events.Length.ShouldBe(2);
        events.ShouldAllBe(item => item.EventId == 2);
        Payload(events[1], "suppressedCount").ShouldBe(4);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    private static void AssertMalformed(EventSourceRecorder recorder, ControlPlaneRequestIdFeature requestId,
        string method, string phase, string exceptionType)
    {
        EventWrittenEventArgs malformed = ForRequest(recorder, requestId).ShouldHaveSingleItem();
        malformed.EventId.ShouldBe(2);
        malformed.Level.ShouldBe(EventLevel.Warning);
        malformed.PayloadNames.ShouldBe(new[] { "resourceName", "requestId", "httpMethod", "phase", "exceptionType", "suppressedCount" });
        Payload(malformed, "httpMethod").ShouldBe(method);
        Payload(malformed, "phase").ShouldBe(phase);
        Payload(malformed, "exceptionType").ShouldBe(exceptionType);
        string.Join("|", malformed.Payload!).ShouldNotContain("secret", Case.Sensitive);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    private static EventWrittenEventArgs[] ForRequest(EventSourceRecorder recorder, ControlPlaneRequestIdFeature requestId) =>
        recorder.Events.Where(item => item.PayloadNames is not null && item.PayloadNames.Contains("requestId") &&
            Equals(Payload(item, "requestId"), requestId.ExpectedId)).ToArray();

    private static object? Payload(EventWrittenEventArgs item, string name) => item.Payload![item.PayloadNames!.IndexOf(name)];

    private static Task InvokeAsync(IResourceControlPlane plane, ResourceContext resource, ControlPlaneExchange exchange) =>
        ResourceControlPlaneMiddleware.InvokeAsync(plane, resource, true, null, exchange,
            static _ => throw new InvalidOperationException("The control-plane route must be terminal."));
}
