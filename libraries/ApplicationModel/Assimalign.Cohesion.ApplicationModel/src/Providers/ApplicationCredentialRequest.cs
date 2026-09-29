using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes one credential a gateway asks an <see cref="IApplicationCredentialIssuer"/> to mint.
/// </summary>
/// <param name="Application">The application whose authority the credential carries.</param>
/// <param name="Gateway">The identity of the gateway minting the credential.</param>
/// <param name="Audience">The party that will verify the credential, such as a resource name.</param>
/// <param name="Subject">The party the credential identifies, such as the gateway or an emitting resource.</param>
/// <param name="Purpose">Why the credential is minted.</param>
/// <param name="Lifetime">The requested validity period.</param>
public sealed record ApplicationCredentialRequest(
    ApplicationName Application,
    ResourceName Gateway,
    string Audience,
    string Subject,
    ApplicationCredentialPurpose Purpose,
    TimeSpan Lifetime);
