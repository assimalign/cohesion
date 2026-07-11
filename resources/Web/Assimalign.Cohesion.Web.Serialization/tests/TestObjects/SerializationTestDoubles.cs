using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Serialization.Tests.TestObjects;

/// <summary>
/// A concrete <see cref="IHttpContext"/> double with a real feature collection, settable request
/// headers/body, and a seekable response body, so registry lookups and typed body IO behave as
/// they would over a buffered transport.
/// </summary>
internal sealed class TestHttpContext : IHttpContext
{
    public TestHttpContext()
    {
        Request = new TestHttpRequest(this);
        Response = new TestHttpResponse(this);
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public CancellationToken RequestCancelled { get; set; } = CancellationToken.None;

    public void Cancel()
    {
    }

    public Task CancelAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Sets the request body to a UTF-8 payload and stamps its <c>Content-Type</c> header.</summary>
    public void SetRequestBody(string payload, string? contentType)
    {
        var request = (TestHttpRequest)Request;
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        if (contentType is not null)
        {
            request.Headers[HttpHeaderKey.ContentType] = contentType;
        }
    }

    /// <summary>Reads the response body as a UTF-8 string, independent of stream position.</summary>
    public string ResponseBodyText() => ((TestHttpResponse)Response).BodyText();
}

internal sealed class TestHttpRequest : IHttpRequest
{
    public TestHttpRequest(IHttpContext context) => HttpContext = context;

    public HttpHost Host => HttpHost.Empty;
    public HttpPath Path => HttpPath.Root;
    public HttpMethod Method => HttpMethod.Post;
    public HttpScheme Scheme => HttpScheme.Http;
    public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body { get; set; } = Stream.Null;
}

internal sealed class TestHttpResponse : IHttpResponse
{
    public TestHttpResponse(IHttpContext context) => HttpContext = context;

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body { get; set; } = new MemoryStream();

    public string BodyText()
    {
        if (Body is MemoryStream memory)
        {
            return Encoding.UTF8.GetString(memory.ToArray());
        }

        long position = Body.CanSeek ? Body.Position : 0;
        if (Body.CanSeek)
        {
            Body.Position = 0;
        }

        using var reader = new StreamReader(Body, Encoding.UTF8, leaveOpen: true);
        string text = reader.ReadToEnd();
        if (Body.CanSeek)
        {
            Body.Position = position;
        }

        return text;
    }
}

/// <summary>
/// An <see cref="IWebApplicationBuilder"/> double that records the features registered by
/// builder-time composition verbs.
/// </summary>
internal sealed class TestWebApplicationBuilder : IWebApplicationBuilder
{
    public List<IHttpFeature> Features { get; } = [];

    public IWebApplicationBuilder AddFeature(IHttpFeature feature)
    {
        Features.Add(feature);
        return this;
    }

    public IWebApplicationBuilder AddFeature(Func<IWebApplicationContext, IHttpFeature> configure) => this;

    public IWebApplicationBuilder AddServer(IWebApplicationServer server) => this;

    public IWebApplicationBuilder AddServer(Func<IWebApplicationContext, IWebApplicationServer> server) => this;

    public IWebApplicationBuilder AddPipeline(IWebApplicationPipeline pipeline) => this;

    public IWebApplication Build() => throw new NotSupportedException("The test builder does not build applications.");
}

/// <summary>
/// A registry entry double whose media types are supplied per test, for exercising the
/// specificity/registration-order matching rules without a real serializer.
/// </summary>
internal sealed class FakeContentReader : IHttpContentReader
{
    public FakeContentReader(params HttpMediaType[] mediaTypes) => MediaTypes = mediaTypes;

    public IReadOnlyList<HttpMediaType> MediaTypes { get; }

    public bool CanRead(Type type) => true;

    public ValueTask<object?> ReadAsync(IHttpRequest request, Type type, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<object?>(null);
}

/// <summary>
/// A writer double mirroring <see cref="FakeContentReader"/>; records the content type it was
/// asked to emit.
/// </summary>
internal sealed class FakeContentWriter : IHttpContentWriter
{
    public FakeContentWriter(params HttpMediaType[] mediaTypes) => MediaTypes = mediaTypes;

    public IReadOnlyList<HttpMediaType> MediaTypes { get; }

    public HttpMediaType? WrittenContentType { get; private set; }

    public bool CanWrite(Type type) => true;

    public Task WriteAsync(IHttpResponse response, object? value, Type type, HttpMediaType contentType, CancellationToken cancellationToken = default)
    {
        WrittenContentType = contentType;
        return Task.CompletedTask;
    }
}
