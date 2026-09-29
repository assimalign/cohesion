namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Carries the outcome of verifying one presented resource credential.</summary>
/// <param name="Status">The verification outcome.</param>
/// <param name="Caller">
/// The mapped caller. Required for <see cref="ResourceCredentialStatus.Authorized"/>; optional for
/// <see cref="ResourceCredentialStatus.Forbidden"/>, when the credential was authentic; otherwise
/// <see langword="null"/>.
/// </param>
/// <param name="Failure">An optional diagnostic description of a refusal, never echoed to the caller.</param>
/// <remarks>
/// The <see langword="default"/> value has <see cref="ResourceCredentialStatus.NoResult"/> status, so a
/// verifier that does not recognize a credential can return <see langword="default"/>.
/// </remarks>
public readonly record struct ResourceCredentialVerification(
    ResourceCredentialStatus Status,
    ResourceCaller? Caller,
    string? Failure);
