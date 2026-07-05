namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>The severity of an <see cref="OpenApiMetadataDiagnostic"/>.</summary>
public enum OpenApiMetadataSeverity
{
    /// <summary>The metadata is invalid and cannot be mapped faithfully.</summary>
    Error,

    /// <summary>The metadata is usable but was adjusted or is questionable.</summary>
    Warning
}

/// <summary>
/// A finding raised while mapping OpenApi attributes to intermediate metadata: an invalid attribute
/// combination, a corrected value, or a questionable declaration. The source generator reports the same
/// findings as compiler diagnostics.
/// </summary>
public sealed class OpenApiMetadataDiagnostic
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiMetadataDiagnostic"/> class.
    /// </summary>
    /// <param name="severity">The severity of the finding.</param>
    /// <param name="code">The stable diagnostic code.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="target">The element the finding applies to, for example an operation path or type name.</param>
    public OpenApiMetadataDiagnostic(OpenApiMetadataSeverity severity, string code, string message, string target)
    {
        Severity = severity;
        Code = code;
        Message = message;
        Target = target;
    }

    /// <summary>Gets the severity of the finding.</summary>
    public OpenApiMetadataSeverity Severity { get; }

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the human-readable message.</summary>
    public string Message { get; }

    /// <summary>Gets the element the finding applies to.</summary>
    public string Target { get; }

    /// <inheritdoc/>
    public override string ToString() => $"[{Severity}] {Code} ({Target}): {Message}";
}
