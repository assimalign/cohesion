using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.OpenApi.Versioning;

/// <summary>
/// The outcome of an <see cref="OpenApiVersionTransformer"/> transform: the transformed document and the
/// findings raised while producing it.
/// </summary>
public sealed class OpenApiTransformResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiTransformResult"/> class.
    /// </summary>
    /// <param name="document">The transformed document, targeting the requested version.</param>
    /// <param name="diagnostics">The findings raised while transforming.</param>
    public OpenApiTransformResult(OpenApiDocument document, IReadOnlyList<OpenApiTransformDiagnostic> diagnostics)
    {
        Document = document;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the transformed document, targeting the requested version.</summary>
    public OpenApiDocument Document { get; }

    /// <summary>Gets the findings raised while transforming.</summary>
    public IReadOnlyList<OpenApiTransformDiagnostic> Diagnostics { get; }

    /// <summary>Gets a value indicating whether any finding is an <see cref="OpenApiTransformSeverity.Error"/>.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.Severity == OpenApiTransformSeverity.Error);
}
