using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal.Http2;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

using static Assimalign.Cohesion.Http.Connections.Tests.TestObjects.RequestInterceptorDoubles;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Exercises the request-parse interceptor seam end to end over the HTTP/2 transport at its
/// context-construction site (<c>Http2Stream.CreateContextAsync</c>, reached from the frame pump's
/// END_HEADERS dispatch): head hooks attaching features and observing read-only headers, body
/// hooks wrapping the streaming request-body stream, the freeze contract, CONNECT body-hook
/// skipping, empty-body invocation, the unenforced-cap posture, and typed rejection surfaced as an
/// RST_STREAM.
/// </summary>
public class Http2InterceptorTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Interceptors: Head hook should attach a feature visible on the dispatched context")]
    public async Task OnRequestHead_ShouldAttachFeatureVisibleOnContext()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        HttpConnectionListenerOptions options = new();
        options.RequestInterceptors.Add(new HostFeatureAttachingInterceptor());

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        RecordingFeature? feature = httpContext.Features.Get<RecordingFeature>();
        feature.ShouldNotBeNull();
        feature!.ObservedHost.ShouldBe("api.test");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Interceptors: Body hooks should wrap the request stream, last registered outermost")]
    public async Task OnRequestBody_ShouldWrapStreamInRegistrationOrder()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(
            1, "POST", "/upload", "https", "api.test", body: System.Text.Encoding.UTF8.GetBytes("hello"));
        HttpConnectionListenerOptions options = new();
        options.RequestInterceptors.Add(new WrappingInterceptor("inner"));
        options.RequestInterceptors.Add(new WrappingInterceptor("outer"));

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        // Last registered = outermost wrapper; the payload still reads intact through the chain.
        TaggedStream outermost = httpContext.Request.Body.ShouldBeOfType<TaggedStream>();
        outermost.Tag.ShouldBe("outer");
        outermost.Inner.ShouldBeOfType<TaggedStream>().Tag.ShouldBe("inner");

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Interceptors: A rejecting head hook should reset the stream and yield no context")]
    public async Task OnRequestHead_Rejecting_ShouldResetStreamWithCancel()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/forbidden", "https", "api.test");
        HttpConnectionListenerOptions options = new();
        options.RequestInterceptors.Add(new HeadRejectingInterceptor(HttpStatusCode.Forbidden));

        (bool yielded, IReadOnlyList<(long FrameType, byte[] Payload)> frames) = await DriveAsync(payload, options);

        // No context is dispatched, and the peer observes an RST_STREAM(CANCEL) on the stream —
        // the h2 analogue of the h1 status-response-and-close, scoped to the single stream.
        yielded.ShouldBeFalse();
        AssertResetStream(frames, Http2ErrorCode.Cancel);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Interceptors: A body-hook rejection should reset the stream and dispose the wrapper chain and features")]
    public async Task OnRequestBody_Rejecting_ShouldResetStreamAndDisposeWrapperChainAndFeatures()
    {
        // Interceptor 1 attaches a disposable feature and wraps the body; interceptor 2 rejects
        // from its body hook. The already-built wrapper chain and the attached feature must both be
        // torn down before the stream is reset, since no exchange context will own their disposal.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(
            1, "POST", "/upload", "https", "api.test", body: System.Text.Encoding.UTF8.GetBytes("hello"));
        HttpConnectionListenerOptions options = new();
        DisposableFeatureAttachingInterceptor first = new();
        WrappingInterceptor wrapper = new("inner");
        BodyRejectingInterceptor rejecting = new(HttpStatusCode.UnProcessableEntity);
        options.RequestInterceptors.Add(first);
        options.RequestInterceptors.Add(wrapper);
        options.RequestInterceptors.Add(rejecting);

        (bool yielded, IReadOnlyList<(long FrameType, byte[] Payload)> frames) = await DriveAsync(payload, options);

        yielded.ShouldBeFalse();
        AssertResetStream(frames, Http2ErrorCode.Cancel);
        first.Feature!.DisposeCount.ShouldBe(1);
        wrapper.Created.ShouldNotBeNull();
        wrapper.Created!.Disposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Interceptors: The body-size knob should freeze once the head hooks have run")]
    public async Task Knob_ShouldFreezeAfterHeadHooks()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(
            1, "POST", "/upload", "https", "api.test", body: System.Text.Encoding.UTF8.GetBytes("hello"));
        HttpConnectionListenerOptions options = new();
        ContextCapturingInterceptor interceptor = new();
        options.RequestInterceptors.Add(interceptor);

        await ReceiveFirstContextAsync(payload, options);

        interceptor.Captured.ShouldNotBeNull();
        interceptor.WasWritableDuringHeadHook.ShouldBeTrue();
        interceptor.Captured!.IsMaxRequestBodySizeReadOnly.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => interceptor.Captured.MaxRequestBodySize = 1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Interceptors: Head hooks should observe read-only headers")]
    public async Task OnRequestHead_Headers_ShouldBeReadOnly()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(
            1, "GET", "/", "https", "api.test", headers: new Dictionary<string, string> { ["content-type"] = "text/plain" });
        HttpConnectionListenerOptions options = new();
        HeaderProbingInterceptor interceptor = new();
        options.RequestInterceptors.Add(interceptor);

        await ReceiveFirstContextAsync(payload, options);

        interceptor.HeadersWereReadOnly.ShouldBeTrue();
        interceptor.ObservedContentType.ShouldBe("text/plain");
        interceptor.MutationThrew.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Interceptors: CONNECT should run head hooks but skip body hooks")]
    public async Task Connect_ShouldRunHeadHookAndSkipBodyHook()
    {
        // A valid extended CONNECT (RFC 8441): its post-head octets are tunnel traffic, so body
        // hooks are skipped while head hooks still run — matching the h1 CONNECT behavior.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(frameType: 0x4, flags: 0, streamId: 0, payload: Array.Empty<byte>());
        byte[] headers = HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            1,
            0x4 | 0x1, // END_HEADERS + END_STREAM
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/chat"),
            (":authority", "api.test"));
        HttpConnectionListenerOptions options = new();
        InvocationRecordingInterceptor interceptor = new();
        options.RequestInterceptors.Add(interceptor);

        IHttpContext httpContext = await ReceiveFirstContextAsync(Combine(preface, settings, headers), options);

        httpContext.Request.Method.ShouldBe(HttpMethod.Connect);
        interceptor.HeadInvocations.ShouldBe(1);
        interceptor.BodyInvocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Interceptors: Body hooks should run for empty bodies")]
    public async Task EmptyBody_ShouldStillRunBodyHooks()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        HttpConnectionListenerOptions options = new();
        InvocationRecordingInterceptor interceptor = new();
        options.RequestInterceptors.Add(interceptor);

        await ReceiveFirstContextAsync(payload, options);

        interceptor.HeadInvocations.ShouldBe(1);
        interceptor.BodyInvocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Interceptors: A lowered cap should not reject the streamed body (h2 cap enforcement is follow-up)")]
    public async Task OnRequestHead_LoweringCap_ShouldNotRejectBody()
    {
        // HTTP/2 bounds request-body buffering via flow-control backpressure; the hard wire-level
        // cap is tracked follow-up work (see HttpConnectionListenerLimits.MaxRequestBodySize).
        // Lowering the cap at head-hook time therefore adjusts only the value hook-attached
        // features expose — the streamed body still dispatches and reads in full.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(
            1, "POST", "/upload", "https", "api.test", body: System.Text.Encoding.UTF8.GetBytes(new string('x', 64)));
        HttpConnectionListenerOptions options = new();
        options.RequestInterceptors.Add(new CapSettingInterceptor(16));

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync()).Length.ShouldBe(64);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Interceptors: Zero interceptors should dispatch a context with no attached features")]
    public async Task ZeroInterceptors_ShouldDispatchContextWithoutFeatures()
    {
        // The fast path: with no registered interceptors the transport allocates no parse context
        // or feature collection, and the request flows through exactly as before the seam existed.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        HttpConnectionListenerOptions options = new();

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        httpContext.Features.Get<RecordingFeature>().ShouldBeNull();
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<IHttpContext> ReceiveFirstContextAsync(byte[] payload, HttpConnectionListenerOptions options)
    {
        TestConnection connection = new(payload);
        options.UseHttp2(new TestConnectionListener(connection));

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

    private static async Task<(bool Yielded, IReadOnlyList<(long FrameType, byte[] Payload)> Frames)> DriveAsync(
        byte[] payload,
        HttpConnectionListenerOptions options)
    {
        TestConnection connection = new(payload);
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();

        bool yielded;
        await using (IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator())
        {
            yielded = await enumerator.MoveNextAsync();
        }

        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp2Frames(await connection.ReadOutputAsync());
        return (yielded, frames);
    }

    /// <summary>
    /// Asserts the first <c>RST_STREAM</c> on the wire carries <paramref name="expectedErrorCode"/>.
    /// Only the first is asserted because a rejection's reset removes the stream, so any DATA the
    /// peer already sent afterward legitimately draws a follow-up <c>RST_STREAM(STREAM_CLOSED)</c>
    /// (RFC 9113 §5.1).
    /// </summary>
    private static void AssertResetStream(
        IReadOnlyList<(long FrameType, byte[] Payload)> frames,
        Http2ErrorCode expectedErrorCode)
    {
        foreach ((long frameType, byte[] framePayload) in frames)
        {
            if (frameType == 3) // RST_STREAM
            {
                framePayload.Length.ShouldBe(4);
                uint code = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(framePayload);
                code.ShouldBe((uint)expectedErrorCode);
                return;
            }
        }

        throw new Shouldly.ShouldAssertException("Expected an RST_STREAM frame on the wire, but none was found.");
    }

    private static byte[] Combine(params byte[][] buffers)
    {
        using MemoryStream stream = new();

        foreach (byte[] buffer in buffers)
        {
            stream.Write(buffer, 0, buffer.Length);
        }

        return stream.ToArray();
    }
}
