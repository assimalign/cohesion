using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

/// <summary>
/// A readable stream that cannot seek and reports no length — the shape of a network, pipe, or
/// decompression body — and counts its reads, so a test can prove a <c>HEAD</c> never touched it.
/// </summary>
internal sealed class NonSeekableReadStream : Stream
{
    private readonly MemoryStream _inner;

    public NonSeekableReadStream(string content)
    {
        _inner = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
    }

    public int ReadCount { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadCount++;
        return _inner.Read(buffer, offset, count);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ReadCount++;
        return _inner.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return _inner.ReadAsync(buffer, cancellationToken);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// An <see cref="IHttpContext"/> test double whose <see cref="RequestCancelled"/> token the test
/// controls, for the helpers' fall-back to the exchange's cancellation. It reuses the request and
/// response doubles of <see cref="TestHttpContext"/>.
/// </summary>
internal sealed class CancellableTestHttpContext : IHttpContext
{
    public CancellableTestHttpContext(string path, HttpMethod method, CancellationToken requestCancelled)
    {
        Request = new TestHttpRequest(this, path, method);
        Response = new TestHttpResponse(this);
        RequestCancelled = requestCancelled;
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public CancellationToken RequestCancelled { get; }
    public void Cancel() { }
    public Task CancelAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
