using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Results.ServerSentEvents.Tests.TestObjects;

/// <summary>
/// A concrete <see cref="IHttpContext"/> double with a real feature collection, mirroring the
/// Web.Results test double of the same shape.
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
}

internal sealed class TestHttpRequest : IHttpRequest
{
    public TestHttpRequest(IHttpContext context) => HttpContext = context;

    public HttpHost Host => HttpHost.Empty;
    public HttpPath Path => HttpPath.Root;
    public HttpMethod Method => HttpMethod.Get;
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
}

/// <summary>
/// A recording <see cref="IHttpResponseStreamingFeature"/> double: accumulates written bytes,
/// counts flushes and completions, and snapshots the response head headers at the first write —
/// the point where a real transport would commit and lock them.
/// </summary>
internal sealed class TestStreamingFeature : IHttpResponseStreamingFeature
{
    private readonly IHttpResponse _response;
    private readonly MemoryStream _written = new();
    private readonly List<int> _writeLengths = new();

    public TestStreamingFeature(IHttpResponse response) => _response = response;

    public string Name => "test-response-streaming";

    public bool HasStarted { get; private set; }
    public int FlushCount { get; private set; }
    public int CompleteCount { get; private set; }
    public string? ContentTypeAtFirstWrite { get; private set; }
    public string? CacheControlAtFirstWrite { get; private set; }
    public bool ContentLengthPresentAtFirstWrite { get; private set; }

    public string WrittenText => Encoding.UTF8.GetString(_written.ToArray());

    public IReadOnlyList<int> WriteLengths => _writeLengths;

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
        _writeLengths.Add(data.Length);
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
        CacheControlAtFirstWrite = _response.Headers.ContainsKey(HttpHeaderKey.CacheControl)
            ? _response.Headers[HttpHeaderKey.CacheControl].Value
            : null;
        ContentLengthPresentAtFirstWrite = _response.Headers.ContainsKey(HttpHeaderKey.ContentLength);
    }
}
