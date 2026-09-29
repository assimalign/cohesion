using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Issues TLS leaves for resource endpoints that declare a certificate mount without a source.
/// Registered through <see cref="ApplicationProviders.CertificateAuthority"/>.
/// </summary>
/// <remarks>
/// Without a registered authority the gateway issues leaves from its development authority in
/// Local only and fails loudly in every other environment. The authority resource's own leaf is
/// always issued by the gateway, because it cannot issue its first certificate itself.
/// </remarks>
public interface IResourceCertificateAuthority
{
    /// <summary>
    /// Gets the manifest kind the bound resource must have, or <see langword="null"/> when the
    /// authority does not require a model resource.
    /// </summary>
    string? ResourceKind { get; }

    /// <summary>
    /// Issues one TLS leaf.
    /// </summary>
    /// <param name="request">The leaf to issue.</param>
    /// <param name="authority">
    /// The connection to the bound authority resource, or <see langword="null"/> when the binding
    /// names no resource.
    /// </param>
    /// <param name="cancellationToken">Signals that issuance should be abandoned.</param>
    /// <returns>The issued certificate bundle and the anchors that validate it.</returns>
    ValueTask<ResourceCertificate> IssueAsync(
        ResourceCertificateRequest request,
        ResourceProviderConnection? authority,
        CancellationToken cancellationToken = default);
}
