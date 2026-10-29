using System;
using System.Net.Http;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// Configures the manifest publisher that <see cref="ApplicationInstancePublisher.Create"/> creates.
/// </summary>
public sealed class ApplicationInstancePublisherOptions
{
    /// <summary>
    /// Gets or sets the absolute URL the gateway receives manifests on: <c>http</c> or <c>https</c>,
    /// or <see cref="ApplicationInstancePublisher.InProcessScheme"/> for a gateway in the instance's
    /// own process. Required.
    /// </summary>
    /// <remarks>
    /// A gateway that provisions the instance names it in the
    /// <see cref="AppEnvironment.Variables.ManifestEndpoint"/> variable.
    /// </remarks>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the callback that creates the HTTP client a publication sends its request with;
    /// <see langword="null"/> uses a client over a handler every publisher in the process shares.
    /// </summary>
    /// <remarks>
    /// The publisher calls it once per publication and disposes the client when the publication
    /// completes. Return a new client on each call whose disposal leaves its connection pool in place,
    /// such as one from an HTTP client factory, which pools and rotates handlers so publications do
    /// not exhaust sockets: <c>() =&gt; clients.Create("gateway")</c>.
    /// </remarks>
    public Func<HttpClient>? HttpClientFactory { get; set; }

    /// <summary>
    /// Gets or sets the content serializer the manifest is written with and the gateway's answer read
    /// with; <see langword="null"/> uses JSON (<see cref="JsonApplicationContentSerializer"/>).
    /// </summary>
    /// <remarks>
    /// Use the serializer of the control plane the publisher serves, so the snapshots the gateway
    /// answers with are values that control plane reads.
    /// </remarks>
    public IApplicationContentSerializer? Serializer { get; set; }
}
