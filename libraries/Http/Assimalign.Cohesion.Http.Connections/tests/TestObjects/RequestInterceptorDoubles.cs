using System;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// Reusable <see cref="IHttpRequestInterceptor"/> / <see cref="IHttpFeature"/> doubles shared by
/// the HTTP/2 and HTTP/3 interceptor-seam suites, mirroring the doubles the HTTP/1.1 suite uses so
/// the three protocols are exercised against an identical set of behaviors.
/// </summary>
internal static class RequestInterceptorDoubles
{
    /// <summary>A feature that records the host observed at head-hook time.</summary>
    public sealed class RecordingFeature : IHttpFeature
    {
        public string Name => "Cohesion.Tests.InterceptorAttachedFeature";

        public string? ObservedHost { get; init; }
    }

    /// <summary>Head hook that attaches a <see cref="RecordingFeature"/> carrying the request host.</summary>
    public sealed class HostFeatureAttachingInterceptor : IHttpRequestInterceptor
    {
        public void AfterRequestHead(HttpRequestInterceptorContext context)
        {
            context.Features.Set(new RecordingFeature { ObservedHost = context.Host.Value });
        }
    }

    /// <summary>Head hook that sets the per-request body-size cap.</summary>
    public sealed class CapSettingInterceptor : IHttpRequestInterceptor
    {
        private readonly long? _cap;

        public CapSettingInterceptor(long? cap)
        {
            _cap = cap;
        }

        public void AfterRequestHead(HttpRequestInterceptorContext context)
        {
            context.MaxRequestBodySize = _cap;
        }
    }

    /// <summary>Body hook that wraps the request stream in a <see cref="TaggedStream"/>.</summary>
    public sealed class WrappingInterceptor : IHttpRequestInterceptor
    {
        private readonly string _tag;

        public WrappingInterceptor(string tag)
        {
            _tag = tag;
        }

        public TaggedStream? Created { get; private set; }

        public Stream AfterRequestBody(HttpRequestInterceptorContext context, Stream body)
        {
            Created = new TaggedStream(body, _tag);
            return Created;
        }
    }

    /// <summary>A disposable feature that counts the times it is disposed.</summary>
    public sealed class DisposableTestFeature : IHttpFeature, IDisposable
    {
        public string Name => "Cohesion.Tests.DisposableInterceptorFeature";

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    /// <summary>Head hook that attaches a <see cref="DisposableTestFeature"/>.</summary>
    public sealed class DisposableFeatureAttachingInterceptor : IHttpRequestInterceptor
    {
        public DisposableTestFeature? Feature { get; private set; }

        public void AfterRequestHead(HttpRequestInterceptorContext context)
        {
            Feature = new DisposableTestFeature();
            context.Features.Set(Feature);
        }
    }

    /// <summary>Head hook that rejects the request with a fixed status.</summary>
    public sealed class HeadRejectingInterceptor : IHttpRequestInterceptor
    {
        private readonly HttpStatusCode _statusCode;

        public HeadRejectingInterceptor(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public void AfterRequestHead(HttpRequestInterceptorContext context)
        {
            throw new HttpRequestRejectedException(_statusCode);
        }
    }

    /// <summary>Body hook that rejects the request with a fixed status.</summary>
    public sealed class BodyRejectingInterceptor : IHttpRequestInterceptor
    {
        private readonly HttpStatusCode _statusCode;

        public BodyRejectingInterceptor(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public Stream AfterRequestBody(HttpRequestInterceptorContext context, Stream body)
        {
            throw new HttpRequestRejectedException(_statusCode);
        }
    }

    /// <summary>Head hook that captures the parse context and its writability at hook time.</summary>
    public sealed class ContextCapturingInterceptor : IHttpRequestInterceptor
    {
        public HttpRequestInterceptorContext? Captured { get; private set; }

        public bool WasWritableDuringHeadHook { get; private set; }

        public void AfterRequestHead(HttpRequestInterceptorContext context)
        {
            Captured = context;
            WasWritableDuringHeadHook = !context.IsMaxRequestBodySizeReadOnly;
        }
    }

    /// <summary>Head hook that probes the read-only header view.</summary>
    public sealed class HeaderProbingInterceptor : IHttpRequestInterceptor
    {
        public bool HeadersWereReadOnly { get; private set; }

        public bool MutationThrew { get; private set; }

        public string? ObservedContentType { get; private set; }

        public void AfterRequestHead(HttpRequestInterceptorContext context)
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

    /// <summary>Counts head- and body-hook invocations.</summary>
    public sealed class InvocationRecordingInterceptor : IHttpRequestInterceptor
    {
        public int HeadInvocations { get; private set; }

        public int BodyInvocations { get; private set; }

        public void AfterRequestHead(HttpRequestInterceptorContext context)
        {
            HeadInvocations++;
        }

        public Stream AfterRequestBody(HttpRequestInterceptorContext context, Stream body)
        {
            BodyInvocations++;
            return body;
        }
    }

    /// <summary>A pass-through stream wrapper that records its tag, inner stream, and disposal.</summary>
    public sealed class TaggedStream : Stream
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
