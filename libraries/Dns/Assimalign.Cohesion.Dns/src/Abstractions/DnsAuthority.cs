using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// An authoritative DNS server &#8211; owns one or more <see cref="DnsZone"/> instances and
/// answers queries directly out of those zones rather than delegating to upstream resolvers.
/// </summary>
/// <remarks>
/// <para>
/// PR 1 ships the minimal shape so the contract layer compiles. The in-process authoritative
/// server, zone-file loader, AXFR/IXFR transfers, and dynamic UPDATE handling all live in
/// later PRs under Feature 07.
/// </para>
/// <para>
/// The shape intentionally exposes only zone enumeration and lookup. The query-answering
/// surface is supplied by <see cref="DnsClient"/>, which concrete authority types also
/// inherit from. Splitting the two abstract classes lets callers distinguish "ask the
/// authority directly" from "ask via recursive resolution".
/// </para>
/// </remarks>
public abstract class DnsAuthority : IDisposable, IAsyncDisposable
{
    private bool _disposed;

    /// <summary>
    /// The zones served by this authority. The collection reflects the authority's current
    /// state &#8211; live-add/remove APIs land alongside Feature 07.
    /// </summary>
    public abstract IReadOnlyCollection<DnsZone> Zones { get; }

    /// <summary>
    /// Returns the zone whose <see cref="DnsZone.Origin"/> is the longest match for
    /// <paramref name="name"/>, or <see langword="null"/> if no zone owns it.
    /// </summary>
    public abstract DnsZone? FindZone(DnsName name);

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
    /// Override to release synchronous resources held by the authority.
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
    /// Throws <see cref="ObjectDisposedException"/> when the authority has already been disposed.
    /// </summary>
    protected void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
