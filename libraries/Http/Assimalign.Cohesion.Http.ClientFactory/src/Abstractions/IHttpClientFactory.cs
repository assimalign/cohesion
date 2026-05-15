using System;
using System.Net.Http;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Hands out lifecycle-managed <see cref="HttpClient"/> instances keyed by name.
/// Implementations pool and rotate the underlying
/// <see cref="HttpMessageHandler"/>s so callers can freely <c>using</c>-dispose the
/// returned clients without exhausting ephemeral ports.
/// </summary>
/// <remarks>
/// Implementations own the pooled handlers and must release them through
/// <see cref="IDisposable.Dispose"/> or <see cref="IAsyncDisposable.DisposeAsync"/> at
/// shutdown. Calling <see cref="Create"/> after disposal must throw
/// <see cref="ObjectDisposedException"/>.
/// </remarks>
public interface IHttpClientFactory : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Returns a fresh <see cref="HttpClient"/> for the named registration. Within the
    /// active handler-lifetime window every call for the same <paramref name="name"/>
    /// returns clients backed by the same underlying handler (and connection pool); after
    /// the window the implementation rotates the handler.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">No client is registered with
    /// <paramref name="name"/>.</exception>
    /// <exception cref="ObjectDisposedException">The factory has been disposed.</exception>
    HttpClient Create(string name);
}
