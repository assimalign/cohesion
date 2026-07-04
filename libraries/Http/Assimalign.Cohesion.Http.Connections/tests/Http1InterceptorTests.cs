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
    public async Task OnRequestHead_ShouldAttachFeatureVisibleOnContext()
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

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Head hook lowering the cap should reject the body with 413")]
    public async Task OnRequestHead_LoweringCap_ShouldRejectBodyWith413()
    {
        // The listener-wide default (~28.6 MB) would accept this 64-octet body; the hook lowers
        // the per-request cap below it, so the transport must reject with 413.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            $"POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 64\r\n\r\n{new string('x', 64)}");
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new CapSettingInterceptor(16));

        (bool yielded, string response) = await DriveAsync(payload, options);

        yielded.ShouldBeFalse();
        response.ShouldContain("413", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Head hook raising the cap should admit a body over the listener limit")]
    public async Task OnRequestHead_RaisingCap_ShouldAdmitLargerBody()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\n\r\nhello");
        HttpConnectionListenerOptions options = new();
        options.Limits.MaxRequestBodySize = 2; // would reject the 5-octet body
        options.Interceptors.Add(new CapSettingInterceptor(1024));

        IHttpContext httpContext = await ReceiveFirstContextAsync(payload, options);

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Body hooks should wrap the request stream, last registered outermost")]
    public async Task OnRequestBody_ShouldWrapStreamInRegistrationOrder()
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
    public async Task OnRequestHead_Rejecting_ShouldAnswerStatusAndDrop()
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

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: The body-size knob should freeze once the body is consumed")]
    public async Task Knob_ShouldFreezeAfterHeadHooks()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\n\r\nhello");
        HttpConnectionListenerOptions options = new();
        ContextCapturingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        await ReceiveFirstContextAsync(payload, options);

        interceptor.Captured.ShouldNotBeNull();
        interceptor.WasWritableDuringHeadHook.ShouldBeTrue();
        interceptor.Captured!.IsMaxRequestBodySizeReadOnly.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => interceptor.Captured.MaxRequestBodySize = 1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Head hooks should observe read-only headers")]
    public async Task OnRequestHead_Headers_ShouldBeReadOnly()
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

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interceptors: Hook-attached disposable features should be disposed when the request is rejected pre-dispatch")]
    public async Task LimitRejection_ShouldDisposeHookAttachedFeatures()
    {
        // The head hook attaches a disposable feature, then the oversized Content-Length
        // declaration is rejected (413) before any context exists. The parser must honor the
        // disposal contract itself — the feature must not leak.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 4096\r\n\r\n");
        HttpConnectionListenerOptions options = new();
        options.Limits.MaxRequestBodySize = 16;
        DisposableFeatureAttachingInterceptor interceptor = new();
        options.Interceptors.Add(interceptor);

        (bool yielded, string response) = await DriveAsync(payload, options);

        yielded.ShouldBeFalse();
        response.ShouldContain("413", Case.Sensitive);
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

    private static async Task<IHttpContext> ReceiveFirstContextAsync(byte[] payload, HttpConnectionListenerOptions options)
    {
        TestConnection connection = new(payload);
        options.UseHttp1(new TestConnectionListener(connection));

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

    private static async Task<(bool Yielded, string Response)> DriveAsync(byte[] payload, HttpConnectionListenerOptions options)
    {
        TestConnection connection = new(payload);
        options.UseHttp1(new TestConnectionListener(connection));

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

    private sealed class FeatureAttachingInterceptor : IHttpRequestInterceptor
    {
        public void OnRequestHead(HttpRequestInterceptorContext context)
        {
            context.Features.Set(new TestFeature { ObservedHost = context.Host.Value });
        }
    }

    private sealed class CapSettingInterceptor : IHttpRequestInterceptor
    {
        private readonly long? _cap;

        public CapSettingInterceptor(long? cap)
        {
            _cap = cap;
        }

        public void OnRequestHead(HttpRequestInterceptorContext context)
        {
            context.MaxRequestBodySize = _cap;
        }
    }

    private sealed class WrappingInterceptor : IHttpRequestInterceptor
    {
        private readonly string _tag;

        public WrappingInterceptor(string tag)
        {
            _tag = tag;
        }

        public TaggedStream? Created { get; private set; }

        public Stream OnRequestBody(HttpRequestInterceptorContext context, Stream body)
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

    private sealed class DisposableFeatureAttachingInterceptor : IHttpRequestInterceptor
    {
        public DisposableTestFeature? Feature { get; private set; }

        public void OnRequestHead(HttpRequestInterceptorContext context)
        {
            Feature = new DisposableTestFeature();
            context.Features.Set(Feature);
        }
    }

    private sealed class BodyRejectingInterceptor : IHttpRequestInterceptor
    {
        private readonly HttpStatusCode _statusCode;

        public BodyRejectingInterceptor(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public Stream OnRequestBody(HttpRequestInterceptorContext context, Stream body)
        {
            throw new HttpRequestRejectedException(_statusCode);
        }
    }

    private sealed class RejectingInterceptor : IHttpRequestInterceptor
    {
        private readonly HttpStatusCode _statusCode;

        public RejectingInterceptor(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public void OnRequestHead(HttpRequestInterceptorContext context)
        {
            throw new HttpRequestRejectedException(_statusCode);
        }
    }

    private sealed class ContextCapturingInterceptor : IHttpRequestInterceptor
    {
        public HttpRequestInterceptorContext? Captured { get; private set; }

        public bool WasWritableDuringHeadHook { get; private set; }

        public void OnRequestHead(HttpRequestInterceptorContext context)
        {
            Captured = context;
            WasWritableDuringHeadHook = !context.IsMaxRequestBodySizeReadOnly;
        }
    }

    private sealed class HeaderProbingInterceptor : IHttpRequestInterceptor
    {
        public bool HeadersWereReadOnly { get; private set; }

        public bool MutationThrew { get; private set; }

        public string? ObservedContentType { get; private set; }

        public void OnRequestHead(HttpRequestInterceptorContext context)
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

    private sealed class InvocationRecordingInterceptor : IHttpRequestInterceptor
    {
        public int HeadInvocations { get; private set; }

        public int BodyInvocations { get; private set; }

        public void OnRequestHead(HttpRequestInterceptorContext context)
        {
            HeadInvocations++;
        }

        public Stream OnRequestBody(HttpRequestInterceptorContext context, Stream body)
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
