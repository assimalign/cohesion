using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

using static Assimalign.Cohesion.Http.Connections.Tests.TestObjects.RequestInterceptorDoubles;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Exercises the request-parse interceptor seam end to end over the HTTP/3 transport at its
/// context-construction site (<c>Http3ConnectionContext.CreateContextAsync</c>): head hooks attaching
/// features and observing read-only headers, body hooks wrapping the lazily read request stream, the
/// freeze-at-first-read contract (HTTP/1.1 parity) and the cap the body read enforces, CONNECT body-hook
/// skipping, empty-body invocation, and typed rejection surfaced as a stream abort.
/// </summary>
public class Http3InterceptorTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Interceptors: Head hook should attach a feature visible on the dispatched context")]
    public async Task AfterRequestHead_ShouldAttachFeatureVisibleOnContext()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "api.test");
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new HostFeatureAttachingInterceptor());

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        RecordingFeature? feature = httpContext.Features.Get<RecordingFeature>();
        feature.ShouldNotBeNull();
        feature!.ObservedHost.ShouldBe("api.test");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Interceptors: Body hooks should wrap the request stream, last registered outermost")]
    public async Task AfterRequestBody_ShouldWrapStreamInRegistrationOrder()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request(
            "POST", "/upload", "https", "api.test", body: Encoding.UTF8.GetBytes("hello"));
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new WrappingInterceptor("inner"));
        options.Interceptors.Add(new WrappingInterceptor("outer"));

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        // Last registered = outermost wrapper; the payload still reads intact through the chain.
        TaggedStream outermost = httpContext.Request.Body.ShouldBeOfType<TaggedStream>();
        outermost.Tag.ShouldBe("outer");
        outermost.Inner.ShouldBeOfType<TaggedStream>().Tag.ShouldBe("inner");

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Interceptors: A rejecting head hook should abort the stream and yield no context")]
    public async Task AfterRequestHead_Rejecting_ShouldAbortStream()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/forbidden", "https", "api.test");
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new HeadRejectingInterceptor(HttpStatusCode.Forbidden));

        (bool yielded, TestConnection requestStream) = await DriveAsync(payload, options);

        // No context is dispatched, and the request stream is aborted (the h3 analogue of the h1
        // status-response-and-close, scoped to the single stream — the QUIC connection survives).
        yielded.ShouldBeFalse();
        requestStream.IsAborted.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Interceptors: A body-hook rejection should abort the stream and dispose the wrapper chain and features")]
    public async Task AfterRequestBody_Rejecting_ShouldAbortStreamAndDisposeWrapperChainAndFeatures()
    {
        // Interceptor 1 attaches a disposable feature and wraps the body; interceptor 2 rejects
        // from its body hook. The already-built wrapper chain and the attached feature must both be
        // torn down before the stream is aborted, since no exchange context will own their disposal.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request(
            "POST", "/upload", "https", "api.test", body: Encoding.UTF8.GetBytes("hello"));
        HttpConnectionListenerOptions options = new();
        DisposableFeatureAttachingInterceptor first = new();
        WrappingInterceptor wrapper = new("inner");
        BodyRejectingInterceptor rejecting = new(HttpStatusCode.UnProcessableEntity);
        options.Interceptors.Add(first);
        options.Interceptors.Add(wrapper);
        options.Interceptors.Add(rejecting);

        (bool yielded, TestConnection requestStream) = await DriveAsync(payload, options);

        yielded.ShouldBeFalse();
        requestStream.IsAborted.ShouldBeTrue();
        first.Feature!.DisposeCount.ShouldBe(1);
        wrapper.Created.ShouldNotBeNull();
        wrapper.Created!.Disposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Interceptors: The body-size knob should stay writable until the body is read, then freeze")]
    public async Task Knob_ShouldRemainWritableUntilBodyRead()
    {
        // HTTP/1.1 parity: the request is dispatched at its HEADERS frame with a lazily read body, so
        // the per-request cap can still be adjusted after dispatch — by middleware or an endpoint —
        // right up until the body is first read, at which point it freezes.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request(
            "POST", "/upload", "https", "api.test", body: Encoding.UTF8.GetBytes("hello"));
        HttpConnectionListenerOptions options = new();
        ContextCapturingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        interceptor.Captured.ShouldNotBeNull();
        interceptor.WasWritableDuringHeadHook.ShouldBeTrue();

        // Dispatched, body not yet read: still writable, so a post-dispatch adjustment sticks.
        interceptor.Captured!.IsMaxRequestBodySizeReadOnly.ShouldBeFalse();
        interceptor.Captured.MaxRequestBodySize = 1024;

        using (StreamReader reader = new(httpContext.Request.Body))
        {
            (await reader.ReadToEndAsync()).ShouldBe("hello");
        }

        interceptor.Captured.IsMaxRequestBodySizeReadOnly.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => interceptor.Captured.MaxRequestBodySize = 1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Interceptors: Head hooks should observe read-only headers")]
    public async Task AfterRequestHead_Headers_ShouldBeReadOnly()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request(
            "GET", "/", "https", "api.test", headers: new Dictionary<string, string> { ["content-type"] = "text/plain" });
        HttpConnectionListenerOptions options = new();
        HeaderProbingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        await ReceiveFirstContextAsync(payload, options);

        interceptor.HeadersWereReadOnly.ShouldBeTrue();
        interceptor.ObservedContentType.ShouldBe("text/plain");
        interceptor.MutationThrew.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Interceptors: CONNECT should run head hooks but skip body hooks")]
    public async Task Connect_ShouldRunHeadHookAndSkipBodyHook()
    {
        // A valid extended CONNECT (RFC 9220): its post-head octets are tunnel traffic, so body
        // hooks are skipped while head hooks still run — matching the h1 CONNECT behavior.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/chat"),
            (":authority", "api.test"));
        HttpConnectionListenerOptions options = new();
        InvocationRecordingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        httpContext.Request.Method.ShouldBe(HttpMethod.Connect);
        interceptor.HeadInvocations.ShouldBe(1);
        interceptor.BodyInvocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Interceptors: Body hooks should run for empty bodies")]
    public async Task EmptyBody_ShouldStillRunBodyHooks()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "api.test");
        HttpConnectionListenerOptions options = new();
        InvocationRecordingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        await ReceiveFirstContextAsync(payload, options);

        interceptor.HeadInvocations.ShouldBe(1);
        interceptor.BodyInvocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Interceptors: Head hook lowering the cap should reject the body with 413 on the body read")]
    public async Task AfterRequestHead_LoweringCap_ShouldRejectBodyWith413()
    {
        // The listener-wide default (~28.6 MB) would accept this 64-octet body; the hook lowers the
        // per-request cap below it, so the lazily read body is rejected (413) before its DATA frame is
        // delivered — HTTP/1.1 parity.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request(
            "POST", "/upload", "https", "api.test", body: Encoding.UTF8.GetBytes(new string('x', 64)));
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new CapSettingInterceptor(16));

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        using StreamReader reader = new(httpContext.Request.Body);
        await Should.ThrowAsync<IOException>(async () => await reader.ReadToEndAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Interceptors: Head hook raising the cap should admit a body over the listener limit")]
    public async Task AfterRequestHead_RaisingCap_ShouldAdmitLargerBody()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request(
            "POST", "/upload", "https", "api.test", body: Encoding.UTF8.GetBytes("hello"));
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new CapSettingInterceptor(1024));

        IHttpContext httpContext = await ReceiveFirstContextAsync(
            payload,
            options,
            http3 => http3.Limits.MaxRequestBodySize = 2); // would reject the 5-octet body

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Interceptors: Zero interceptors should dispatch a context with no attached features")]
    public async Task ZeroInterceptors_ShouldDispatchContextWithoutFeatures()
    {
        // The fast path: with no registered interceptors the transport allocates no parse context
        // or feature collection, and the request flows through exactly as before the seam existed.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "api.test");
        HttpConnectionListenerOptions options = new();

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        httpContext.Features.Get<RecordingFeature>().ShouldBeNull();
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<IHttpContext> ReceiveFirstContextAsync(
        byte[] payload,
        HttpConnectionListenerOptions options,
        Action<Http3ConnectionListenerOptions>? configure = null)
    {
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), configure ?? (static _ => { }));

        HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        return await ReadSingleContextAsync(context);
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private static async Task<(bool Yielded, TestConnection RequestStream)> DriveAsync(
        byte[] payload,
        HttpConnectionListenerOptions options)
    {
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();

        bool yielded;
        await using (IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator())
        {
            yielded = await enumerator.MoveNextAsync();
        }

        return (yielded, stream);
    }
}
