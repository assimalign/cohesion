using System;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Endpoint metadata that gives an endpoint a longer description in its API description. The convention
/// verb <c>WithDescription</c> attaches it.
/// </summary>
/// <remarks>
/// <para>
/// The most specific declaration wins: read it with
/// <c>GetMetadata&lt;EndpointDescriptionMetadata&gt;()</c>, so a route's description replaces one its
/// group declared. Documentation formats such as OpenAPI render the text as CommonMark.
/// </para>
/// <para>
/// Neutral documentation metadata, like <see cref="EndpointTagsMetadata"/>: a documentation adapter maps
/// it onto its own model. This sealed carrier is the metadata contract; there is deliberately no
/// interface.
/// </para>
/// </remarks>
public sealed class EndpointDescriptionMetadata
{
    /// <summary>
    /// Creates the description metadata.
    /// </summary>
    /// <param name="description">The description.</param>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="description"/> is empty or whitespace.</exception>
    public EndpointDescriptionMetadata(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Description = description;
    }

    /// <summary>
    /// Gets the description. Never <see langword="null"/>, empty, or whitespace.
    /// </summary>
    public string Description { get; }
}
