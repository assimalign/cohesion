namespace Assimalign.Cohesion.OpenApi.Integration;

/// <summary>
/// Produces the OpenApi description a service exposes (for example at a Web layer's <c>/openapi.json</c>
/// endpoint) by composing its <see cref="IOpenApiEndpointSource"/> contributions into a version-targeted
/// document. The service layer depends on this contract, not on the generation internals.
/// </summary>
public interface IOpenApiDescriptionProvider
{
    /// <summary>
    /// Builds the OpenApi document for a target line.
    /// </summary>
    /// <param name="version">The OpenAPI line to target.</param>
    /// <returns>The composed, version-targeted document.</returns>
    OpenApiDocument GetDocument(OpenApiSpecVersion version);
}
