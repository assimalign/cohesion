using System;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// The default <see cref="IDataProtectionProvider"/>: roots every protector's purpose chain at
/// the application discriminator and shares one <see cref="KeyRing"/> across them.
/// </summary>
internal sealed class KeyRingProtectionProvider : IDataProtectionProvider
{
    private readonly KeyRing _ring;
    private readonly string _discriminator;

    public KeyRingProtectionProvider(KeyRing ring, string discriminator)
    {
        _ring = ring;
        _discriminator = discriminator;
    }

    /// <inheritdoc />
    public IDataProtector CreateProtector(string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        return new AesGcmDataProtector(_ring, new[] { _discriminator, purpose });
    }
}
