using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents the request and response state for a single HTTP exchange.
/// </summary>
public interface IHttpContext : IAsyncDisposable
{
    /// <summary>
    /// Gets the HTTP version for the current exchange.
    /// </summary>
    HttpVersion Version { get; }

    /// <summary>
    /// Gets the session associated with the current exchange.
    /// </summary>
    IHttpSession Session { get; }

    /// <summary>
    /// Gets the current request.
    /// </summary>
    IHttpRequest Request { get; }

    /// <summary>
    /// Gets the current response.
    /// </summary>
    IHttpResponse Response { get; }

    /// <summary>
    /// Gets the transport connection metadata for the current exchange.
    /// </summary>
    IHttpConnectionInfo ConnectionInfo { get; }

    /// <summary>
    /// Gets a bag of items shared for the lifetime of the exchange.
    /// </summary>
    IDictionary<string, object?> Items { get; }

    /// <summary>
    /// Gets the cancellation token that signals when the request has been aborted.
    /// </summary>
    CancellationToken RequestAborted { get; }
}
