using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Integration;

/// <summary>
/// The entry point for composing the OpenApi integration contracts. Service layers create a description
/// provider from their endpoint sources, or an importer/exporter for management flows, without touching
/// the generation, serialization, or versioning internals.
/// </summary>
public static class OpenApiIntegration
{
    /// <summary>
    /// Creates a description provider that composes the given endpoint sources.
    /// </summary>
    /// <param name="info">The document-level metadata.</param>
    /// <param name="sources">The endpoint metadata sources to compose.</param>
    /// <returns>A description provider.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="info"/> or <paramref name="sources"/> is <see langword="null"/>.</exception>
    public static IOpenApiDescriptionProvider CreateProvider(OpenApiDescriptionInfo info, params IOpenApiEndpointSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(sources);
        return new OpenApiDescriptionProvider(sources, info);
    }

    /// <summary>
    /// Creates a description provider that composes the given endpoint sources.
    /// </summary>
    /// <param name="info">The document-level metadata.</param>
    /// <param name="sources">The endpoint metadata sources to compose.</param>
    /// <returns>A description provider.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="info"/> or <paramref name="sources"/> is <see langword="null"/>.</exception>
    public static IOpenApiDescriptionProvider CreateProvider(OpenApiDescriptionInfo info, IReadOnlyList<IOpenApiEndpointSource> sources)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(sources);
        return new OpenApiDescriptionProvider(sources, info);
    }

    /// <summary>Creates the default document importer.</summary>
    /// <returns>An importer over the JSON and YAML readers.</returns>
    public static IOpenApiDocumentImporter CreateImporter() => new OpenApiDocumentImporter();

    /// <summary>Creates the default document exporter.</summary>
    /// <returns>An exporter over the writers and the transform pipeline.</returns>
    public static IOpenApiDocumentExporter CreateExporter() => new OpenApiDocumentExporter();
}
