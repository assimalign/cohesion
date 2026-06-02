using System;
using System.Threading;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpRequestLifetime"/> implementation backed by a
/// <see cref="CancellationTokenSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequestAborted"/> defaults to the internal source's token, and
/// <see cref="Abort"/> cancels that source &#8212; so by default an observer of
/// <see cref="RequestAborted"/> sees the cancellation. The token is settable
/// per the interface: replacing it (for example to substitute a token linked
/// to an external source) means the caller takes responsibility for that
/// token's cancellation; <see cref="Abort"/> still cancels the internal
/// source.
/// </para>
/// <para>
/// <see cref="Abort"/> is idempotent and safe to call after
/// <see cref="Dispose"/>. Dispose releases the underlying
/// <see cref="CancellationTokenSource"/>.
/// </para>
/// </remarks>
public sealed class HttpRequestLifetime : IHttpRequestLifetime, IDisposable
{
    private readonly CancellationTokenSource _abortedSource;
    private CancellationToken _requestAborted;
    private int _disposed;

    /// <summary>
    /// Initializes a new request lifetime with a fresh cancellation source.
    /// </summary>
    public HttpRequestLifetime()
    {
        _abortedSource = new CancellationTokenSource();
        _requestAborted = _abortedSource.Token;
    }

    /// <inheritdoc />
    public CancellationToken RequestAborted
    {
        get => _requestAborted;
        set => _requestAborted = value;
    }

    /// <inheritdoc />
    public void Abort()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            _abortedSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose — the request is already over, so the abort
            // is moot. Idempotent by contract.
        }
    }

    /// <summary>
    /// Releases the underlying cancellation source. After disposal
    /// <see cref="Abort"/> is a safe no-op.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _abortedSource.Dispose();
    }
}
