using System;

namespace Assimalign.Cohesion.Web.Testing;

/// <summary>
/// Options controlling how a <see cref="WebApplicationTestFactory"/> composes and serves the
/// application under test.
/// </summary>
public sealed class WebApplicationTestFactoryOptions
{
    /// <summary>
    /// Gets or sets the HTTP protocol served over the in-memory transport. Defaults to
    /// <see cref="WebApplicationTestProtocol.Http1"/>.
    /// </summary>
    public WebApplicationTestProtocol Protocol { get; set; } = WebApplicationTestProtocol.Http1;

    /// <summary>
    /// Gets or sets the base address stamped on clients the factory creates. Defaults to
    /// <c>http://localhost/</c>.
    /// </summary>
    /// <remarks>
    /// The authority is nominal: it shapes the request URI (and therefore the <c>Host</c>
    /// header the application observes) but is never resolved — every connection dials the
    /// factory's in-memory listener regardless of host or port.
    /// </remarks>
    public Uri BaseAddress { get; set; } = new Uri("http://localhost/");
}
