using System;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes one statically bound endpoint in a serialized external resource.
/// </summary>
public sealed class ApplicationModelExternalEndpointDocument
{
    /// <summary>Initializes a serialized external endpoint binding.</summary>
    /// <param name="name">The endpoint name.</param>
    /// <param name="url">The absolute endpoint URL.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> or <paramref name="url"/> is empty, or the URL is not absolute.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="url"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ApplicationModelExternalEndpointDocument(string name, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            throw new ArgumentException("An external endpoint URL must be absolute.", nameof(url));
        }

        Name = name;
        Url = url;
    }

    /// <summary>Gets the endpoint name.</summary>
    public string Name { get; }

    /// <summary>Gets the absolute endpoint URL.</summary>
    public string Url { get; }
}
