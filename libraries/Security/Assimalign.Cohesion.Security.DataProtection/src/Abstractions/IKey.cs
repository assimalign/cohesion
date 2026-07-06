using System;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// Read-only metadata describing one key in the ring. The key's secret material is never
/// exposed through this contract — only the identity and lifecycle timestamps the ring uses
/// to decide which key signs new payloads and which retired keys may still unprotect.
/// </summary>
public interface IKey
{
    /// <summary>
    /// Gets the stable identifier written into the header of every payload this key protects.
    /// The ring resolves the producing key on unprotect by this id.
    /// </summary>
    Guid KeyId { get; }

    /// <summary>
    /// Gets the instant the key was created.
    /// </summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Gets the instant from which the key is eligible to protect new payloads.
    /// </summary>
    DateTimeOffset ActivatedAt { get; }

    /// <summary>
    /// Gets the instant after which the key no longer protects new payloads. The key may
    /// still unprotect existing payloads until <see cref="ExpiresAt"/> plus the ring's
    /// configured grace period.
    /// </summary>
    DateTimeOffset ExpiresAt { get; }

    /// <summary>
    /// Gets a value indicating whether the key has been revoked. A revoked key neither
    /// protects new payloads nor unprotects existing ones, regardless of its expiration.
    /// </summary>
    bool IsRevoked { get; }
}
