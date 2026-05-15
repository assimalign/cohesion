using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents the request and response state for a single HTTP exchange.
/// </summary>
/// <remarks>
/// <para>
/// The protocol core deliberately exposes only wire-level concerns &#8211;
/// <see cref="Request"/>, <see cref="Response"/>, <see cref="ConnectionInfo"/>,
/// <see cref="Items"/>. Application-layer concepts such as sessions, authenticated
/// principals, or parsed form bodies layer on top via extension methods in dedicated
/// packages (<c>Assimalign.Cohesion.Http.Sessions</c>,
/// <c>Assimalign.Cohesion.Http.Forms</c>, &#8230;) that read and write through
/// <see cref="Items"/>.
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
    /// Gets a bag of items shared for the lifetime of the exchange. Higher-layer features
    /// (sessions, identity, parsed forms) attach state here via extension methods.
    /// </summary>
    IDictionary<string, object?> Items { get; }

    /// <summary>
    /// Gets the cancellation token that signals when the request has been aborted.
    /// </summary>
    CancellationToken RequestAborted { get; }

    /// <summary>
    /// Gets the protocol-upgrade feature for this exchange, or <see langword="null"/>
    /// when the exchange is not a candidate for a connection transition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A non-null value indicates that the request matches either the RFC 9110 §7.8
    /// upgrade signal (<c>Connection: upgrade</c> + <c>Upgrade</c>) or the
    /// RFC 9110 §9.3.6 <c>CONNECT</c> tunnel shape. Inspect
    /// <see cref="IHttpProtocolUpgrade.Kind"/> to disambiguate.
    /// </para>
    /// <para>
    /// Most exchanges are normal request/response and this property returns
    /// <see langword="null"/>.
    /// </para>
    /// </remarks>
    IHttpProtocolUpgrade? Upgrade { get; }
}
