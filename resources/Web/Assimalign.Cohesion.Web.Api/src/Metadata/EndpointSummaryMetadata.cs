using System;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Endpoint metadata that gives an endpoint a short summary in its API description. The convention
/// verb <c>WithSummary</c> attaches it.
/// </summary>
/// <remarks>
/// <para>
/// The most specific declaration wins: read it with <c>GetMetadata&lt;EndpointSummaryMetadata&gt;()</c>,
/// so a route's summary replaces one its group declared.
/// </para>
/// <para>
/// Neutral documentation metadata, like <see cref="EndpointTagsMetadata"/>: a documentation adapter maps
/// it onto its own model. This sealed carrier is the metadata contract; there is deliberately no
/// interface.
/// </para>
/// </remarks>
public sealed class EndpointSummaryMetadata
{
    /// <summary>
    /// Creates the summary metadata.
    /// </summary>
    /// <param name="summary">The summary: one short line.</param>
    /// <exception cref="ArgumentNullException"><paramref name="summary"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="summary"/> is empty or whitespace.</exception>
    public EndpointSummaryMetadata(string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        Summary = summary;
    }

    /// <summary>
    /// Gets the summary. Never <see langword="null"/>, empty, or whitespace.
    /// </summary>
    public string Summary { get; }
}
