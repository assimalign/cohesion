using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents the immutable outcome of an authentication attempt: either an authenticated
/// subject with its context and provenance, or a failure. Every result carries the protocol
/// and asserting party that produced it, so downstream services and auditors never lose
/// where an authentication came from.
/// </summary>
public sealed class AuthenticationResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationResult" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The result contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a property name is blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor does not set exactly one of <c>Subject</c> and
    /// <c>Failure</c>, or has no completion instant.
    /// </exception>
    public AuthenticationResult(AuthenticationResultDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.Subject is null == descriptor.Failure is null)
        {
            throw new IdentityModelException(
                "An authentication result requires exactly one of a subject (success) or a failure.");
        }

        if (descriptor.CompletedAt is null)
        {
            throw new IdentityModelException(
                "An authentication result requires a completion instant.");
        }

        Subject = descriptor.Subject;
        Failure = descriptor.Failure;
        AttemptedSubject = descriptor.AttemptedSubject;
        Protocol = descriptor.Protocol;
        CompletedAt = descriptor.CompletedAt.Value;
        Issuer = descriptor.Issuer;
        Audience = descriptor.Audience;
        CredentialId = descriptor.CredentialId;
        EvidenceId = descriptor.EvidenceId;
        Context = descriptor.Context;
        Properties = ModelSnapshot.Properties(descriptor.Properties, nameof(descriptor));
    }

    /// <summary>
    /// Creates a successful result with the minimum required members.
    /// </summary>
    /// <param name="subject">The authenticated subject.</param>
    /// <param name="protocol">The protocol that produced the result.</param>
    /// <param name="completedAt">The instant the authentication completed.</param>
    /// <returns>A successful authentication result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="subject" /> is null.</exception>
    public static AuthenticationResult Success(
        IIdentitySubject subject,
        AuthenticationProtocol protocol,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(subject);

        return new AuthenticationResult(new AuthenticationResultDescriptor
        {
            Subject = subject,
            Protocol = protocol,
            CompletedAt = completedAt,
        });
    }

    /// <summary>
    /// Creates a failed result with the minimum required members.
    /// </summary>
    /// <param name="failure">The failure.</param>
    /// <param name="protocol">The protocol that produced the result.</param>
    /// <param name="completedAt">The instant the authentication attempt completed.</param>
    /// <returns>A failed authentication result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="failure" /> is null.</exception>
    public static AuthenticationResult Failed(
        AuthenticationFailure failure,
        AuthenticationProtocol protocol,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new AuthenticationResult(new AuthenticationResultDescriptor
        {
            Failure = failure,
            Protocol = protocol,
            CompletedAt = completedAt,
        });
    }

    /// <summary>
    /// Gets a value indicating whether the authentication succeeded. When
    /// <see langword="true" />, <see cref="Subject" /> is non-null; when
    /// <see langword="false" />, <see cref="Failure" /> is non-null.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Subject))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool Succeeded => Subject is not null;

    /// <summary>
    /// Gets the authenticated subject. Non-null exactly when <see cref="Succeeded" /> is
    /// <see langword="true" />.
    /// </summary>
    public IIdentitySubject? Subject { get; }

    /// <summary>
    /// Gets the failure. Non-null exactly when <see cref="Succeeded" /> is
    /// <see langword="false" />.
    /// </summary>
    public AuthenticationFailure? Failure { get; }

    /// <summary>
    /// Gets the identifier the failed attempt claimed to authenticate as, when known.
    /// </summary>
    public SubjectIdentifier? AttemptedSubject { get; }

    /// <summary>
    /// Gets the protocol that produced the result.
    /// </summary>
    public AuthenticationProtocol Protocol { get; }

    /// <summary>
    /// Gets the instant the authentication attempt completed.
    /// </summary>
    public DateTimeOffset CompletedAt { get; }

    /// <summary>
    /// Gets the asserting party (OIDC issuer / SAML IdP entity ID).
    /// </summary>
    public string? Issuer { get; }

    /// <summary>
    /// Gets the party the authentication was performed for (OIDC audience or client / SAML
    /// audience restriction).
    /// </summary>
    public string? Audience { get; }

    /// <summary>
    /// Gets the identifier of the credential used, when known.
    /// </summary>
    public string? CredentialId { get; }

    /// <summary>
    /// Gets the identifier of the evidencing token or assertion (JWT <c>jti</c> / SAML
    /// assertion ID), when known.
    /// </summary>
    public string? EvidenceId { get; }

    /// <summary>
    /// Gets the authentication context, when known. May be present on failures (for
    /// example when a first factor succeeded and a later step failed).
    /// </summary>
    public AuthenticationContext? Context { get; }

    /// <summary>
    /// Gets additional result data.
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }

    /// <inheritdoc />
    public override string ToString()
        => Succeeded
            ? $"Succeeded ({Subject.Identifier.Value}, {Protocol})"
            : $"Failed ({Failure.Code}, {Protocol})";
}
