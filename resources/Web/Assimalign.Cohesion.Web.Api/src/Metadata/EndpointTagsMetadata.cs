using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Endpoint metadata that groups an endpoint under one or more tags in its API description. The
/// convention verb <c>WithTags</c> attaches it to a route or to every route of a group.
/// </summary>
/// <remarks>
/// <para>
/// Tags compose rather than override: an endpoint's tags are the tags of every item it carries, read
/// with <c>GetOrderedMetadata&lt;EndpointTagsMetadata&gt;()</c> (outer group first, then the route),
/// with repeated names listed once. A group's tags therefore reach every child, and a child adds its
/// own beside them.
/// </para>
/// <para>
/// Like the other endpoint-description carriers in this package, this is neutral documentation
/// metadata: <c>Web.Api</c> knows nothing of OpenAPI, and a documentation adapter (the OpenAPI adapter,
/// #152) maps the tags onto its own model. This sealed carrier is the metadata contract; there is
/// deliberately no interface, following the endpoint-metadata family rule in the Web.Routing DESIGN.
/// </para>
/// </remarks>
public sealed class EndpointTagsMetadata
{
    /// <summary>
    /// Creates the tag metadata.
    /// </summary>
    /// <param name="tags">The tag names, in order. At least one is required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tags"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="tags"/> is empty or contains a <see langword="null"/>, empty, or whitespace entry.
    /// </exception>
    public EndpointTagsMetadata(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        if (tags.Length == 0)
        {
            throw new ArgumentException("At least one tag is required.", nameof(tags));
        }

        string[] copy = new string[tags.Length];

        for (int i = 0; i < tags.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(tags[i]))
            {
                throw new ArgumentException("Tags must not be null, empty, or whitespace.", nameof(tags));
            }

            copy[i] = tags[i];
        }

        Tags = Array.AsReadOnly(copy);
    }

    /// <summary>
    /// Gets the tag names, in the order they were declared. Never empty.
    /// </summary>
    public IReadOnlyList<string> Tags { get; }
}
