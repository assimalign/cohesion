namespace Assimalign.Cohesion.Web.Hosting.Internal;

using Assimalign.Cohesion.Http.Connections;

/// <summary>
/// The construction options for the default <see cref="WebApplicationServer"/>.
/// </summary>
internal sealed class WebApplicationServerOptions
{
    /// <summary>
    /// Gets or sets the middleware pipeline every accepted exchange is dispatched through.
    /// </summary>
    public IWebApplicationPipeline? Pipeline { get; set; }

    /// <summary>
    /// Gets or sets the listener the accept loop draws connections from.
    /// </summary>
    public IHttpConnectionListener? Listener { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of connections served concurrently. <see langword="null"/>
    /// (the default) means unlimited; a positive value gates the accept loop so additional
    /// connections are not opened until an active connection completes.
    /// </summary>
    public int? MaxConcurrentConnections { get; set; }
}
