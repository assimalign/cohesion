using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>AuthnStatement</c> (SAML Core §2.7.2): a statement that the
/// assertion subject was authenticated at a particular time and context. It is a standalone
/// sealed type (there is no shared statement base — the statement kinds share no members).
/// </summary>
public sealed class SamlAuthnStatement
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlAuthnStatement" /> class.
    /// </summary>
    /// <param name="authnContext">The authentication context (required by SAML Core).</param>
    /// <param name="authnInstant">The instant the subject authenticated.</param>
    /// <param name="sessionIndex">The provider session index.</param>
    /// <param name="sessionNotOnOrAfter">The instant at or after which the session must be considered ended.</param>
    /// <param name="subjectLocalityAddress">The network address the subject authenticated from.</param>
    /// <param name="subjectLocalityDnsName">The DNS name the subject authenticated from.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="authnContext" /> is null.</exception>
    public SamlAuthnStatement(
        SamlAuthnContext authnContext,
        DateTimeOffset? authnInstant = null,
        string? sessionIndex = null,
        DateTimeOffset? sessionNotOnOrAfter = null,
        string? subjectLocalityAddress = null,
        string? subjectLocalityDnsName = null)
    {
        ArgumentNullException.ThrowIfNull(authnContext);

        AuthnContext = authnContext;
        AuthnInstant = authnInstant;
        SessionIndex = sessionIndex;
        SessionNotOnOrAfter = sessionNotOnOrAfter;
        SubjectLocalityAddress = subjectLocalityAddress;
        SubjectLocalityDnsName = subjectLocalityDnsName;
    }

    /// <summary>
    /// Gets the authentication context.
    /// </summary>
    public SamlAuthnContext AuthnContext { get; }

    /// <summary>
    /// Gets the instant the subject authenticated.
    /// </summary>
    public DateTimeOffset? AuthnInstant { get; }

    /// <summary>
    /// Gets the provider session index, correlated into an authentication session's
    /// provider session identifiers.
    /// </summary>
    public string? SessionIndex { get; }

    /// <summary>
    /// Gets the instant at or after which the session must be considered ended.
    /// </summary>
    public DateTimeOffset? SessionNotOnOrAfter { get; }

    /// <summary>
    /// Gets the network address the subject authenticated from.
    /// </summary>
    public string? SubjectLocalityAddress { get; }

    /// <summary>
    /// Gets the DNS name the subject authenticated from.
    /// </summary>
    public string? SubjectLocalityDnsName { get; }
}
