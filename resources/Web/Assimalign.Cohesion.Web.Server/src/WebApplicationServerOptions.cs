using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http.Connections;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// The construction options for the default <see cref="WebApplicationServer"/>.
/// </summary>
public sealed class WebApplicationServerOptions
{
    /// <summary>
    /// Gets or sets the middleware pipeline every accepted exchange is dispatched through.
    /// </summary>
    public IWebApplicationPipeline? Pipeline { get; set; }

    /// <summary>
    /// Gets or sets the listener options used to construct the server's HTTP connection listener.
    /// </summary>
    /// <remarks>
    /// Register transport listeners and application interceptors before constructing the server.
    /// The server installs its own client-fault and response-starting interceptors before these
    /// interceptors and adds its feature count to <see cref="HttpConnectionListenerOptions.ExchangeFeatureCapacity"/>.
    /// Construction snapshots these options; do not mutate them concurrently with construction.
    /// </remarks>
    public HttpConnectionListenerOptions Listener { get; set; } = new();

    /// <summary>
    /// Gets or sets the maximum number of connections served concurrently. <see langword="null"/>
    /// (the default) means unlimited; a positive value gates the accept loop so additional
    /// connections are not opened until an active connection completes.
    /// </summary>
    public int? MaxConcurrentConnections { get; set; }

    /// <summary>
    /// Gets the case-insensitive set of query parameter names whose values the server's request
    /// spans show in <c>url.query</c>. The empty default set redacts every value.
    /// </summary>
    /// <remarks>The server snapshots the set during construction.</remarks>
    public ISet<string> TelemetryQueryValues { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
