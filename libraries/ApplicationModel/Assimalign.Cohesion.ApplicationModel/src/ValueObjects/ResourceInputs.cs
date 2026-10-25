using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The late-bound mount contents and bootstrap credential supplied to a controller for one
/// reconcile pass.
/// </summary>
public sealed class ResourceInputs
{
    private static readonly IReadOnlyDictionary<ResourceExtensionKey, JsonElement> _noOverrides =
        new ReadOnlyDictionary<ResourceExtensionKey, JsonElement>(new Dictionary<ResourceExtensionKey, JsonElement>());

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

    /// <summary>
    /// Gets the directory holding the resource's deployment record, as the resource's instances see it, or
    /// <see langword="null"/> when the gateway delivers no record. A controller passes it to the resource as
    /// <see cref="AppEnvironment.Variables.ConfigDirectory"/>.
    /// </summary>
    public string? ConfigurationDirectory { get; private init; }

    /// <summary>
    /// Gets the deployment generation the delivered record belongs to, or <see langword="null"/> when the gateway
    /// delivers no record. Management overrides last for one generation.
    /// </summary>
    public string? DeploymentGeneration { get; private init; }

    /// <summary>
    /// Gets the management overrides of the resource's platform surfaces for the current generation, keyed by
    /// surface, as RFC 7396 merge patches. A platform compiler applies each over the value code declares
    /// (<see cref="ResourcePlatformSurface.Apply"/>). Empty when there are none.
    /// </summary>
    public IReadOnlyDictionary<ResourceExtensionKey, JsonElement> PlatformOverrides { get; private init; } = _noOverrides;

    /// <summary>
    /// Gets the digest of what the delivered record sets, through overrides and claims, for sections an instance
    /// applies only when it starts, or <see langword="null"/> when the record sets none.
    /// </summary>
    /// <remarks>
    /// A platform that restarts instances for such a change stamps it on what its instances start from, such as a
    /// Kubernetes pod template annotation, so a deployment that discards a restart-class override restarts the
    /// instances even when nothing else about them changed.
    /// </remarks>
    public string? RestartDigest { get; private init; }

    /// <summary>Returns a copy of these inputs that delivers a deployment record.</summary>
    /// <param name="directory">The record directory, as the resource's instances see it.</param>
    /// <param name="generation">The deployment generation the record belongs to.</param>
    /// <param name="platformOverrides">The current generation's platform surface overrides, or null for none.</param>
    /// <param name="restartDigest">The record's restart digest, or null when it sets no restart-class section.</param>
    /// <returns>The copy; the overrides are copied.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> or <paramref name="generation"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> or <paramref name="generation"/> is null.</exception>
    public ResourceInputs WithDeploymentRecord(
        string directory,
        string generation,
        IReadOnlyDictionary<ResourceExtensionKey, JsonElement>? platformOverrides = null,
        string? restartDigest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        IReadOnlyDictionary<ResourceExtensionKey, JsonElement> overrides = _noOverrides;
        if (platformOverrides is { Count: > 0 })
        {
            var copy = new Dictionary<ResourceExtensionKey, JsonElement>(platformOverrides.Count);
            foreach ((ResourceExtensionKey key, JsonElement patch) in platformOverrides)
            {
                copy.Add(key, patch.Clone());
            }

            overrides = new ReadOnlyDictionary<ResourceExtensionKey, JsonElement>(copy);
        }

        return new ResourceInputs(Mounts, BootstrapCredential, ApplicationTrustKey, TrustBundle)
        {
            ConfigurationDirectory = directory,
            DeploymentGeneration = generation,
            PlatformOverrides = overrides,
            RestartDigest = string.IsNullOrEmpty(restartDigest) ? null : restartDigest,
        };
    }
}
