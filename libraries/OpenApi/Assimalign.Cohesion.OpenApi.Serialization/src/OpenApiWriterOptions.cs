namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// Options that control how an <see cref="OpenApiDocument"/> is written.
/// </summary>
public sealed class OpenApiWriterOptions
{
    /// <summary>
    /// Gets or sets the OpenAPI line to target. When <see langword="null"/>, the document's
    /// <see cref="OpenApiDocument.SpecVersion"/> is used. Setting this allows a single document to be
    /// emitted against a different line, with version-gated fields adapted accordingly.
    /// </summary>
    public OpenApiSpecVersion? Version { get; set; }

    /// <summary>Gets or sets a value indicating whether the output is indented for readability. Defaults to <see langword="true"/>.</summary>
    public bool Indented { get; set; } = true;
}
