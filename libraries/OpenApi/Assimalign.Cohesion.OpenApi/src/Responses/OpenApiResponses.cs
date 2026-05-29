using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A container for the expected responses of an operation. See the "Responses Object" section of the
/// OpenAPI Specification.
/// </summary>
/// <remarks>
/// Keys are HTTP status codes (for example <c>200</c>), status-code ranges (for example <c>2XX</c>),
/// or the literal <c>default</c>.
/// </remarks>
public sealed class OpenApiResponses : IOpenApiExtensible
{
    /// <summary>Gets the responses keyed by status code, status-code range, or <c>default</c>.</summary>
    public IDictionary<string, OpenApiResponse> Items { get; } = new Dictionary<string, OpenApiResponse>();

    /// <summary>Gets or sets the response for the specified status-code key.</summary>
    /// <param name="statusCode">The status code, range, or <c>default</c>.</param>
    /// <returns>The response associated with <paramref name="statusCode"/>.</returns>
    public OpenApiResponse this[string statusCode]
    {
        get => Items[statusCode];
        set => Items[statusCode] = value;
    }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}
