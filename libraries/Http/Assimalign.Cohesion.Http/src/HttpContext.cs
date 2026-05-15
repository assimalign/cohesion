using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a base abstraction for concrete HTTP context implementations.
/// </summary>
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

    /// <inheritdoc />
    public abstract IHttpConnectionInfo ConnectionInfo { get; }

    /// <inheritdoc />
    public abstract IDictionary<string, object?> Items { get; }

    /// <inheritdoc />
    public abstract CancellationToken RequestAborted { get; }

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    IHttpRequest IHttpContext.Request => Request;

    IHttpResponse IHttpContext.Response => Response;
}
