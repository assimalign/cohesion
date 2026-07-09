using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Exercises the unified interceptor lifecycle (#875): the <c>Before*</c>/<c>After*</c> hooks of
/// <see cref="IHttpExchangeInterceptor"/> and the <see cref="IHttpExchangeControl"/> mechanisms —
/// a hook can mutate the final head at the last moment (<c>BeforeResponseHeadAsync</c>) and observe
/// completion (<c>AfterResponseAsync</c>); an application cancel (<see cref="IHttpContext.Cancel"/>)
/// or a takeover is honored by the transport with its version's wire behavior.
/// </summary>
public class InterceptorLifecycleTests
{
    // ------------------------------------------------------------- request side

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http1: Request hooks fire in order (AfterRequestHead → BeforeRequestBody → AfterRequestBody) with the knob frozen before the body")]
    public async Task Http1_RequestHooks_ShouldFireInLifecycleOrder()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\n\r\nhello");
        TestConnection connection = new(payload);
        RecordingRequestInterceptor recorder = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        recorder.Invocations.ShouldBe(["after-head", "before-body", "after-body"]);
        recorder.KnobFrozenAtHead.ShouldBe(false);
        recorder.KnobFrozenAtBeforeBody.ShouldBe(true);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http2: Request hooks fire in order through the shared pipeline")]
    public async Task Http2_RequestHooks_ShouldFireInLifecycleOrder()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(
            1, "POST", "/upload", "https", "api.test", body: Encoding.ASCII.GetBytes("hello"));
        TestConnection connection = new(payload);
        RecordingRequestInterceptor recorder = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        recorder.Invocations.ShouldBe(["after-head", "before-body", "after-body"]);
        recorder.KnobFrozenAtBeforeBody.ShouldBe(true);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http1: A BeforeRequestBody rejection precedes the automatic Expect 100-continue solicitation")]
    public async Task Http1_BeforeRequestBodyRejection_ShouldPrecede100ContinueSolicit()
    {
        // The client declares Expect: 100-continue and withholds the body. A BeforeRequestBody hook
        // that rejects must do so BEFORE the transport solicits the body — the peer sees the 4xx
        // rejection and never a 100 Continue.
        byte[] head = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n");
        TestConnection connection = new(head, completeInput: false);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(new RejectingBeforeBodyInterceptor());

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeFalse("the rejected request never becomes an exchange");

        string wire = Encoding.ASCII.GetString(await connection.ReadOutputAsync());
        wire.ShouldContain("HTTP/1.1 417");
        wire.ShouldNotContain("100 Continue");
    }

    // ---------------------------------------------------- BeforeResponseHead

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http1: BeforeResponseHead mutates the final head at the last moment (buffered path)")]
    public async Task Http1_BeforeResponseHead_ShouldMutateFinalHead()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        RecordingResponseInterceptor recorder = new()
        {
            OnBeforeResponseHead = ctx => ctx.Headers[new HttpHeaderKey("X-Hooked")] = "yes",
        };
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Response.StatusCode = HttpStatusCode.Ok;
        await connectionContext.SendAsync(context);

        string wire = Encoding.ASCII.GetString(await connection.ReadOutputAsync());
        wire.ShouldContain("HTTP/1.1 200");
        wire.ShouldContain("X-Hooked: yes");

        recorder.BeforeResponseCount.ShouldBe(1);
        recorder.BeforeResponseHeadCount.ShouldBe(1);
        recorder.AfterResponseCount.ShouldBe(1);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http2: BeforeResponseHead mutates the final HEADERS block (buffered path)")]
    public async Task Http2_BeforeResponseHead_ShouldMutateFinalHead()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        TestConnection connection = new(payload);
        RecordingResponseInterceptor recorder = new()
        {
            OnBeforeResponseHead = ctx => ctx.Headers[new HttpHeaderKey("X-Hooked")] = "yes",
        };
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Response.StatusCode = HttpStatusCode.Ok;
        await connectionContext.SendAsync(context);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = await ReadHttp2FramesUntilAsync(
            connection, fs => fs.Any(f => f.FrameType == 1 /* HEADERS */));
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(
            frames.First(f => f.FrameType == 1).Payload);

        headers[":status"].ShouldBe("200");
        headers["x-hooked"].ShouldBe("yes");
        recorder.AfterResponseCount.ShouldBe(1);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http3: BeforeResponseHead mutates the final field section (buffered path)")]
    public async Task Http3_BeforeResponseHead_ShouldMutateFinalHead()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "a");
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        RecordingResponseInterceptor recorder = new()
        {
            OnBeforeResponseHead = ctx => ctx.Headers[new HttpHeaderKey("X-Hooked")] = "yes",
        };
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Response.StatusCode = HttpStatusCode.Ok;
        await connectionContext.SendAsync(context);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = await ReadHttp3FramesUntilAsync(
            stream, fs => fs.Any(f => f.FrameType == 1 /* HEADERS */));
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(
            frames.First(f => f.FrameType == 1).Payload);

        headers[":status"].ShouldBe("200");
        headers["x-hooked"].ShouldBe("yes");
        recorder.AfterResponseCount.ShouldBe(1);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http1: BeforeResponseHead fires exactly once when the streaming path commits the head")]
    public async Task Http1_BeforeResponseHead_OnStreamingCommit_ShouldFireOnce()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /sse HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        RecordingResponseInterceptor recorder = new()
        {
            OnBeforeResponseHead = ctx => ctx.Headers[new HttpHeaderKey("X-Hooked")] = "yes",
        };
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        // The first streamed write commits the head — the hook fires here, before the head bytes.
        await context.Response.Streaming.WriteAsync(Encoding.UTF8.GetBytes("event-1"));
        recorder.BeforeResponseHeadCount.ShouldBe(1);

        string wire = Encoding.ASCII.GetString(await connection.ReadOutputAsync());
        wire.ShouldContain("X-Hooked: yes");

        // Finalize must not re-fire the head hook; completion fires the after hook once.
        await connectionContext.SendAsync(context);
        recorder.BeforeResponseHeadCount.ShouldBe(1);
        recorder.AfterResponseCount.ShouldBe(1);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http1: Request hooks skip the body hooks for a CONNECT tunnel")]
    public async Task Http1_RequestHooks_OnConnect_ShouldSkipBodyHooks()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n");
        TestConnection connection = new(payload);
        RecordingRequestInterceptor recorder = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        // RFC 9110 §9.3.6 — a CONNECT's post-head octets are tunnel traffic, not a message body:
        // the head hook runs, the body hooks (BeforeRequestBody / AfterRequestBody) are skipped.
        recorder.Invocations.ShouldBe(["after-head"]);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http2: BeforeResponseHead fires exactly once when the streaming path commits the head")]
    public async Task Http2_BeforeResponseHead_OnStreamingCommit_ShouldFireOnce()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/sse", "https", "api.test");
        TestConnection connection = new(payload);
        RecordingResponseInterceptor recorder = new()
        {
            OnBeforeResponseHead = ctx => ctx.Headers[new HttpHeaderKey("X-Hooked")] = "yes",
        };
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        await context.Response.Streaming.WriteAsync(Encoding.UTF8.GetBytes("chunk-1"));
        recorder.BeforeResponseHeadCount.ShouldBe(1);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = await ReadHttp2FramesUntilAsync(
            connection, fs => fs.Any(f => f.FrameType == 1 /* HEADERS */));
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(
            frames.First(f => f.FrameType == 1).Payload);
        headers["x-hooked"].ShouldBe("yes");

        await connectionContext.SendAsync(context);
        recorder.BeforeResponseHeadCount.ShouldBe(1);
        recorder.AfterResponseCount.ShouldBe(1);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http1: A BeforeResponseHead hook that starts the streamed response does not cause a second head")]
    public async Task Http1_HookWritesSinkInBeforeResponseHead_ShouldNotDoubleHead()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        RecordingResponseInterceptor recorder = new()
        {
            // Pathological but legal: the last-word hook itself starts the response through the
            // raw sink. The transport must finalize that response, never write a second head.
            OnBeforeResponseHead = ctx => ctx.ResponseBody.Write(Encoding.UTF8.GetBytes("hooked"), 0, 6),
        };
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Response.StatusCode = HttpStatusCode.Ok;
        await connectionContext.SendAsync(context);

        StringBuilder wire = new();
        for (int attempt = 0; attempt < 20 && !wire.ToString().Contains("0\r\n\r\n", StringComparison.Ordinal); attempt++)
        {
            wire.Append(Encoding.ASCII.GetString(await connection.ReadOutputAsync()));
        }

        string observed = wire.ToString();
        CountOccurrences(observed, "HTTP/1.1").ShouldBe(1);
        observed.ShouldContain("hooked");
        recorder.AfterResponseCount.ShouldBe(1);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http1: The control reports the response started after the buffered send (no late interim writes)")]
    public async Task Http1_AfterBufferedSend_ShouldRejectLateInterimWrites()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        RecordingResponseInterceptor recorder = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        IHttpExchangeControl control = recorder.CapturedControl!;
        control.HasResponseStarted.ShouldBeFalse();
        control.CanWriteInterimResponse.ShouldBeTrue();

        context.Response.StatusCode = HttpStatusCode.Ok;
        await connectionContext.SendAsync(context);

        // RFC 9110 §15.2 — every interim response precedes the final one. After the buffered final
        // response is on the wire the probes report the commit and a late write is rejected.
        control.HasResponseStarted.ShouldBeTrue();
        control.CanWriteInterimResponse.ShouldBeFalse();
        control.CanTakeOver.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await control.WriteInterimResponseAsync(HttpStatusCode.EarlyHints));

        await context.DisposeAsync();
    }

    // ------------------------------------------------------- directives: Abort

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http1: An application cancel (IHttpContext.Cancel) writes no response and ends the connection")]
    public async Task Http1_ApplicationCancel_ShouldWriteNothingAndCloseConnection()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        RecordingResponseInterceptor recorder = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        IHttpContext context = enumerator.Current;

        // Aborting is authored at the application layer — IHttpContext.Cancel — not on the seam;
        // the control merely observes the consequence through its probes.
        IHttpExchangeControl control = recorder.CapturedControl!;
        control.ShouldNotBeNull();
        control.CanWriteInterimResponse.ShouldBeTrue();

        context.Cancel();
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        control.CanWriteInterimResponse.ShouldBeFalse("an aborted exchange can no longer carry an interim response");
        control.CanTakeOver.ShouldBeFalse("an aborted exchange can no longer be taken over");

        await connectionContext.SendAsync(context);

        // No response bytes were written, the after-hook never fired, and the keep-alive loop ends.
        recorder.AfterResponseCount.ShouldBe(0);
        (await enumerator.MoveNextAsync()).ShouldBeFalse();

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http2: An application cancel (IHttpContext.Cancel) resets the stream with RST_STREAM(CANCEL)")]
    public async Task Http2_ApplicationCancel_ShouldResetStreamWithCancel()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        TestConnection connection = new(payload);
        RecordingResponseInterceptor recorder = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Cancel();
        await connectionContext.SendAsync(context);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = await ReadHttp2FramesUntilAsync(
            connection, fs => fs.Any(f => f.FrameType == 3 /* RST_STREAM */));
        byte[] rst = frames.First(f => f.FrameType == 3).Payload;

        // RFC 9113 §6.4 — 4-octet error code; CANCEL = 0x8.
        rst.Length.ShouldBe(4);
        rst[3].ShouldBe((byte)0x8);
        frames.ShouldNotContain(f => f.FrameType == 1, "no response HEADERS may follow an aborted exchange");
        recorder.AfterResponseCount.ShouldBe(0);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http3: An application cancel (IHttpContext.Cancel) resets the request stream")]
    public async Task Http3_ApplicationCancel_ShouldAbortRequestStream()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "a");
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        RecordingResponseInterceptor recorder = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Cancel();
        await connectionContext.SendAsync(context);

        stream.IsAborted.ShouldBeTrue("an aborted HTTP/3 exchange must reset its request stream");
        recorder.AfterResponseCount.ShouldBe(0);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http2: An application cancel during BeforeResponseHead resets the stream instead of writing the head")]
    public async Task Http2_CancelDuringBeforeResponseHead_ShouldResetStream()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        TestConnection connection = new(payload);
        RecordingResponseInterceptor recorder = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        // Models an application-layer cancel (e.g. a timeout middleware) landing at the last
        // possible moment — inside the final BeforeResponseHead window. The transport re-reads the
        // exchange state after the hooks run, so the cancel is honored instead of the head.
        recorder.OnBeforeResponseHead = _ => context.Cancel();

        context.Response.StatusCode = HttpStatusCode.Ok;
        await connectionContext.SendAsync(context);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = await ReadHttp2FramesUntilAsync(
            connection, fs => fs.Any(f => f.FrameType == 3 /* RST_STREAM */));
        frames.First(f => f.FrameType == 3).Payload[3].ShouldBe((byte)0x8, "the reset carries CANCEL");
        frames.ShouldNotContain(f => f.FrameType == 1, "no response HEADERS may follow a cancelled exchange");
        recorder.AfterResponseCount.ShouldBe(0);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http1: An application cancel during BeforeResponseHead is honored before the head is written")]
    public async Task Http1_CancelDuringBeforeResponseHead_ShouldSuppressHead()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        RecordingResponseInterceptor recorder = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(recorder);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        // Same last-moment application cancel as the HTTP/2 variant, on the h1 buffered path.
        recorder.OnBeforeResponseHead = _ => context.Cancel();

        context.Response.StatusCode = HttpStatusCode.Ok;
        await connectionContext.SendAsync(context);

        recorder.BeforeResponseHeadCount.ShouldBe(1);
        recorder.AfterResponseCount.ShouldBe(0);

        await context.DisposeAsync();
    }

    // -------------------------------------------------------- scope partition

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle/Http1: Scopes partition the single interceptor list — undeclared phases are never invoked")]
    public async Task Http1_ScopePartition_ShouldSkipUndeclaredPhases()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        ScopeProbeInterceptor requestScoped = new(HttpInterceptorScopes.Request);
        ScopeProbeInterceptor responseScoped = new(HttpInterceptorScopes.Response);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(requestScoped);
        options.Interceptors.Add(responseScoped);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Response.StatusCode = HttpStatusCode.Ok;
        await connectionContext.SendAsync(context);

        // The request-scoped probe saw only the request phase; the response-scoped probe only the
        // response phase — one registration list, invocation partitioned by declared interest.
        requestScoped.RequestHookCount.ShouldBeGreaterThan(0);
        requestScoped.ResponseHookCount.ShouldBe(0);
        responseScoped.RequestHookCount.ShouldBe(0);
        responseScoped.ResponseHookCount.ShouldBeGreaterThan(0);

        await context.DisposeAsync();
    }
    // ---------------------------------------------------- directives: TakeOver

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Lifecycle: The control reports takeover support per version (h1 yes; h2/h3 report-don't-throw false)")]
    public async Task Control_CanTakeOver_ShouldReportPerVersionSupport()
    {
        // HTTP/1.1 — an exchange owns its whole connection.
        {
            byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
                "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
            TestConnection connection = new(payload);
            RecordingResponseInterceptor recorder = new();
            HttpConnectionListenerOptions options = new();
            options.UseHttp1(new TestConnectionListener(connection));
            options.Interceptors.Add(recorder);

            await using HttpConnectionListener listener = new(options);
            IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
            IHttpContext context = await ReadSingleContextAsync(connectionContext);

            IHttpExchangeControl control = recorder.CapturedControl!;
            control.CanTakeOver.ShouldBeTrue();

            Stream raw = control.TakeOver();
            raw.ShouldNotBeNull();
            control.CanTakeOver.ShouldBeFalse("the takeover is one-shot");
            Should.Throw<InvalidOperationException>(() => control.TakeOver());

            // The transport suppressed its own response for the taken-over exchange.
            await connectionContext.SendAsync(context);
            recorder.AfterResponseCount.ShouldBe(0);
            await context.DisposeAsync();
        }

        // HTTP/2 — multiplexed streams over a shared connection: report false, throw on misuse.
        {
            byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
            TestConnection connection = new(payload);
            RecordingResponseInterceptor recorder = new();
            HttpConnectionListenerOptions options = new();
            options.UseHttp2(new TestConnectionListener(connection));
            options.Interceptors.Add(recorder);

            await using HttpConnectionListener listener = new(options);
            IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
            IHttpContext context = await ReadSingleContextAsync(connectionContext);

            recorder.CapturedControl!.CanTakeOver.ShouldBeFalse();
            Should.Throw<InvalidOperationException>(() => recorder.CapturedControl!.TakeOver());
            await context.DisposeAsync();
        }

        // HTTP/3 — multiplexed QUIC streams over a shared connection: same posture as HTTP/2.
        {
            byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "a");
            TestConnection stream = new(payload);
            TestMultiplexedConnection connection = new(stream);
            RecordingResponseInterceptor recorder = new();
            HttpConnectionListenerOptions options = new();
            options.UseHttp3(new TestMultiplexedConnectionListener(connection));
            options.Interceptors.Add(recorder);

            await using HttpConnectionListener listener = new(options);
            IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
            IHttpContext context = await ReadSingleContextAsync(connectionContext);

            recorder.CapturedControl!.CanTakeOver.ShouldBeFalse();
            Should.Throw<InvalidOperationException>(() => recorder.CapturedControl!.TakeOver());
            await context.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------- Helpers

    private static int CountOccurrences(string text, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private static async Task<IReadOnlyList<(long FrameType, byte[] Payload)>> ReadHttp2FramesUntilAsync(
        TestConnection connection,
        Func<IReadOnlyList<(long FrameType, byte[] Payload)>, bool> predicate)
    {
        List<byte> accumulated = new();

        for (int attempt = 0; attempt < 50; attempt++)
        {
            accumulated.AddRange(await connection.ReadOutputAsync());
            IReadOnlyList<(long FrameType, byte[] Payload)> frames =
                HttpProtocolPayloadFactory.ParseHttp2Frames(accumulated.ToArray());
            if (predicate(frames))
            {
                return frames;
            }
        }

        throw new InvalidOperationException("The expected HTTP/2 frames were not observed on the wire.");
    }

    private static async Task<IReadOnlyList<(long FrameType, byte[] Payload)>> ReadHttp3FramesUntilAsync(
        TestConnection connection,
        Func<IReadOnlyList<(long FrameType, byte[] Payload)>, bool> predicate)
    {
        List<byte> accumulated = new();

        for (int attempt = 0; attempt < 50; attempt++)
        {
            accumulated.AddRange(await connection.ReadOutputAsync());
            IReadOnlyList<(long FrameType, byte[] Payload)> frames =
                HttpProtocolPayloadFactory.ParseHttp3Frames(accumulated.ToArray());
            if (predicate(frames))
            {
                return frames;
            }
        }

        throw new InvalidOperationException("The expected HTTP/3 frames were not observed on the wire.");
    }

    /// <summary>
    /// Records the request-side lifecycle hooks in invocation order and snapshots the body-size
    /// knob's frozen state at each point.
    /// </summary>
    /// <summary>
    /// Counts request-phase vs response-phase hook invocations under a configurable scope — used
    /// to prove the transport partitions the single interceptor list by declared interest.
    /// </summary>
    private sealed class ScopeProbeInterceptor : HttpExchangeInterceptor
    {
        private readonly HttpInterceptorScopes _scopes;

        public ScopeProbeInterceptor(HttpInterceptorScopes scopes) => _scopes = scopes;

        public override HttpInterceptorScopes Scopes => _scopes;

        public int RequestHookCount { get; private set; }

        public int ResponseHookCount { get; private set; }

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context) => RequestHookCount++;

        public override void BeforeRequestBody(HttpExchangeInterceptorRequestContext context) => RequestHookCount++;

        public override Stream AfterRequestBody(HttpExchangeInterceptorRequestContext context, Stream body)
        {
            RequestHookCount++;
            return body;
        }

        public override void BeforeResponse(HttpExchangeInterceptorResponseContext context) => ResponseHookCount++;

        public override ValueTask BeforeResponseHeadAsync(HttpExchangeInterceptorResponseContext context, CancellationToken cancellationToken)
        {
            ResponseHookCount++;
            return ValueTask.CompletedTask;
        }

        public override ValueTask AfterResponseAsync(HttpExchangeInterceptorResponseContext context, CancellationToken cancellationToken)
        {
            ResponseHookCount++;
            return ValueTask.CompletedTask;
        }
    }
    private sealed class RecordingRequestInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

        public List<string> Invocations { get; } = new();

        public bool? KnobFrozenAtHead { get; private set; }

        public bool? KnobFrozenAtBeforeBody { get; private set; }

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            Invocations.Add("after-head");
            KnobFrozenAtHead = context.IsMaxRequestBodySizeReadOnly;
        }

        public override void BeforeRequestBody(HttpExchangeInterceptorRequestContext context)
        {
            Invocations.Add("before-body");
            KnobFrozenAtBeforeBody = context.IsMaxRequestBodySizeReadOnly;
        }

        public override Stream AfterRequestBody(HttpExchangeInterceptorRequestContext context, Stream body)
        {
            Invocations.Add("after-body");
            return body;
        }
    }

    /// <summary>
    /// Rejects every request from <see cref="IHttpRequestInterceptor.BeforeRequestBody"/> with
    /// <c>417 Expectation Failed</c> — used to prove the hook runs before the transport solicits
    /// an <c>Expect: 100-continue</c> body.
    /// </summary>
    private sealed class RejectingBeforeBodyInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

        public override void BeforeRequestBody(HttpExchangeInterceptorRequestContext context)
            => throw new HttpRequestRejectedException(HttpStatusCode.ExpectationFailed);
    }

    /// <summary>
    /// Records the response-side lifecycle hooks, captures the exchange control, and lets a test
    /// inject per-hook behavior (head mutation, abort) via <see cref="OnBeforeResponseHead"/>.
    /// </summary>
    private sealed class RecordingResponseInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

        public int BeforeResponseCount { get; private set; }

        public int BeforeResponseHeadCount { get; private set; }

        public int AfterResponseCount { get; private set; }

        public IHttpExchangeControl? CapturedControl { get; private set; }

        public Action<HttpExchangeInterceptorResponseContext>? OnBeforeResponseHead { get; set; }

        public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
        {
            BeforeResponseCount++;
            CapturedControl = context.Control;
        }

        public override ValueTask BeforeResponseHeadAsync(HttpExchangeInterceptorResponseContext context, CancellationToken cancellationToken)
        {
            BeforeResponseHeadCount++;
            OnBeforeResponseHead?.Invoke(context);
            return ValueTask.CompletedTask;
        }

        public override ValueTask AfterResponseAsync(HttpExchangeInterceptorResponseContext context, CancellationToken cancellationToken)
        {
            AfterResponseCount++;
            return ValueTask.CompletedTask;
        }
    }
}
