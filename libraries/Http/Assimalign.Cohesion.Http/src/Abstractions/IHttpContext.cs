using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents the request and response state for a single HTTP exchange.
/// </summary>
/// <remarks>
/// <para>
/// The protocol core deliberately exposes only wire-level concerns &#8211;
/// <see cref="Request"/>, <see cref="Response"/>, <see cref="ConnectionInfo"/>,
/// <see cref="Features"/>, <see cref="Items"/>. Application-layer concepts such
/// as sessions, authenticated principals, and parsed form bodies layer on top
/// via extension members in dedicated packages
/// (<c>Assimalign.Cohesion.Http.Sessions</c>, <c>Assimalign.Cohesion.Http.Forms</c>,
/// <c>Assimalign.Cohesion.Web.Authentication</c>, &#8230;) that attach
/// strongly-typed features to <see cref="Features"/>. <see cref="Items"/>
/// remains available for genuinely loose, name-keyed ad-hoc bookkeeping that
/// doesn't warrant a full feature contract.
/// </para>
/// </remarks>
public interface IHttpContext : IAsyncDisposable
{
    /// <summary>
    /// Gets the HTTP version for the current exchange.
    /// </summary>
    HttpVersion Version { get; }

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
    /// Gets the per-exchange feature collection. Higher-layer packages
    /// (authentication, sessions, etc.) attach strongly-typed features
    /// here rather than relying on the loosely-typed <see cref="Items"/>
    /// dictionary.
    /// </summary>
    IHttpFeatureCollection Features { get; }

    /// <summary>
    /// Gets a bag of items shared for the lifetime of the exchange.
    /// Loosely-typed, name-keyed state &mdash; per-request bookkeeping that
    /// crosses middleware boundaries but doesn't warrant a full feature
    /// contract &mdash; attaches here. Strongly-typed, type-keyed state belongs
    /// in <see cref="Features"/> instead.
    /// </summary>
    IDictionary<string, object?> Items { get; }

    /// <summary>
    /// Gets the cancellation token that signals when the request has been cancelled.
    /// </summary>
    CancellationToken RequestCancelled { get; }

    /// <summary>
    /// Requests cancellation of the current exchange at the protocol layer
    /// without tearing down the underlying connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the application-/middleware-facing signal that says "abandon this
    /// one exchange." It trips <see cref="RequestCancelled"/> and lets the
    /// transport decide how to reject the exchange on the wire: HTTP/2 resets the
    /// stream (<c>RST_STREAM</c>), HTTP/3 resets the request stream, and a
    /// multiplexed connection stays alive for its other streams. A non-multiplexed
    /// transport may have no finer-grained option than ending the exchange.
    /// </para>
    /// <para>
    /// Implemented as a default interface member that does nothing, so existing
    /// <see cref="IHttpContext"/> implementations are unaffected; transports that
    /// can cancel a single exchange override it.
    /// </para>
    /// </remarks>
    void Cancel();

    /// <summary>
    /// Asynchronously requests cancellation of the current exchange at the protocol layer
    /// without tearing down the underlying connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the application-/middleware-facing signal that says "abandon this
    /// one exchange." It trips <see cref="RequestCancelled"/> and lets the
    /// transport decide how to reject the exchange on the wire: HTTP/2 resets the
    /// stream (<c>RST_STREAM</c>), HTTP/3 resets the request stream, and a
    /// multiplexed connection stays alive for its other streams. A non-multiplexed
    /// transport may have no finer-grained option than ending the exchange.
    /// </para>
    /// <para>
    /// Implemented as a default interface member that does nothing, so existing
    /// <see cref="IHttpContext"/> implementations are unaffected; transports that
    /// can cancel a single exchange override it.
    /// </para>
    /// </remarks>
    Task CancelAsync();
}
