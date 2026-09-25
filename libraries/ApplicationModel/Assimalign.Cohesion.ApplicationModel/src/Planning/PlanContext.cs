using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Carries the immutable manifest, deployer options, target environment, and referenced
/// manifests available while a resource plan is computed.
/// </summary>
public sealed class PlanContext
{
    /// <summary>Initializes a planning context.</summary>
    /// <param name="manifest">The manifest being planned.</param>
    /// <param name="options">The typed deployer options for the resource.</param>
    /// <param name="environment">The environment into which the resource will be realized.</param>
    /// <param name="references">Referenced resource manifests keyed by resource name.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public PlanContext(
        ResourceManifest manifest,
        IResourceOptions options,
        IApplicationEnvironment environment,
        IReadOnlyDictionary<string, ResourceManifest> references)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(references);

        Manifest = manifest;
        Options = options;
        Environment = environment;

        var referenceCopy = new Dictionary<string, ResourceManifest>(StringComparer.Ordinal);
        foreach ((string name, ResourceManifest reference) in references)
        {
            referenceCopy.Add(name, reference);
        }

        References = new ReadOnlyDictionary<string, ResourceManifest>(referenceCopy);
    }

    /// <summary>Gets the resource manifest being planned.</summary>
    public ResourceManifest Manifest { get; }

    /// <summary>Gets the resource's typed deployer options.</summary>
    public IResourceOptions Options { get; }

    /// <summary>Gets the target application environment.</summary>
    public IApplicationEnvironment Environment { get; }

    /// <summary>Gets the immutable referenced-manifest map.</summary>
    public IReadOnlyDictionary<string, ResourceManifest> References { get; }

    /// <summary>Gets the deployer options as the resource area's concrete option type.</summary>
    /// <typeparam name="TOptions">The expected resource option type.</typeparam>
    /// <returns>The typed options instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// The context carries a different option type.
    /// </exception>
    public TOptions GetOptions<TOptions>() where TOptions : class, IResourceOptions
    {
        if (Options is TOptions typedOptions)
        {
            return typedOptions;
        }

        throw new InvalidOperationException(
            $"Planning options are '{Options.GetType().FullName}', not '{typeof(TOptions).FullName}'.");
    }
}
