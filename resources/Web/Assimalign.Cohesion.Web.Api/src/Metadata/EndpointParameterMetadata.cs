using System;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Endpoint metadata that describes one request-bound parameter of a typed endpoint: the name the
/// request supplies it under, where it is read from, its CLR type, and whether the request must supply
/// it.
/// </summary>
/// <remarks>
/// <para>
/// The Web endpoint-binding source generator attaches one instance per request-bound parameter, in
/// handler order, to every typed endpoint it maps. Parameters the request does not supply — the injected
/// <c>IHttpContext</c>, <c>IHttpRequest</c>, <c>IHttpResponse</c>, <c>CancellationToken</c> and
/// <c>IHttpFeature</c> types — are not described. Read
/// the descriptions from a route's metadata with
/// <c>GetOrderedMetadata&lt;EndpointParameterMetadata&gt;()</c>, either from a built route
/// (<c>IRouterRoute.Metadata</c>) or, during a request, from <c>context.GetEndpointMetadata()</c>.
/// </para>
/// <para>
/// The description lets a documentation adapter (OpenAPI, #152) describe an endpoint without reflection:
/// <see cref="Type"/> is a <c>typeof(...)</c> value written by the generator, never found by inspecting
/// members, so a schema can be produced from the application's source-generated <c>JsonTypeInfo</c> for
/// that type, and a scalar maps to its primitive schema.
/// </para>
/// <para>
/// This sealed carrier is the metadata contract; there is deliberately no interface, following the
/// endpoint-metadata family rule in the Web.Routing DESIGN.
/// </para>
/// </remarks>
public sealed class EndpointParameterMetadata
{
    /// <summary>
    /// Creates the description of one request-bound parameter.
    /// </summary>
    /// <param name="name">
    /// The name the request supplies the value under: the route parameter, query key, header, or form
    /// field name, which is an attribute's <c>Name</c> when one is given. For
    /// <see cref="EndpointParameterSource.Body"/>, the handler parameter's name.
    /// </param>
    /// <param name="source">Where the value is read from.</param>
    /// <param name="type">
    /// The parameter's declared CLR type. An optional value type is described as its
    /// <see cref="Nullable{T}"/> form.
    /// </param>
    /// <param name="isRequired">
    /// Whether the request must supply the value. A required value that is missing is answered with
    /// <c>400 Bad Request</c> before the handler runs; a request body is always required.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is not a defined <see cref="EndpointParameterSource"/>.</exception>
    public EndpointParameterMetadata(string name, EndpointParameterSource source, Type type, bool isRequired)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(type);

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "The parameter source is not a defined EndpointParameterSource value.");
        }

        Name = name;
        Source = source;
        Type = type;
        IsRequired = isRequired;
    }

    /// <summary>
    /// Gets the name the request supplies the value under, or the handler parameter's name for a request
    /// body. Never <see langword="null"/> or empty.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets where the value is read from.
    /// </summary>
    public EndpointParameterSource Source { get; }

    /// <summary>
    /// Gets the parameter's declared CLR type.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// Gets whether the request must supply the value.
    /// </summary>
    public bool IsRequired { get; }
}
