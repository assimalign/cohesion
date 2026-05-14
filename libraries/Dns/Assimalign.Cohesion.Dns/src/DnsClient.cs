using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Base class for every Cohesion DNS client. A <see cref="DnsClient"/> sends one query and
/// returns one response without taking a position on whether the answer came from a cache, a
/// recursive resolver, or an upstream authority &#8211; that distinction lives further down the
/// inheritance chain (<see cref="DnsResolver"/> adds the recursive walk; future authority
/// types may serve queries directly from a zone).
/// </summary>
/// <remarks>
/// <para>
/// DNS is a uniform protocol &#8211; every concrete client has the same shape (question in,
/// <see cref="DnsMessage"/> out, transport underneath). Modeling the surface as an
/// <see langword="abstract"/> class instead of an interface lets us share lifecycle plumbing
/// (<see cref="Dispose()"/> / <see cref="DisposeAsync"/> done once) and add cross-cutting
/// hooks without breaking implementers.
/// </para>
/// <para>
/// Implementations <strong>MUST</strong> honour <paramref name="cancellationToken"/> and
/// <strong>SHOULD</strong> raise <see cref="DnsException"/> with an explicit
/// <see cref="DnsErrorCode"/> on failure rather than surfacing OS-level socket exceptions
/// directly.
/// </para>
/// </remarks>
public abstract class DnsClient : IDisposable, IAsyncDisposable
{
    private bool _disposed;

    /// <summary>
    /// Sends <paramref name="question"/> and returns the responder's <see cref="DnsMessage"/>.
    /// </summary>
    /// <param name="question">The question to ask.</param>
    /// <param name="cancellationToken">Cancels the in-flight query. Implementations that
    /// maintain connection pools must release resources held for the cancelled query.</param>
    /// <exception cref="DnsException">The query failed. <see cref="DnsException.Code"/>
    /// indicates the category; non-success RCODEs surface here rather than as a successful
    /// <see cref="DnsMessage"/> with an error code.</exception>
    public abstract Task<DnsMessage> QueryAsync(
        DnsQuestion question,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True after <see cref="Dispose()"/> or <see cref="DisposeAsync"/> completes; further
    /// calls to <see cref="QueryAsync"/> SHOULD throw <see cref="ObjectDisposedException"/>.
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
    /// Override to release synchronous resources held by the client. Called at most once.
    /// Override <see cref="DisposeAsyncCore"/> in addition (or instead) when cleanup is
    /// genuinely async &#8211; the default implementation of <see cref="DisposeAsyncCore"/>
    /// falls back to <see cref="DisposeCore"/>.
    /// </summary>
    protected virtual void DisposeCore() { }

    /// <summary>
    /// Override to release resources asynchronously. The default implementation invokes
    /// <see cref="DisposeCore"/> and completes synchronously.
    /// </summary>
    protected virtual ValueTask DisposeAsyncCore()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> when the client has already been disposed.
    /// Use at the top of every public method on a derived client.
    /// </summary>
    protected void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
