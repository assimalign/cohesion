namespace Assimalign.Cohesion.OpenApi.Versioning;

/// <summary>The severity of an <see cref="OpenApiTransformDiagnostic"/>.</summary>
public enum OpenApiTransformSeverity
{
    /// <summary>A construct was translated cleanly; the finding is informational.</summary>
    Information,

    /// <summary>A construct was translated with a loss of fidelity, or needs manual attention.</summary>
    Warning,

    /// <summary>A construct cannot be represented in the target version.</summary>
    Error
}

/// <summary>
/// A finding produced while transforming a document between OpenAPI lines: a clean conversion, a lossy
/// downgrade, or a construct that cannot translate and needs manual intervention.
/// </summary>
public sealed class OpenApiTransformDiagnostic
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiTransformDiagnostic"/> class.
    /// </summary>
    /// <param name="severity">The severity of the finding.</param>
    /// <param name="code">The stable diagnostic code.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="location">A JSON Pointer to the affected element.</param>
    public OpenApiTransformDiagnostic(OpenApiTransformSeverity severity, string code, string message, string location)
    {
        Severity = severity;
        Code = code;
        Message = message;
        Location = location;
    }

    /// <summary>Gets the severity of the finding.</summary>
    public OpenApiTransformSeverity Severity { get; }

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the human-readable message.</summary>
    public string Message { get; }

    /// <summary>Gets the JSON Pointer to the affected element.</summary>
    public string Location { get; }

    /// <inheritdoc/>
    public override string ToString() => $"[{Severity}] {Code} ({Location}): {Message}";
}
