using System;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// A concrete key in the ring: the public <see cref="IKey"/> metadata plus the secret
/// master material used as HKDF input keying material. The master never leaves the assembly —
/// only derived, purpose-bound subkeys are used for encryption, and those are zeroed after
/// each operation.
/// </summary>
internal sealed class ManagedKey : IKey
{
    /// <summary>The length, in bytes, of a key's master secret (256 bits).</summary>
    public const int MasterLength = 32;

    private readonly byte[] _master;

    public ManagedKey(
        Guid keyId,
        DateTimeOffset createdAt,
        DateTimeOffset activatedAt,
        DateTimeOffset expiresAt,
        bool isRevoked,
        byte[] master)
    {
        ArgumentNullException.ThrowIfNull(master);
        if (master.Length != MasterLength)
        {
            throw new ArgumentException($"A key master must be exactly {MasterLength} bytes.", nameof(master));
        }

        KeyId = keyId;
        CreatedAt = createdAt;
        ActivatedAt = activatedAt;
        ExpiresAt = expiresAt;
        IsRevoked = isRevoked;
        _master = master;
    }

    /// <inheritdoc />
    public Guid KeyId { get; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; }

    /// <inheritdoc />
    public DateTimeOffset ActivatedAt { get; }

    /// <inheritdoc />
    public DateTimeOffset ExpiresAt { get; }

    /// <inheritdoc />
    public bool IsRevoked { get; }

    /// <summary>Gets the 32-byte master secret used as HKDF input keying material.</summary>
    public ReadOnlySpan<byte> Master => _master;
}
