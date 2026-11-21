using System;
using System.Collections.Generic;

using Assimalign.Cohesion.IdentityHub.Internal;
using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// A federated credential of an app registration: the statement that a token another issuer
/// gave a workload stands for this application, so the workload holds no secret of the hub's.
/// </summary>
/// <remarks>
/// <para>
/// The credential names what such a token must say: its issuer, its subject, and an audience
/// it was issued for. A build pipeline or a cluster workload presents the token its own
/// platform issued, and the hub accepts it in place of a secret when all three match exactly.
/// </para>
/// <para>
/// Nothing in the credential is secret. The issuer is an <c>https</c> URL with no query or
/// fragment, as OpenID Connect Discovery defines an issuer identifier. Issuer, subject, and
/// audiences compare ordinally and are never patterns.
/// </para>
/// </remarks>
public sealed class IdentityClientFederatedCredential : IdentityClientCredential
{
    /// <summary>
    /// The most audiences a federated credential accepts.
    /// </summary>
    public const int MaxAudienceCount = 8;

    private IdentityClientFederatedCredential(
        IdentityClientCredentialId id,
        string? displayName,
        DateTimeOffset? notBefore,
        DateTimeOffset? expiresAt,
        string issuer,
        string subject,
        IReadOnlyList<IdentityClientAudience> audiences)
        : base(id, displayName, notBefore, expiresAt)
    {
        Issuer = issuer;
        Subject = subject;
        Audiences = audiences;
    }

    /// <inheritdoc />
    public override IdentityCredentialKind Kind => IdentityCredentialKind.FederatedToken;

    /// <summary>
    /// Gets the issuer whose tokens the credential accepts, exactly as the token's <c>iss</c>
    /// claim names it.
    /// </summary>
    public string Issuer { get; }

    /// <summary>
    /// Gets the subject the credential accepts, exactly as the token's <c>sub</c> claim names it.
    /// </summary>
    public string Subject { get; }

    /// <summary>
    /// Gets the audiences the credential accepts: the token's <c>aud</c> claim holds at least
    /// one of them.
    /// </summary>
    public IReadOnlyList<IdentityClientAudience> Audiences { get; }

    /// <summary>
    /// Creates a federated credential.
    /// </summary>
    /// <param name="id">The credential's identifier.</param>
    /// <param name="issuer">The issuer: an <c>https</c> URL of at most 512 characters with no query or fragment.</param>
    /// <param name="subject">The subject: 1 to 512 characters with no control character.</param>
    /// <param name="audiences">The audiences the credential accepts: 1 to <see cref="MaxAudienceCount"/>, each once.</param>
    /// <param name="displayName">The name a person is shown for the credential, or <see langword="null"/> for none.</param>
    /// <param name="notBefore">The instant from which the credential is accepted, or <see langword="null"/> for from the start.</param>
    /// <param name="expiresAt">The instant from which the credential is no longer accepted, or <see langword="null"/> for never.</param>
    /// <returns>The credential.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="issuer"/>, <paramref name="subject"/>, or <paramref name="audiences"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is <see langword="default"/>; <paramref name="issuer"/>,
    /// <paramref name="subject"/>, or <paramref name="displayName"/> breaks its rule;
    /// <paramref name="audiences"/> is empty, too long, or holds a <see langword="default"/> or
    /// repeated audience; or <paramref name="expiresAt"/> is not after <paramref name="notBefore"/>.
    /// </exception>
    public static IdentityClientFederatedCredential Create(
        IdentityClientCredentialId id,
        string issuer,
        string subject,
        IEnumerable<IdentityClientAudience> audiences,
        string? displayName = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(audiences);

        if (!IdentityClientGrammar.IsIssuer(issuer))
        {
            throw new ArgumentException(ErrorMessages.CredentialIssuerInvalid, nameof(issuer));
        }

        if (!IdentityClientGrammar.IsText(subject, IdentityClientGrammar.MaxSubjectLength, allowLineBreaks: false))
        {
            throw new ArgumentException(ErrorMessages.CredentialSubjectInvalid, nameof(subject));
        }

        var accepted = new List<IdentityClientAudience>();
        foreach (IdentityClientAudience audience in audiences)
        {
            if (audience.Value is null || accepted.Count == MaxAudienceCount || accepted.Contains(audience))
            {
                throw new ArgumentException(ErrorMessages.CredentialAudiencesInvalid, nameof(audiences));
            }

            accepted.Add(audience);
        }

        if (accepted.Count is 0)
        {
            throw new ArgumentException(ErrorMessages.CredentialAudiencesInvalid, nameof(audiences));
        }

        return new IdentityClientFederatedCredential(
            id,
            displayName,
            notBefore,
            expiresAt,
            issuer,
            subject,
            Array.AsReadOnly(accepted.ToArray()));
    }
}
