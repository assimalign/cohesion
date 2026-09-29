using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Supplies the application-scoped issuer keys used by gateway export endpoints and importers.
/// </summary>
public interface ITrustedIssuerProvider
{
    /// <summary>Gets an immutable snapshot of trusted issuers for an initialized application.</summary>
    /// <param name="application">The application whose trust set is requested.</param>
    /// <returns>The trusted issuer snapshot.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// Trust has not been initialized for <paramref name="application"/>.
    /// </exception>
    IReadOnlyList<TrustedIssuer> GetTrustedIssuers(ApplicationName application);
}
