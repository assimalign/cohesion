using System;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// The public entry point that builds a rotating-key-ring <see cref="IDataProtectionProvider"/>
/// over an <see cref="IKeyRepository"/>. Create one per application at composition time (a
/// <c>*.Hosting</c> project) and share it.
/// </summary>
public static class DataProtectionProvider
{
    /// <summary>
    /// Creates a provider over <paramref name="repository"/> with default
    /// <see cref="DataProtectionOptions"/>, optionally customized by <paramref name="configure"/>.
    /// </summary>
    /// <param name="repository">The key repository backing the ring (for example
    /// <see cref="KeyRepository.CreateFileSystem(string)"/>).</param>
    /// <param name="configure">An optional callback to adjust the options before the ring is built.</param>
    /// <returns>A shareable data-protection provider.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="repository"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A configured option value is out of range.</exception>
    public static IDataProtectionProvider Create(IKeyRepository repository, Action<DataProtectionOptions>? configure = null)
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
    public static IDataProtectionProvider Create(DataProtectionOptions options, IKeyRepository repository)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(repository);

        return Create(options, repository, TimeProvider.System);
    }

    // Time-injectable overload used by tests to drive rotation and grace deterministically.
    internal static IDataProtectionProvider Create(DataProtectionOptions options, IKeyRepository repository, TimeProvider timeProvider)
    {
        options.Validate();

        KeyRing ring = new(repository, timeProvider, options.KeyLifetime, options.UnprotectGracePeriod);
        return new KeyRingProtectionProvider(ring, options.ApplicationDiscriminator);
    }
}
