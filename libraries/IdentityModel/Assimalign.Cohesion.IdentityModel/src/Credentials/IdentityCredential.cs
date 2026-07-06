using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents an immutable description of a credential: its identity, kind, administrative
/// state, and validity window. Credential models carry references and metadata only —
/// <b>never secret material</b>. Secrets live with the systems that verify them.
/// </summary>
public sealed class IdentityCredential
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityCredential" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The credential contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a property name is blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no identifier, or when <c>ExpiresAt</c> is not after
    /// <c>NotBefore</c> (both instants come from the credential itself, so a backwards
    /// window is a data invariant, unlike the session model's wire-sourced expiry).
    /// </exception>
    public IdentityCredential(IdentityCredentialDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (string.IsNullOrWhiteSpace(descriptor.Id))
        {
            throw new IdentityModelException("A credential requires an identifier.");
        }

        if (descriptor.NotBefore is not null &&
            descriptor.ExpiresAt is not null &&
            descriptor.ExpiresAt <= descriptor.NotBefore)
        {
            throw new IdentityModelException(
                "A credential's expiry must be after its validity start.");
        }

        Id = descriptor.Id;
        Kind = descriptor.Kind;
        State = descriptor.State;
        Subject = descriptor.Subject;
        CreatedAt = descriptor.CreatedAt;
        NotBefore = descriptor.NotBefore;
        ExpiresAt = descriptor.ExpiresAt;
        Properties = ModelSnapshot.Properties(descriptor.Properties, nameof(descriptor));
    }

    /// <summary>
    /// Gets the credential identifier (for example a key identifier, certificate
    /// thumbprint, or passkey credential id).
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the credential kind.
    /// </summary>
    public IdentityCredentialKind Kind { get; }

    /// <summary>
    /// Gets the administrative state.
    /// </summary>
    public IdentityCredentialState State { get; }

    /// <summary>
    /// Gets the identifier of the subject the credential belongs to, when known.
    /// </summary>
    public SubjectIdentifier? Subject { get; }

    /// <summary>
    /// Gets the instant the credential was created or enrolled.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; }

    /// <summary>
    /// Gets the instant the credential becomes valid.
    /// </summary>
    public DateTimeOffset? NotBefore { get; }

    /// <summary>
    /// Gets the instant the credential expires.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// Gets additional credential metadata.
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }

    /// <summary>
    /// Determines whether the credential is usable at the provided instant: the state is
    /// <see cref="IdentityCredentialState.Active" /> and the instant falls within the
    /// validity window.
    /// </summary>
    /// <param name="at">The instant to evaluate.</param>
    /// <returns><see langword="true" /> when the credential is usable; otherwise <see langword="false" />.</returns>
    public bool IsUsable(DateTimeOffset at)
    {
        return State == IdentityCredentialState.Active
            && (NotBefore is null || at >= NotBefore)
            && (ExpiresAt is null || at < ExpiresAt);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Id} ({Kind}, {State})";
}
