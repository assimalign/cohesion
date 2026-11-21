using System;

using Assimalign.Cohesion.IdentityHub.Internal;
using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// One credential of an app registration: what the application proves its identity with. A
/// registration that holds at least one is a confidential client.
/// </summary>
/// <remarks>
/// <para>
/// There are three kinds, and no other: a secret
/// (<see cref="IdentityClientSecretCredential"/>), a certificate
/// (<see cref="IdentityClientCertificateCredential"/>), and a federated credential
/// (<see cref="IdentityClientFederatedCredential"/>). The family is closed because the hub has
/// to know how to verify, store, and report every kind it accepts.
/// </para>
/// <para>
/// <b>A credential never holds a secret in the clear.</b> A secret credential keeps a derived
/// verifier, a certificate credential keeps the public certificate, and a federated credential
/// keeps the names it trusts. <see cref="object.ToString"/> returns the identifier and the kind,
/// never the material.
/// </para>
/// <para>
/// A credential is immutable. Rotating one is adding a new credential and later removing the
/// old one, which is why a registration holds several.
/// </para>
/// </remarks>
public abstract class IdentityClientCredential
{
    private protected IdentityClientCredential(
        IdentityClientCredentialId id,
        string? displayName,
        DateTimeOffset? notBefore,
        DateTimeOffset? expiresAt)
    {
        if (id.Value is null)
        {
            throw new ArgumentException(ErrorMessages.CredentialIdRequired, nameof(id));
        }

        if (displayName is { Length: > 0 } &&
            !IdentityClientGrammar.IsText(displayName, IdentityClientGrammar.MaxDisplayNameLength, allowLineBreaks: false))
        {
            throw new ArgumentException(ErrorMessages.DisplayNameInvalid, nameof(displayName));
        }

        if (notBefore is not null && expiresAt is not null && expiresAt <= notBefore)
        {
            throw new ArgumentException(ErrorMessages.CredentialWindowInvalid, nameof(expiresAt));
        }

        Id = id;
        DisplayName = displayName is { Length: > 0 } ? displayName : null;
        NotBefore = notBefore?.ToUniversalTime();
        ExpiresAt = expiresAt?.ToUniversalTime();
    }

    /// <summary>
    /// Gets the credential's identifier, unique among the credentials of its registration.
    /// </summary>
    public IdentityClientCredentialId Id { get; }

    /// <summary>
    /// Gets the kind of credential, in the vocabulary of the shared identity model.
    /// </summary>
    public abstract IdentityCredentialKind Kind { get; }

    /// <summary>
    /// Gets the name a person is shown for the credential, when it has one.
    /// </summary>
    public string? DisplayName { get; }

    /// <summary>
    /// Gets the instant, in UTC, from which the credential is accepted, or <see langword="null"/>
    /// when it is accepted from the start.
    /// </summary>
    public DateTimeOffset? NotBefore { get; }

    /// <summary>
    /// Gets the instant, in UTC, from which the credential is no longer accepted, or
    /// <see langword="null"/> when it does not expire.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// Determines whether the credential is accepted at an instant: the instant is not before
    /// <see cref="NotBefore"/> and is before <see cref="ExpiresAt"/>.
    /// </summary>
    /// <param name="at">The instant to evaluate.</param>
    /// <returns><see langword="true"/> when the credential is accepted at <paramref name="at"/>.</returns>
    public bool IsUsable(DateTimeOffset at)
    {
        return (NotBefore is null || at >= NotBefore) && (ExpiresAt is null || at < ExpiresAt);
    }

    /// <summary>
    /// Returns the credential's identifier and kind. The credential's material is never part of
    /// the text.
    /// </summary>
    /// <returns>The identifier and the kind.</returns>
    public override string ToString() => $"{Id} ({Kind})";
}
