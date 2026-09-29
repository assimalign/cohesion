using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Binds a gateway-side provider to the model resource whose control plane backs it, or to no
/// resource when the provider lives outside the application model.
/// </summary>
/// <typeparam name="T">
/// The provider seam, such as <see cref="IResourceCertificateAuthority"/> or
/// <see cref="ITrustedIssuerStore"/>.
/// </typeparam>
/// <param name="Resource">
/// The model resource that backs the provider, or <see langword="null"/> when the provider lives
/// outside the model and authenticates itself. For a bound resource the gateway waits until it
/// is Running and hands the provider a <see cref="ResourceProviderConnection"/> to it.
/// </param>
/// <param name="Provider">The provider implementation.</param>
/// <exception cref="ArgumentNullException"><paramref name="Provider"/> is <see langword="null"/>.</exception>
/// <remarks>
/// A bound resource must belong to the declaring application and, when the provider declares a
/// resource kind, have that manifest kind. Cross-application providers are not supported yet.
/// </remarks>
public sealed record ResourceProviderBinding<T>(ResourceName? Resource, T Provider)
    where T : class
{
    /// <summary>
    /// Gets the provider implementation.
    /// </summary>
    public T Provider { get; init; } = Provider ?? throw new ArgumentNullException(nameof(Provider));
}
