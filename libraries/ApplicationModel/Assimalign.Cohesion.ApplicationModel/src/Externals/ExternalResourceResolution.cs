using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The immutable result of attempting to resolve one external resource.
/// </summary>
public sealed class ExternalResourceResolution
{
    /// <summary>Initializes an external resource resolution.</summary>
    /// <param name="resolved">Whether an authoritative endpoint set was found.</param>
    /// <param name="endpoints">The observed endpoint set.</param>
    /// <param name="manifestHash">The provider's canonical manifest hash, when exported.</param>
    /// <param name="schemaVersion">The provider's export schema version, when exported.</param>
    /// <param name="detail">A human-readable resolution detail.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="endpoints"/> is <see langword="null"/>.
    /// </exception>
    public ExternalResourceResolution(
        bool resolved,
        IReadOnlyList<ResourceEndpoint> endpoints,
        string? manifestHash = null,
        int? schemaVersion = null,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Resolved = resolved;
        Endpoints = Copy(endpoints);
        ManifestHash = manifestHash;
        SchemaVersion = schemaVersion;
        Detail = detail;
    }

    /// <summary>Gets whether the external was resolved.</summary>
    public bool Resolved { get; }

    /// <summary>Gets the external's observed endpoints.</summary>
    public IReadOnlyList<ResourceEndpoint> Endpoints { get; }

    /// <summary>Gets the provider's canonical manifest hash, when known.</summary>
    public string? ManifestHash { get; }

    /// <summary>Gets the provider's export schema version, when known.</summary>
    public int? SchemaVersion { get; }

    /// <summary>Gets a diagnostic describing the resolution.</summary>
    public string? Detail { get; }

    /// <summary>Creates an unresolved result with an actionable diagnostic.</summary>
    /// <param name="detail">The reason no endpoint set was found.</param>
    /// <returns>An unresolved result.</returns>
    public static ExternalResourceResolution Unresolved(string detail) =>
        new(false, Array.Empty<ResourceEndpoint>(), detail: detail);

    private static IReadOnlyList<ResourceEndpoint> Copy(IReadOnlyList<ResourceEndpoint> source)
    {
        var copy = new ResourceEndpoint[source.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = source[index];
        }

        return new ReadOnlyCollection<ResourceEndpoint>(copy);
    }
}
