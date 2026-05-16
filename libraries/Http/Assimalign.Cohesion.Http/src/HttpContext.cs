using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a base abstraction for concrete HTTP context implementations.
/// </summary>
/// <remarks>
/// <para>
/// Reference-typed members (<see cref="Request"/>, <see cref="Response"/>,
/// <see cref="ConnectionInfo"/>, <see cref="Features"/>) are declared with
/// their concrete types so transport implementations and derived classes can
/// program against the concrete surface directly &#8211; without re-casting
/// from the interface at every call site. The corresponding members on
/// <see cref="IHttpContext"/> are implemented explicitly and delegate to these
/// properties so external consumers programming against the interface still
/// see only the interface contract.
/// </para>
/// </remarks>
public abstract class HttpContext : IHttpContext
{
    /// <inheritdoc />
    public abstract HttpVersion Version { get; }

    /// <summary>
    /// Gets the strongly typed request for the current exchange.
    /// </summary>
    public abstract HttpRequest Request { get; }

    /// <summary>
    /// Gets the strongly typed response for the current exchange.
    /// </summary>
    public abstract HttpResponse Response { get; }

    /// <summary>
    /// Gets the strongly typed transport connection metadata for the current exchange.
    /// </summary>
    public abstract HttpConnectionInfo ConnectionInfo { get; }

    /// <inheritdoc />
    public abstract HttpFeatureCollection Features { get; }

    /// <inheritdoc />
    public virtual IHttpProtocolUpgrade? Upgrade => null;
    
    /// <inheritdoc />
    public abstract IDictionary<string, object?> Items { get; }

    /// <inheritdoc />
    public abstract CancellationToken RequestAborted { get; }

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();


    IHttpRequest IHttpContext.Request => Request;
    IHttpResponse IHttpContext.Response => Response;
    IHttpFeatureCollection IHttpContext.Features => Features;
    IHttpConnectionInfo IHttpContext.ConnectionInfo => ConnectionInfo;
}
