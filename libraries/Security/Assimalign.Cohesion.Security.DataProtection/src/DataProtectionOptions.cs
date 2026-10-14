using System;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// Configures the key ring behind a data-protection provider: how long keys sign for, how
/// long retired keys keep unprotecting, how often an unknown key id may reload the
/// repository, and which application the ring belongs to.
/// </summary>
/// <remarks>
/// These values are a deployment decision and belong in the composition root (a
/// <c>*.Hosting</c> project), not in request-path code. Defaults suit a single application
/// with periodic rotation; multi-node deployments simply point every node at the same
/// repository and use the same <see cref="ApplicationDiscriminator"/>.
/// </remarks>
public sealed class DataProtectionOptions
{
    /// <summary>
    /// Gets or sets the application isolation discriminator. It is folded into every subkey
    /// derivation as the root of the purpose chain, so two applications that share a
    /// repository but use different discriminators cannot read each other's payloads. Nodes of
    /// the <em>same</em> application must use the <em>same</em> value. It is not a secret.
    /// Defaults to an empty string (no additional isolation beyond the repository itself).
    /// </summary>
    public string ApplicationDiscriminator { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how long a newly created key is eligible to protect new payloads before
    /// the ring rotates to a fresh key. Must be greater than zero. Defaults to 90 days.
    /// </summary>
    public TimeSpan KeyLifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Gets or sets how long past its expiration a retired (non-revoked) key may still
    /// unprotect existing payloads. This window lets payloads minted just before a rotation —
    /// or under a node with a slightly skewed clock — keep validating. Must be zero or
    /// greater. Defaults to 7 days.
    /// </summary>
    public TimeSpan UnprotectGracePeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets the minimum time between the key-repository reloads that an unknown key id
    /// can cause. Must be greater than zero. Defaults to 30 seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A protected payload names the key that produced it, and the client that sent the
    /// payload chooses those bytes. When the id is not in the ring, the ring reloads the
    /// repository, because another node may have created the key since the last load. That
    /// reload opens a window of this length, and every unknown id inside the window is reported
    /// unknown without reading the repository. This caps the reads that invented key ids can
    /// cause at one per interval.
    /// </para>
    /// <para>
    /// The interval also bounds key propagation: a key another node writes to the repository
    /// becomes resolvable here no later than one interval after it is written. Unprotecting
    /// with a key the ring already holds never waits on a reload.
    /// </para>
    /// </remarks>
    public TimeSpan UnknownKeyReloadInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Validates the option values, throwing when they are inconsistent.
    /// </summary>
    /// <exception cref="ArgumentException">A value is out of range.</exception>
    internal void Validate()
    {
        if (ApplicationDiscriminator is null)
        {
            throw new ArgumentException("ApplicationDiscriminator must not be null.", nameof(ApplicationDiscriminator));
        }

        if (KeyLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentException("KeyLifetime must be greater than zero.", nameof(KeyLifetime));
        }

        if (UnprotectGracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentException("UnprotectGracePeriod must be zero or greater.", nameof(UnprotectGracePeriod));
        }

        if (UnknownKeyReloadInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("UnknownKeyReloadInterval must be greater than zero.", nameof(UnknownKeyReloadInterval));
        }
    }
}
