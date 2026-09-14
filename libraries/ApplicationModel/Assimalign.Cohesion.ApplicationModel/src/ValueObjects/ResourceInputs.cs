using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The late-bound mount contents and bootstrap credential supplied to a controller for one
/// reconcile pass.
/// </summary>
public sealed class ResourceInputs
{
    /// <summary>Gets an input set with no mounts or bootstrap credential.</summary>
    public static ResourceInputs Empty { get; } = new(
        new Dictionary<string, ResourceMountInput>(StringComparer.Ordinal),
        ReadOnlyMemory<byte>.Empty,
        ReadOnlyMemory<byte>.Empty);

    /// <summary>Initializes an immutable resource input set.</summary>
    /// <param name="mounts">Inputs keyed by plan mount name.</param>
    /// <param name="bootstrapCredential">
    /// The short-lived bootstrap credential, or empty content when none is required.
    /// The value is defensively copied.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="mounts"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A mount name is empty or a mount input is <see langword="null"/>.</exception>
    public ResourceInputs(
        IReadOnlyDictionary<string, ResourceMountInput> mounts,
        ReadOnlyMemory<byte> bootstrapCredential)
        : this(mounts, bootstrapCredential, ReadOnlyMemory<byte>.Empty)
    {
    }

    /// <summary>Initializes an immutable resource input set with its public trust key.</summary>
    /// <param name="mounts">Inputs keyed by plan mount name.</param>
    /// <param name="bootstrapCredential">
    /// The short-lived bootstrap credential, or empty content when none is required.
    /// The value is defensively copied.
    /// </param>
    /// <param name="applicationTrustKey">
    /// The application's public JSON Web Key encoded as UTF-8, or empty content when unavailable.
    /// The value is defensively copied.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="mounts"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A mount name is empty or a mount input is <see langword="null"/>.</exception>
    public ResourceInputs(
        IReadOnlyDictionary<string, ResourceMountInput> mounts,
        ReadOnlyMemory<byte> bootstrapCredential,
        ReadOnlyMemory<byte> applicationTrustKey)
        : this(mounts, bootstrapCredential, applicationTrustKey, default)
    {
    }

    /// <summary>Initializes inputs with public application identity and transport trust anchors.</summary>
    /// <param name="mounts">Inputs keyed by plan mount name.</param>
    /// <param name="bootstrapCredential">The bootstrap credential, defensively copied.</param>
    /// <param name="applicationTrustKey">The public JSON Web Key, defensively copied.</param>
    /// <param name="trustBundle">PEM transport trust-anchor certificates without private keys, defensively copied.</param>
    public ResourceInputs(
        IReadOnlyDictionary<string, ResourceMountInput> mounts,
        ReadOnlyMemory<byte> bootstrapCredential,
        ReadOnlyMemory<byte> applicationTrustKey,
        ReadOnlyMemory<byte> trustBundle)
    {
        ArgumentNullException.ThrowIfNull(mounts);

        var copy = new Dictionary<string, ResourceMountInput>(mounts.Count, StringComparer.Ordinal);
        foreach ((string name, ResourceMountInput input) in mounts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (input is null)
            {
                throw new ArgumentException($"Mount input '{name}' is null.", nameof(mounts));
            }

            copy.Add(name, input);
        }

        Mounts = new ReadOnlyDictionary<string, ResourceMountInput>(copy);
        BootstrapCredential = bootstrapCredential.IsEmpty
            ? ReadOnlyMemory<byte>.Empty
            : bootstrapCredential.ToArray();
        ApplicationTrustKey = applicationTrustKey.IsEmpty
            ? ReadOnlyMemory<byte>.Empty
            : applicationTrustKey.ToArray();
        TrustBundle = trustBundle.ToArray();
    }

    /// <summary>Gets immutable mount inputs keyed by plan mount name.</summary>
    public IReadOnlyDictionary<string, ResourceMountInput> Mounts { get; }

    /// <summary>Gets the immutable bootstrap credential, or empty content when none was issued.</summary>
    public ReadOnlyMemory<byte> BootstrapCredential { get; }

    /// <summary>
    /// Gets the application's public JSON Web Key encoded as UTF-8, or empty content when none
    /// was supplied.
    /// </summary>
    public ReadOnlyMemory<byte> ApplicationTrustKey { get; }

    /// <summary>Gets transport trust-anchor certificates as one PEM document, without private keys.</summary>
    public ReadOnlyMemory<byte> TrustBundle { get; }
}
