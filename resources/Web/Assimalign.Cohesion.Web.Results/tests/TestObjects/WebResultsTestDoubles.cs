using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Results.Tests.TestObjects;

/// <summary>
/// A concrete <see cref="IHttpContext"/> double with a real feature collection, a seekable response
/// body (so header/body assertions behave as they would over a buffered transport), and a settable
/// <see cref="RequestCancelled"/> token for cancellation tests.
/// </summary>
internal sealed class TestHttpContext : IHttpContext
{
    public TestHttpContext(HttpMethod? method = null, HttpPath? path = null)
    {
        Request = new TestHttpRequest(this, method ?? HttpMethod.Get, path ?? HttpPath.Root);
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

    /// <summary>Reads the response body as a UTF-8 string, independent of stream position.</summary>
    public string ResponseBodyText() => ((TestHttpResponse)Response).BodyText();
}

internal sealed class TestHttpRequest : IHttpRequest
{
    public TestHttpRequest(IHttpContext context, HttpMethod method, HttpPath path)
    {
        HttpContext = context;
        Method = method;
        Path = path;
    }

    public HttpHost Host => HttpHost.Empty;
    public HttpPath Path { get; }
    public HttpMethod Method { get; }
    public HttpScheme Scheme => HttpScheme.Http;
    public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body { get; } = Stream.Null;
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
/// A recording <see cref="IHttpResponseStreamingFeature"/> double: accumulates written bytes,
/// counts flushes and completions, and snapshots the response's <c>Content-Type</c> and
/// <c>Content-Length</c> headers at the moment of the first write — the point where a real
/// transport would commit and lock the response head.
/// </summary>
internal sealed class TestStreamingFeature : IHttpResponseStreamingFeature
{
    private readonly IHttpResponse _response;
    private readonly MemoryStream _written = new();

    public TestStreamingFeature(IHttpResponse response) => _response = response;

    public string Name => "test-response-streaming";

    public bool HasStarted { get; private set; }
    public int FlushCount { get; private set; }
    public int CompleteCount { get; private set; }
    public string? ContentTypeAtFirstWrite { get; private set; }
    public bool ContentLengthPresentAtFirstWrite { get; private set; }

    public string WrittenText => Encoding.UTF8.GetString(_written.ToArray());

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        Start();
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (CompleteCount > 0)
        {
            throw new InvalidOperationException("The response has already been completed.");
        }

        Start();
        _written.Write(data.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        Start();
        FlushCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        Start();
        CompleteCount++;
        return ValueTask.CompletedTask;
    }

    private void Start()
    {
        if (HasStarted)
        {
            return;
        }

        HasStarted = true;
        ContentTypeAtFirstWrite = _response.Headers.ContainsKey(HttpHeaderKey.ContentType)
            ? _response.Headers[HttpHeaderKey.ContentType].Value
            : null;
        ContentLengthPresentAtFirstWrite = _response.Headers.ContainsKey(HttpHeaderKey.ContentLength);
    }
}
