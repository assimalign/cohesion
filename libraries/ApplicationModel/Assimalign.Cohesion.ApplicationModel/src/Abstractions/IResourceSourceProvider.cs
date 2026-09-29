using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Resolves <c>&lt;source&gt;:&lt;key&gt;</c> mount sources for a gateway. Registered in
/// <see cref="ApplicationProviders.Sources"/> under the <c>&lt;source&gt;</c> name.
/// </summary>
/// <remarks>
/// An implementation supplies only the mount kinds its source can serve; the remaining members
/// keep their default body, which throws <see cref="NotSupportedException"/>. A gateway reports
/// that failure as an unresolved mount input rather than stopping the reconcile pass.
/// </remarks>
public interface IResourceSourceProvider
{
    /// <summary>
    /// Gets the manifest kind a bound model resource must have (the store area's kind), or
    /// <see langword="null"/> when the provider does not require a model resource.
    /// </summary>
    /// <remarks>
    /// When the value is non-null, the source name must be a resource of the declaring
    /// application with this manifest kind; the gateway then passes a
    /// <see cref="ResourceProviderConnection"/> to it in <see cref="ResourceSourceRequest.Store"/>.
    /// </remarks>
    string? ResourceKind { get; }

    /// <summary>
    /// Reads the value of a <see cref="ResourceMountKind.Secret"/> mount.
    /// </summary>
    /// <param name="request">The mount being resolved.</param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>The secret bytes written to the mount.</returns>
    /// <exception cref="NotSupportedException">The provider does not supply secrets (default implementation).</exception>
    ValueTask<ReadOnlyMemory<byte>> ReadSecretAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"Source provider '{GetType().Name}' does not supply secret mount values.");

    /// <summary>
    /// Reads the certificate for a <see cref="ResourceMountKind.Secret"/> mount that an endpoint
    /// names as its certificate.
    /// </summary>
    /// <param name="request">The mount being resolved.</param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>The certificate bundle and the anchors that validate it.</returns>
    /// <exception cref="NotSupportedException">The provider does not supply certificates (default implementation).</exception>
    ValueTask<ResourceCertificate> ReadCertificateAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"Source provider '{GetType().Name}' does not supply certificate mount values.");

    /// <summary>
    /// Reads the entries of a <see cref="ResourceMountKind.Configuration"/> mount.
    /// </summary>
    /// <param name="request">The mount being resolved.</param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>
    /// The configuration entries. The gateway serializes them to the mount as a JSON object with
    /// ordinally sorted keys.
    /// </returns>
    /// <exception cref="NotSupportedException">The provider does not supply configuration (default implementation).</exception>
    ValueTask<IReadOnlyDictionary<string, string?>> ReadConfigurationAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"Source provider '{GetType().Name}' does not supply configuration mount values.");
}
