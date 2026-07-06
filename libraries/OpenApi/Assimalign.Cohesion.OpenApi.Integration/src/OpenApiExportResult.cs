using System.Collections.Generic;

using Assimalign.Cohesion.OpenApi.Versioning;

namespace Assimalign.Cohesion.OpenApi.Integration;

/// <summary>
/// The outcome of a version-retargeting export: the serialized document plus the diagnostics raised
/// while transforming it to the requested line.
/// </summary>
public sealed class OpenApiExportResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiExportResult"/> class.
    /// </summary>
    /// <param name="content">The serialized document.</param>
    /// <param name="diagnostics">The retarget diagnostics.</param>
    public OpenApiExportResult(string content, IReadOnlyList<OpenApiTransformDiagnostic> diagnostics)
    {
        Content = content;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the serialized document.</summary>
    public string Content { get; }

    /// <summary>Gets the diagnostics raised while retargeting the document to the requested line.</summary>
    public IReadOnlyList<OpenApiTransformDiagnostic> Diagnostics { get; }
}
