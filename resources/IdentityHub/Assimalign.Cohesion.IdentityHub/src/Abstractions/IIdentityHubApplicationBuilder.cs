using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// Defines the contract-only composition seam for an identity hub application.
/// </summary>
public interface IIdentityHubApplicationBuilder
{
    /// <summary>
    /// Declares an audience for access tokens issued by this identity hub.
    /// </summary>
    /// <param name="audience">The exact audience identifier.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="audience"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The audience is already declared.</exception>
    IIdentityHubApplicationBuilder AddAudience(string audience);

    /// <summary>
    /// Registers an OAuth client in the identity hub's code-first configuration.
    /// </summary>
    /// <param name="clientId">The exact client identifier.</param>
    /// <param name="configure">The callback that configures grants, credentials, and audiences.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="clientId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The client is already registered.</exception>
    IIdentityHubApplicationBuilder AddClient(
        string clientId,
        Action<IdentityHubClientOptions> configure);

    /// <summary>
    /// Builds the identity hub application.
    /// </summary>
    /// <returns>The configured identity hub application.</returns>
    IIdentityHubApplication Build();
}
