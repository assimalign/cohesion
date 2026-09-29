using System;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes one observed endpoint published by an application export.
/// </summary>
public sealed class ApplicationExportEndpoint
{
    /// <summary>
    /// Initializes an exported endpoint.
    /// </summary>
    /// <param name="name">The manifest endpoint name.</param>
    /// <param name="internal">The internal address visible to colocated workloads.</param>
    /// <param name="public">The public address, when one is exposed.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> or <paramref name="internal"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="internal"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ApplicationExportEndpoint(string name, string @internal, string? @public = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(@internal);

        Name = name;
        Internal = @internal;
        Public = @public;
    }

    /// <summary>Gets the manifest endpoint name.</summary>
    public string Name { get; }

    /// <summary>Gets the internal address visible to colocated workloads.</summary>
    public string Internal { get; }

    /// <summary>Gets the public address, when one is exposed.</summary>
    public string? Public { get; }
}
