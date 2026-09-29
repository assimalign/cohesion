using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes one TLS leaf a gateway asks an <see cref="IResourceCertificateAuthority"/> to issue
/// for a resource endpoint that declares a certificate mount without a source.
/// </summary>
/// <param name="Application">The application that owns the resource.</param>
/// <param name="Resource">The resource serving the endpoint.</param>
/// <param name="Endpoint">The endpoint name.</param>
/// <param name="LeafName">The stable leaf identity, <c>&lt;resource&gt;-&lt;endpoint&gt;</c>.</param>
/// <param name="SubjectAlternativeNames">
/// The host names and addresses the leaf must cover, as observed or planned for the endpoint.
/// </param>
public sealed record ResourceCertificateRequest(
    ApplicationName Application,
    ResourceName Resource,
    string Endpoint,
    string LeafName,
    IReadOnlyList<string> SubjectAlternativeNames);
