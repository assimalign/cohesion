using System;

using Assimalign.Cohesion.Security.DataProtection.Internal;

namespace Assimalign.Cohesion.Security.DataProtection;

// Deviates from the internal-implementation rule per owner decision 59 (#1465): the implementation is public.
/// <summary>
/// The default <see cref="IDataProtectionProvider"/>: roots every protector's purpose chain at
/// the application discriminator and shares one <see cref="KeyRing"/> across them.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("DataProtectionProvider")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class DataProtectionProvider : IDataProtectionProvider
{
    private readonly KeyRing _ring;
    private readonly string _discriminator;

    private DataProtectionProvider(KeyRing ring, string discriminator)
    {
        _ring = ring;
        _discriminator = discriminator;
    }

    /// <summary>
    /// Creates a provider over <paramref name="repository"/> with default
    /// <see cref="DataProtectionOptions"/>, optionally customized by <paramref name="configure"/>.
    /// </summary>
    /// <param name="repository">The key repository backing the ring (for example
    /// <see cref="FileSystemKeyRepository.Create(string)"/>).</param>
    /// <param name="configure">An optional callback to adjust the options before the ring is built.</param>
    /// <returns>A shareable data-protection provider.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="repository"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A configured option value is out of range.</exception>
    public static DataProtectionProvider Create(IKeyRepository repository, Action<DataProtectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(repository);

        DataProtectionOptions options = new();
        configure?.Invoke(options);
        return Create(options, repository);
    }

    /// <summary>
    /// Creates a provider over <paramref name="repository"/> with the supplied
    /// <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The key-ring options.</param>
    /// <param name="repository">The key repository backing the ring.</param>
    /// <returns>A shareable data-protection provider.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="repository"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An option value is out of range.</exception>
    public static DataProtectionProvider Create(DataProtectionOptions options, IKeyRepository repository)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(repository);

        return Create(options, repository, TimeProvider.System);
    }

    // Time-injectable overload used by tests to drive rotation and grace deterministically.
    internal static DataProtectionProvider Create(DataProtectionOptions options, IKeyRepository repository, TimeProvider timeProvider)
    {
        options.Validate();

        KeyRing ring = new(
            repository,
            timeProvider,
            options.KeyLifetime,
            options.UnprotectGracePeriod,
            options.UnknownKeyReloadInterval);
        return new DataProtectionProvider(ring, options.ApplicationDiscriminator);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="purpose"/> is <see langword="null"/>.</exception>
    public IDataProtector CreateProtector(string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        return new AesGcmDataProtector(_ring, new[] { _discriminator, purpose });
    }

    private sealed class DebugView
    {
        public DebugView(DataProtectionProvider value)
        {
        }
    }
}
