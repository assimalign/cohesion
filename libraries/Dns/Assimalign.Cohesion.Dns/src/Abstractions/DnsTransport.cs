using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Sends a serialized DNS message to a single upstream server and returns the response bytes.
/// Pluggable so resolvers can swap UDP / TCP / DoT / DoH / DoQ without changing higher-level
/// query logic.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="DnsTransport"/> binds to one <see cref="Endpoint"/>. Resolvers that need to
/// fail over between multiple upstreams hold a list of transports rather than a single
/// transport with N endpoints — this matches the
/// <c>Assimalign.Cohesion.Transports</c> family's <c>ClientTransport</c> shape and lets
/// stream-based subclasses (TCP, DoT, DoQ) pool connections against a fixed remote.
/// </para>
/// <para>
/// The transport works in terms of raw byte buffers; serialization and parsing live in the
/// wire-format layer of <c>Assimalign.Cohesion.Dns</c>. This split keeps the transport
/// implementation small and lets the resolver decide message-level concerns like EDNS
/// payload size or DNSSEC bits without involving the network layer.
/// </para>
/// <para>
/// Implementations <strong>MUST</strong> honour the cancellation token and translate
/// network-level failures into <see cref="DnsException"/> with
/// <see cref="DnsErrorCode.Transport"/> or <see cref="DnsErrorCode.Timeout"/>.
/// </para>
/// </remarks>
public abstract class DnsTransport : IDisposable, IAsyncDisposable
{
    private bool _disposed;

    /// <summary>
    /// The upstream server bound to this transport. Set at construction; immutable.
    /// </summary>
    public abstract EndPoint Endpoint { get; }

    /// <summary>
    /// Sends <paramref name="request"/> to <see cref="Endpoint"/> and returns the response
    /// payload. The returned <see cref="ReadOnlyMemory{Byte}"/> may share storage with a
    /// pooled buffer owned by the transport; callers <strong>MUST NOT</strong> retain it
    /// beyond the lifetime of the awaited task without copying.
    /// </summary>
    /// <param name="request">The serialized DNS request bytes (just the DNS message; no
    /// length-prefix for stream-based transports &#8211; the transport supplies framing).</param>
    /// <param name="cancellationToken">Cancels the in-flight exchange.</param>
    public abstract Task<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True after <see cref="Dispose()"/> or <see cref="DisposeAsync"/> completes.
    /// </summary>
    protected bool IsDisposed => _disposed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        var task = DisposeAsyncCore();
        GC.SuppressFinalize(this);
        return task;
    }

    /// <summary>
    /// Override to release synchronous resources held by the transport.
    /// </summary>
    protected virtual void DisposeCore() { }

    /// <summary>
    /// Override to release resources asynchronously. The default invokes
    /// <see cref="DisposeCore"/> and completes synchronously.
    /// </summary>
    protected virtual ValueTask DisposeAsyncCore()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> when the transport has already been disposed.
    /// </summary>
    protected void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
