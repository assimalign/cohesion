using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents an immutable description of key material published by a protocol party
/// (a SAML metadata key descriptor / a JWK Set entry). The model is descriptive only —
/// it carries key <em>references and metadata</em>, never performs cryptography, and never
/// parses key material.
/// </summary>
public sealed class ProtocolKey
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolKey" /> class by snapshotting
    /// the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The key contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a certificate or algorithm entry is null or whitespace, or when a
    /// property name is blank or a property value is undefined.
    /// </exception>
    public ProtocolKey(ProtocolKeyDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Use = descriptor.Use;
        KeyId = descriptor.KeyId;
        Role = descriptor.Role;
        Certificates = ModelSnapshot.Strings(descriptor.Certificates, nameof(descriptor));
        Algorithms = ModelSnapshot.Strings(descriptor.Algorithms, nameof(descriptor));
        Properties = ModelSnapshot.Properties(descriptor.Properties, nameof(descriptor));
    }

    /// <summary>
    /// Gets the declared use restriction.
    /// </summary>
    public ProtocolKeyUse Use { get; }

    /// <summary>
    /// Gets the key identifier (JWK <c>kid</c> / a key name).
    /// </summary>
    public string? KeyId { get; }

    /// <summary>
    /// Gets the role this key serves, when the publishing entity plays several roles.
    /// Null means entity-wide. Consumers selecting keys for a role-specific operation
    /// (for example verifying identity-provider-issued assertions from a dual-role
    /// entity) must respect this scope.
    /// </summary>
    public ProtocolRole? Role { get; }

    /// <summary>
    /// Gets the certificates carrying the key, as base64 DER strings, in order.
    /// </summary>
    public IReadOnlyList<string> Certificates { get; }

    /// <summary>
    /// Gets the algorithms the key is declared for, in order. Empty means undeclared.
    /// </summary>
    public IReadOnlyList<string> Algorithms { get; }

    /// <summary>
    /// Gets additional key detail as typed values.
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }

    /// <summary>
    /// Gets a value indicating whether the key may be used for signing: its use is either
    /// declared as <see cref="ProtocolKeyUse.Signing" /> or left unspecified (which both
    /// SAML and JWK define as unrestricted).
    /// </summary>
    public bool CanSign => Use is ProtocolKeyUse.Signing or ProtocolKeyUse.Unspecified;

    /// <summary>
    /// Gets a value indicating whether the key may be used for encryption: its use is
    /// either declared as <see cref="ProtocolKeyUse.Encryption" /> or left unspecified.
    /// </summary>
    public bool CanEncrypt => Use is ProtocolKeyUse.Encryption or ProtocolKeyUse.Unspecified;

    /// <inheritdoc />
    public override string ToString() => $"{KeyId ?? "(no key id)"} ({Use})";
}
