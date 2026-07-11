namespace Assimalign.Cohesion.Web.Diagnostics;

/// <summary>
/// Endpoint metadata that overrides the HTTP logging field set for the routes it is attached
/// to. Attach an instance to a route's metadata bag to change what the logging middleware
/// emits for that endpoint — including <see cref="HttpLoggingFields.None"/> to silence it
/// entirely (the usual choice for health probes).
/// </summary>
/// <remarks>
/// <para>
/// The middleware resolves this metadata with last-wins semantics
/// (<c>IRouterRouteMetadataCollection.GetMetadata&lt;HttpLoggingMetadata&gt;()</c>) after the
/// downstream pipeline has completed, so a group-level override is superseded by an
/// endpoint-level one, and the lookup costs nothing on routes that carry no override.
/// </para>
/// <para>
/// Because the override is only observable <em>after</em> routing has run, it can freely turn
/// emission-time fields on or off (request line, headers, duration, status, ...), but it cannot
/// arm body capture the global <see cref="HttpLoggingOptions.Fields"/> left disabled: capture
/// streams are attached before the downstream pipeline runs, when no route is known yet.
/// <see cref="HttpLoggingFields.RequestBody"/>, <see cref="HttpLoggingFields.ResponseBody"/>,
/// and <see cref="HttpLoggingFields.BytesTransferred"/> in an override therefore only
/// <em>narrow</em> the globally armed set.
/// </para>
/// <para>
/// This sealed carrier <em>is</em> the metadata contract — there is deliberately no
/// <c>IHttpLoggingMetadata</c> interface. Metadata items in the bag are immutable data
/// carriers; the sealed type guarantees the value the middleware reads is the one attached at
/// map time.
/// </para>
/// </remarks>
public sealed class HttpLoggingMetadata
{
    /// <summary>
    /// Creates logging metadata carrying the effective field set for the endpoint.
    /// </summary>
    /// <param name="fields">
    /// The fields to emit for exchanges handled by the endpoint.
    /// <see cref="HttpLoggingFields.None"/> suppresses the entry entirely.
    /// </param>
    public HttpLoggingMetadata(HttpLoggingFields fields)
    {
        Fields = fields;
    }

    /// <summary>
    /// Gets the field set emitted for exchanges handled by the endpoint.
    /// </summary>
    public HttpLoggingFields Fields { get; }
}
