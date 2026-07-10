using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Exercises the request-parse interceptor seam end to end over the HTTP/1.1
/// transport: head hooks attaching features and adjusting the body-size knob,
/// body hooks wrapping the request stream, typed rejection, freeze timing, and
/// the zero-interceptor fast path.
/// </summary>
public class Http1InterceptorTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Head hook should attach a feature visible on the dispatched context")]
    public async Task AfterRequestHead_ShouldAttachFeatureVisibleOnContext()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        HttpConnectionListenerOptions options = new();
        FeatureAttachingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        TestFeature? feature = httpContext.Features.Get<TestFeature>();
        feature.ShouldNotBeNull();
        feature!.ObservedHost.ShouldBe("api.test");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Head hook lowering the cap should reject the body with 413 on the body read")]
    public async Task AfterRequestHead_LoweringCap_ShouldRejectBodyWith413()
    {
        // The listener-wide default (~28.6 MB) would accept this 64-octet body; the hook lowers
        // the per-request cap below it, so the streamed body read must reject with 413.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            $"POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 64\r\n\r\n{new string('x', 64)}");
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new CapSettingInterceptor(16));

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        await Should.ThrowAsync<IOException>(async () => await ReadBodyToEndAsync(httpContext.Request.Body));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Head hook raising the cap should admit a body over the listener limit")]
    public async Task AfterRequestHead_RaisingCap_ShouldAdmitLargerBody()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\n\r\nhello");
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new CapSettingInterceptor(1024));

        IHttpContext httpContext = await ReceiveFirstContextAsync(
            payload,
            options,
            http1 => http1.Limits.MaxRequestBodySize = 2); // would reject the 5-octet body

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Body hooks should wrap the request stream, last registered outermost")]
    public async Task AfterRequestBody_ShouldWrapStreamInRegistrationOrder()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\n\r\nhello");
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

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: A rejecting head hook should answer with its status and drop the connection")]
    public async Task AfterRequestHead_Rejecting_ShouldAnswerStatusAndDrop()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /forbidden HTTP/1.1\r\nHost: api.test\r\n\r\n");
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new RejectingInterceptor(HttpStatusCode.Forbidden));

        (bool yielded, string response) = await DriveAsync(payload, options);

        yielded.ShouldBeFalse();
        response.ShouldContain("403", Case.Sensitive);
        response.ShouldContain("Connection: close", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: The body-size knob should stay writable until the body is read, then freeze")]
    public async Task Knob_ShouldRemainWritableUntilBodyRead()
    {
        // #810: the request is dispatched at head with a streamed body, so the per-request cap can be
        // adjusted (by an endpoint / middleware) after dispatch — right up until the body is read,
        // at which point it freezes.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\n\r\nhello");
        HttpConnectionListenerOptions options = new();
        ContextCapturingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        interceptor.Captured.ShouldNotBeNull();
        interceptor.WasWritableDuringHeadHook.ShouldBeTrue();

        // Dispatched at head, body not yet read: still writable, so a post-dispatch adjustment sticks.
        interceptor.Captured!.IsMaxRequestBodySizeReadOnly.ShouldBeFalse();
        interceptor.Captured.MaxRequestBodySize = 1024;

        // Reading the body starts consuming it, freezing the knob.
        await ReadBodyToEndAsync(httpContext.Request.Body);

        interceptor.Captured.IsMaxRequestBodySizeReadOnly.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => interceptor.Captured.MaxRequestBodySize = 1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: A post-dispatch cap lowered before the body is read should be enforced")]
    public async Task PostDispatchCapLowering_ShouldBeEnforcedOnBodyRead()
    {
        // #810: an endpoint / middleware lowers the per-request cap after dispatch, before reading the
        // body. The transport enforces whatever the cap is when the body read begins — here 4 octets,
        // below the 5-octet body — so the read is rejected with 413.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\n\r\nhello");
        HttpConnectionListenerOptions options = new();
        ContextCapturingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        // Stand in for middleware running after dispatch and before the body is read.
        interceptor.Captured.ShouldNotBeNull();
        interceptor.Captured!.MaxRequestBodySize = 4;

        await Should.ThrowAsync<IOException>(async () => await ReadBodyToEndAsync(httpContext.Request.Body));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Head hooks should observe read-only headers")]
    public async Task AfterRequestHead_Headers_ShouldBeReadOnly()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nContent-Type: text/plain\r\n\r\n");
        HttpConnectionListenerOptions options = new();
        HeaderProbingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        await ReceiveFirstContextAsync(payload, options);

        interceptor.HeadersWereReadOnly.ShouldBeTrue();
        interceptor.ObservedContentType.ShouldBe("text/plain");
        interceptor.MutationThrew.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: CONNECT should run head hooks but skip body hooks")]
    public async Task Connect_ShouldRunHeadHookAndSkipBodyHook()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "CONNECT origin.example.com:443 HTTP/1.1\r\nHost: origin.example.com:443\r\n\r\n");
        HttpConnectionListenerOptions options = new();
        InvocationRecordingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        httpContext.Request.Method.ShouldBe(HttpMethod.Connect);
        interceptor.HeadInvocations.ShouldBe(1);
        interceptor.BodyInvocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Body hooks should run for empty bodies")]
    public async Task EmptyBody_ShouldStillRunBodyHooks()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        HttpConnectionListenerOptions options = new();
        InvocationRecordingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        await ReceiveFirstContextAsync(payload, options);

        interceptor.HeadInvocations.ShouldBe(1);
        interceptor.BodyInvocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Hook-attached disposable features should be disposed with the exchange after a body-read rejection")]
    public async Task HookAttachedFeature_ShouldBeDisposedOnExchangeDispose()
    {
        // The head hook attaches a disposable feature; the oversized Content-Length body is rejected
        // (413) on the streamed read, after dispatch. The exchange now owns the disposal walk, so
        // disposing it disposes the hook-attached feature — it must not leak.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 4096\r\n\r\n");
        HttpConnectionListenerOptions options = new();
        DisposableFeatureAttachingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options, http1 => http1.Limits.MaxRequestBodySize = 16);

        await Should.ThrowAsync<IOException>(async () => await ReadBodyToEndAsync(httpContext.Request.Body));

        await httpContext.DisposeAsync();

        interceptor.Feature.ShouldNotBeNull();
        interceptor.Feature!.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: A body-hook rejection should dispose the partial wrapper chain and hook-attached features")]
    public async Task BodyHookRejection_ShouldDisposeWrapperChainAndFeatures()
    {
        // Interceptor 1 attaches a disposable feature and wraps the body; interceptor 2 rejects
        // from its body hook. The already-built wrapper chain and the attached feature must both
        // be torn down before the rejection surfaces.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\n\r\nhello");
        HttpConnectionListenerOptions options = new();
        DisposableFeatureAttachingInterceptor first = new();
        WrappingInterceptor wrapper = new("inner");
        BodyRejectingInterceptor rejecting = new(HttpStatusCode.UnProcessableEntity);
        options.Interceptors.Add(first);
        options.Interceptors.Add(wrapper);
        options.Interceptors.Add(rejecting);

        (bool yielded, string response) = await DriveAsync(payload, options);

        yielded.ShouldBeFalse();
        response.ShouldContain("422", Case.Sensitive);
        first.Feature!.DisposeCount.ShouldBe(1);
        wrapper.Created.ShouldNotBeNull();
        wrapper.Created!.Disposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Registrations after listener construction should have no effect")]
    public async Task Interceptors_AddedAfterListenerConstruction_ShouldNotRun()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);

        // The listener snapshotted the (empty) list at construction; this registration is inert.
        InvocationRecordingInterceptor late = new();
        options.Interceptors.Add(late);

        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await ReadSingleContextAsync(context);

        late.HeadInvocations.ShouldBe(0);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<IHttpContext> ReceiveFirstContextAsync(byte[] payload, HttpConnectionListenerOptions options, Action<Http1ConnectionListenerOptions>? configure = null)
    {
        TestConnection connection = new(payload);
        options.UseHttp1(new TestConnectionListener(connection), configure ?? (static _ => { }));

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

    private static async Task ReadBodyToEndAsync(Stream body)
    {
        byte[] buffer = new byte[8192];
        while (await body.ReadAsync(buffer) > 0)
        {
        }
    }

    private static async Task<(bool Yielded, string Response)> DriveAsync(byte[] payload, HttpConnectionListenerOptions options, Action<Http1ConnectionListenerOptions>? configure = null)
    {
        TestConnection connection = new(payload);
        options.UseHttp1(new TestConnectionListener(connection), configure ?? (static _ => { }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();

        bool yielded;
        await using (IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator())
        {
            yielded = await enumerator.MoveNextAsync();
        }

        string response = Encoding.ASCII.GetString(await connection.ReadOutputAsync());
        return (yielded, response);
    }

    // ------------------------------------------------------------------ doubles

    private sealed class TestFeature : IHttpFeature
    {
        public string Name => "Cohesion.Tests.InterceptorAttachedFeature";

        public string? ObservedHost { get; init; }
    }

    private sealed class FeatureAttachingInterceptor : HttpExchangeInterceptor
    {
        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            context.Features.Set(new TestFeature { ObservedHost = context.Host.Value });
        }
    }

    private sealed class CapSettingInterceptor : HttpExchangeInterceptor
    {
        private readonly long? _cap;

        public CapSettingInterceptor(long? cap)
        {
            _cap = cap;
        }

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            context.MaxRequestBodySize = _cap;
        }
    }

    private sealed class WrappingInterceptor : HttpExchangeInterceptor
    {
        private readonly string _tag;

        public WrappingInterceptor(string tag)
        {
            _tag = tag;
        }

        public TaggedStream? Created { get; private set; }

        public override Stream AfterRequestBody(HttpExchangeInterceptorRequestContext context, Stream body)
        {
            Created = new TaggedStream(body, _tag);
            return Created;
        }
    }

    private sealed class DisposableTestFeature : IHttpFeature, IDisposable
    {
        public string Name => "Cohesion.Tests.DisposableInterceptorFeature";

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class DisposableFeatureAttachingInterceptor : HttpExchangeInterceptor
    {
        public DisposableTestFeature? Feature { get; private set; }

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            Feature = new DisposableTestFeature();
            context.Features.Set(Feature);
        }
    }

    private sealed class BodyRejectingInterceptor : HttpExchangeInterceptor
    {
        private readonly HttpStatusCode _statusCode;

        public BodyRejectingInterceptor(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public override Stream AfterRequestBody(HttpExchangeInterceptorRequestContext context, Stream body)
        {
            throw new HttpRequestRejectedException(_statusCode);
        }
    }

    private sealed class RejectingInterceptor : HttpExchangeInterceptor
    {
        private readonly HttpStatusCode _statusCode;

        public RejectingInterceptor(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            throw new HttpRequestRejectedException(_statusCode);
        }
    }

    private sealed class ContextCapturingInterceptor : HttpExchangeInterceptor
    {
        public HttpExchangeInterceptorRequestContext? Captured { get; private set; }

        public bool WasWritableDuringHeadHook { get; private set; }

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            Captured = context;
            WasWritableDuringHeadHook = !context.IsMaxRequestBodySizeReadOnly;
        }
    }

    private sealed class HeaderProbingInterceptor : HttpExchangeInterceptor
    {
        public bool HeadersWereReadOnly { get; private set; }

        public bool MutationThrew { get; private set; }

        public string? ObservedContentType { get; private set; }

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            HeadersWereReadOnly = context.Headers.IsReadOnly;
            ObservedContentType = context.Headers[HttpHeaderKey.ContentType].Value;

            try
            {
                context.Headers[HttpHeaderKey.ContentLength] = "999";
            }
            catch (InvalidOperationException)
            {
                MutationThrew = true;
            }
        }
    }

    private sealed class InvocationRecordingInterceptor : HttpExchangeInterceptor
    {
        public int HeadInvocations { get; private set; }

        public int BodyInvocations { get; private set; }

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            HeadInvocations++;
        }

        public override Stream AfterRequestBody(HttpExchangeInterceptorRequestContext context, Stream body)
        {
            BodyInvocations++;
            return body;
        }
    }

    private sealed class TaggedStream : Stream
    {
        public TaggedStream(Stream inner, string tag)
        {
            Inner = inner;
            Tag = tag;
        }

        public Stream Inner { get; }

        public string Tag { get; }

        public bool Disposed { get; private set; }

        public override bool CanRead => Inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => Inner.Length;
        public override long Position
        {
            get => Inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
        public override void Flush() => Inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
                Inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
